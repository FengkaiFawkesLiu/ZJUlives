using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ZJUSS.Core.Classroom;

/// <summary>
/// 课堂内容本机存储：索引、语音文字、课件文件都在课堂内容根目录内，
/// 独立于学习数据与备份；不加密、不自动清理、不提供删除。
/// </summary>
public sealed class LocalClassroomContentStore : IClassroomContentStore
{
    private const string IndexFileName = "index.json";
    private const string TranscriptFileName = "transcript.txt";
    private const string PptDirectoryName = "ppt";
    private const string FallbackPptName = "课件.pdf";

    private readonly ClassroomPathPolicy _paths;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public LocalClassroomContentStore(string rootDirectory)
    {
        _paths = new ClassroomPathPolicy(rootDirectory);
    }

    public string RootDirectory => _paths.RootDirectory;

    private string IndexPath => Path.Combine(RootDirectory, IndexFileName);

    public async Task<ClassroomIndexLoadResult> LoadIndexAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(IndexPath))
            {
                return new ClassroomIndexLoadResult
                {
                    Status = ClassroomIndexLoadStatus.Missing,
                    Index = new ClassroomIndex()
                };
            }

            string json;
            try
            {
                json = await File.ReadAllTextAsync(IndexPath, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return Corrupted("课堂内容索引无法读取，文件保持不变。");
            }

            int version;
            try
            {
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object ||
                    !root.TryGetProperty("version", out var versionElement) ||
                    !versionElement.TryGetInt32(out version))
                {
                    return Corrupted("课堂内容索引缺少版本信息。");
                }

                if (version != ClassroomIndex.CurrentVersion)
                {
                    return new ClassroomIndexLoadResult
                    {
                        Status = ClassroomIndexLoadStatus.Incompatible,
                        Detail = "课堂内容索引版本不兼容。"
                    };
                }

                var index = JsonSerializer.Deserialize<ClassroomIndex>(json, ClassroomJson.Options);
                if (index is null || index.Courses is null)
                {
                    return Corrupted("课堂内容索引结构不完整。");
                }

                return new ClassroomIndexLoadResult
                {
                    Status = ClassroomIndexLoadStatus.Ok,
                    Index = index
                };
            }
            catch (Exception exception) when (exception is JsonException or NotSupportedException)
            {
                return Corrupted("课堂内容索引无法解析，文件保持不变。");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveIndexAsync(ClassroomIndex index, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(index);
        if (index.Courses is null)
        {
            throw new ClassroomStorageException(ClassroomStorageError.IndexCorrupted, "课堂内容索引结构不完整。");
        }

        byte[] json;
        try
        {
            json = JsonSerializer.SerializeToUtf8Bytes(index, ClassroomJson.Options);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            throw new ClassroomStorageException(
                ClassroomStorageError.IndexCorrupted,
                "课堂内容索引无法序列化。",
                exception);
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await WriteAtomicAsync(IndexPath, json, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task SaveTranscriptAsync(
        string classroomCourseId,
        string lessonId,
        string transcript,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transcript);
        var path = TranscriptPath(classroomCourseId, lessonId);
        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(transcript);
        return WriteAtomicAsync(path, bytes, cancellationToken);
    }

    public async Task<string> ReadTranscriptAsync(
        string classroomCourseId,
        string lessonId,
        CancellationToken cancellationToken = default)
    {
        var path = TranscriptPath(classroomCourseId, lessonId);
        try
        {
            return await File.ReadAllTextAsync(path, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new ClassroomStorageException(
                ClassroomStorageError.FileMissing,
                "这一节课的语音文字没有保存在本机。",
                exception);
        }
    }

    public async Task<ClassroomPptFile> SavePptAsync(
        string classroomCourseId,
        string lessonId,
        ClassroomPptFile file,
        Stream content,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(content);

        var directory = PptDirectory(classroomCourseId, lessonId);
        Directory.CreateDirectory(directory);

        var safeName = ClassroomPathPolicy.TrySanitizeFileName(file.OriginalFileName, out var sanitized)
            ? sanitized
            : FallbackPptName;
        var existing = Directory.EnumerateFiles(directory)
            .Select(path => Path.GetFileName(path))
            .ToArray();
        var uniqueName = ClassroomPathPolicy.CreateUniqueFileName(safeName, existing);
        var fullPath = Path.Combine(directory, uniqueName);
        var relativePath = Path.GetRelativePath(RootDirectory, fullPath).Replace('\\', '/');

        await using (var target = new FileStream(
            fullPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            81920,
            FileOptions.Asynchronous))
        {
            try
            {
                await content.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
                await target.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                target.Close();
                TryDelete(fullPath);
                throw;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                target.Close();
                TryDelete(fullPath);
                throw new ClassroomStorageException(
                    Classify(exception),
                    "课件文件无法保存。",
                    exception);
            }
        }

        return new ClassroomPptFile
        {
            FileId = file.FileId,
            OriginalFileName = file.OriginalFileName ?? uniqueName,
            RelativePath = relativePath,
            SizeBytes = new FileInfo(fullPath).Length,
            SavedAt = DateTimeOffset.Now,
            PageCount = file.PageCount,
            Status = ClassroomContentStatus.Read,
            FailureReason = null
        };
    }

    public string ResolvePptPath(ClassroomPptFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (!_paths.TryResolveUnderRoot(file.RelativePath, out var fullPath))
        {
            throw new ClassroomStorageException(
                ClassroomStorageError.PathRejected,
                "课件路径不在课堂内容目录内。");
        }

        if (!File.Exists(fullPath))
        {
            throw new ClassroomStorageException(
                ClassroomStorageError.FileMissing,
                "课件文件已不存在。");
        }

        return fullPath;
    }

    private string TranscriptPath(string classroomCourseId, string lessonId)
    {
        var courseKey = KeyOf(classroomCourseId);
        var lessonKey = KeyOf(lessonId);
        return Path.Combine(
            RootDirectory,
            "courses",
            courseKey,
            "lessons",
            lessonKey,
            TranscriptFileName);
    }

    private string PptDirectory(string classroomCourseId, string lessonId) =>
        Path.Combine(
            RootDirectory,
            "courses",
            KeyOf(classroomCourseId),
            "lessons",
            KeyOf(lessonId),
            PptDirectoryName);

    /// <summary>平台标识不进路径：用 SHA-256 前 16 字节十六进制作为稳定目录名。</summary>
    private static string KeyOf(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ClassroomStorageException(ClassroomStorageError.PathRejected, "课程或课节标识为空。");
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexStringLower(hash)[..32];
    }

    private static async Task WriteAtomicAsync(
        string path,
        byte[] data,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(directory))
        {
            throw new ClassroomStorageException(ClassroomStorageError.PathRejected, "课堂内容路径无效。");
        }

        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                81920,
                FileOptions.Asynchronous))
            {
                await stream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporary, path, overwrite: true);
        }
        catch (OperationCanceledException)
        {
            TryDelete(temporary);
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            TryDelete(temporary);
            throw new ClassroomStorageException(Classify(exception), "课堂内容无法写入本机。", exception);
        }
    }

    private static ClassroomStorageError Classify(Exception exception) => exception switch
    {
        UnauthorizedAccessException => ClassroomStorageError.AccessDenied,
        IOException io when io.HResult is 0x70 or 0x27 or 112 => ClassroomStorageError.DiskFull,
        IOException => ClassroomStorageError.WriteFailed,
        _ => ClassroomStorageError.Unknown
    };

    private static ClassroomIndexLoadResult Corrupted(string detail) => new()
    {
        Status = ClassroomIndexLoadStatus.Corrupted,
        Detail = detail
    };

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 清理失败不影响主流程，临时文件可能保留。
        }
    }
}
