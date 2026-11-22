namespace ZJUSS.Core.Classroom;

/// <summary>
/// 课堂内容存储的路径约束：只允许访问课堂内容根目录内的文件。
/// 拒绝绝对路径、盘符、`..` 段、根目录外路径和空文件名；同名文件使用稳定后缀，不覆盖。
/// </summary>
public sealed class ClassroomPathPolicy
{
    private const int MaxFileNameLength = 120;

    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    public ClassroomPathPolicy(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        RootDirectory = Path.GetFullPath(rootDirectory);
    }

    public string RootDirectory { get; }

    /// <summary>把课堂内容根目录内的相对路径解析为完整路径；不合法或越界时返回 false。</summary>
    public bool TryResolveUnderRoot(string? relativePath, out string fullPath)
    {
        fullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return false;
        }

        var candidate = relativePath.Replace('\\', '/');
        if (candidate.StartsWith('/') || Path.IsPathRooted(relativePath) || HasDriveSpecifier(candidate))
        {
            return false;
        }

        var segments = candidate.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return false;
        }

        foreach (var segment in segments)
        {
            if (segment is "." or "..")
            {
                return false;
            }

            if (segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                return false;
            }
        }

        var combined = Path.GetFullPath(Path.Combine(RootDirectory, Path.Combine(segments)));
        var rootWithSeparator = RootDirectory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!combined.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.Equals(combined, RootDirectory, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        fullPath = combined;
        return true;
    }

    /// <summary>
    /// 按平台原始文件名生成可安全落盘的文件名：去掉目录部分、替换非法字符、处理保留设备名、
    /// 去掉结尾的点和空格，并限制长度。空文件名或只剩扩展名时返回 false。
    /// </summary>
    public static bool TrySanitizeFileName(string? originalFileName, out string safeFileName)
    {
        safeFileName = string.Empty;
        if (string.IsNullOrWhiteSpace(originalFileName))
        {
            return false;
        }

        var normalized = originalFileName.Replace('\\', '/');
        var lastSlash = normalized.LastIndexOf('/');
        if (lastSlash >= 0)
        {
            normalized = normalized[(lastSlash + 1)..];
        }

        var builder = new System.Text.StringBuilder(normalized.Length);
        foreach (var character in normalized)
        {
            builder.Append(char.IsControl(character) || Path.GetInvalidFileNameChars().Contains(character)
                ? '_'
                : character);
        }

        var name = builder.ToString().Trim().TrimEnd('.', ' ');
        if (name.Length == 0 || name.All(character => character == '.'))
        {
            return false;
        }

        var stem = Path.GetFileNameWithoutExtension(name);
        if (stem.Length == 0)
        {
            return false;
        }

        if (ReservedDeviceNames.Contains(stem))
        {
            name = "_" + name;
        }

        if (name.Length > MaxFileNameLength)
        {
            var extension = Path.GetExtension(name);
            if (extension.Length >= MaxFileNameLength - 1)
            {
                name = name[..MaxFileNameLength];
            }
            else
            {
                var allowedStemLength = MaxFileNameLength - extension.Length;
                name = name[..allowedStemLength] + extension;
            }
        }

        if (name.Length == 0)
        {
            return false;
        }

        safeFileName = name;
        return true;
    }

    /// <summary>在同一目录已有文件名中生成不覆盖的唯一文件名：`name (2).ext`、`name (3).ext`…</summary>
    public static string CreateUniqueFileName(string desiredFileName, IEnumerable<string> existingFileNames)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(desiredFileName);
        ArgumentNullException.ThrowIfNull(existingFileNames);

        var existing = new HashSet<string>(existingFileNames, StringComparer.OrdinalIgnoreCase);
        if (!existing.Contains(desiredFileName))
        {
            return desiredFileName;
        }

        var stem = Path.GetFileNameWithoutExtension(desiredFileName);
        var extension = Path.GetExtension(desiredFileName);
        for (var index = 2; index < 100000; index++)
        {
            var candidate = $"{stem} ({index}){extension}";
            if (!existing.Contains(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("无法生成唯一的课堂文件名。");
    }

    private static bool HasDriveSpecifier(string candidate)
    {
        if (candidate.Length < 2 || candidate[1] != ':')
        {
            return false;
        }

        return char.IsAsciiLetter(candidate[0]);
    }
}
