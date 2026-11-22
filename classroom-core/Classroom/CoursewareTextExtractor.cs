using System.Diagnostics;
using System.Text;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Presentation;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using UglyToad.PdfPig.Exceptions;
using ZJUSS.Core.Logging;
using ZJUSS.Core.Messages;
using A = DocumentFormat.OpenXml.Drawing;

namespace ZJUSS.Core.Classroom;

/// <summary>
/// 课件文字本地提取：PDF 文本层（PdfPig，Apache-2.0）和 PPTX 幻灯片文字（Open XML SDK，MIT）。
/// 不做 OCR、不联网、不修改原文件、不缓存文字；失败返回 <see cref="CoursewareTextStatus.ExtractionFailed"/>，不抛出。
/// 只读取调用方给出的课堂内容根目录内文件；拒绝空路径、目录、缺失文件、符号链接和非课件扩展名。
/// </summary>
public sealed class CoursewareTextExtractor : ICoursewareTextExtractor
{
    private const string PdfFormat = "pdf";
    private const string PptxFormat = "pptx";
    private const string PptFormat = "ppt";
    private const string PageMarkerFormat = "[第{0}页]";
    private const string SlideMarkerFormat = "[幻灯片{0}]";

    private readonly IAppLogger _logger;

    public CoursewareTextExtractor(IAppLogger? logger = null)
    {
        _logger = logger ?? NullAppLogger.Instance;
    }

    public async Task<CoursewareTextResult> ExtractAsync(
        string fullPath,
        string? originalFileName = null,
        CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            var validation = ValidatePath(fullPath);
            if (validation is not null)
            {
                return LogAndReturn(validation, started);
            }

            var format = ResolveFormat(fullPath, originalFileName);
            if (format is null)
            {
                return LogAndReturn(Unsupported(
                    Path.GetExtension(fullPath).TrimStart('.').ToLowerInvariant(),
                    "不支持的课件格式。"), started);
            }

            if (format == PptFormat)
            {
                // 旧版二进制 PPT 不依赖本机 Office/LibreOffice，也不调用外部转换器。
                return LogAndReturn(Unsupported(PptFormat, "旧版 .ppt 格式不支持提取文字。"), started);
            }

            cancellationToken.ThrowIfCancellationRequested();
            var result = await Task.Run(
                    () => format == PdfFormat
                        ? ExtractPdf(fullPath, cancellationToken)
                        : ExtractPptx(fullPath, cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);
            return LogAndReturn(result, started);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (UnauthorizedAccessException)
        {
            return LogAndReturn(Failed("没有读取课件的权限。"), started);
        }
        catch (Exception)
        {
            // 任何库解析异常都不能成为未处理异常；不记录正文，也不记录完整路径。
            return LogAndReturn(Failed("课件无法读取。"), started);
        }
    }

    private static CoursewareTextResult? ValidatePath(string? fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath))
        {
            return Failed("课件路径为空。");
        }

        string? fileName;
        try
        {
            if (Directory.Exists(fullPath))
            {
                return Failed("课件路径不是文件。");
            }

            if (!File.Exists(fullPath))
            {
                return Failed("课件文件缺失。");
            }

            var attributes = File.GetAttributes(fullPath);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                // 不跟随符号链接/目录联接，避免读取课堂内容根目录以外的文件。
                return Failed("课件路径被拒绝。");
            }

            fileName = Path.GetFileName(fullPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Failed("课件文件无法访问。");
        }

        if (!ClassroomPathPolicy.TrySanitizeFileName(fileName, out _))
        {
            return Failed("课件文件名不合法。");
        }

        return null;
    }

    private static string? ResolveFormat(string fullPath, string? originalFileName)
    {
        var fromOriginal = ExtensionOf(originalFileName);
        if (IsKnownFormat(fromOriginal))
        {
            return fromOriginal;
        }

        var fromPath = ExtensionOf(fullPath);
        return IsKnownFormat(fromPath) ? fromPath : null;
    }

    private static string? ExtensionOf(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        var extension = Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant();
        return extension.Length == 0 ? null : extension;
    }

    private static bool IsKnownFormat(string? format) =>
        format is PdfFormat or PptxFormat or PptFormat;

    private CoursewareTextResult ExtractPdf(string fullPath, CancellationToken cancellationToken)
    {
        try
        {
            using var document = PdfDocument.Open(fullPath);
            var pageCount = document.NumberOfPages;
            var blocks = new List<string>(pageCount);
            var characterCount = 0;
            foreach (var page in document.GetPages())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var body = NormalizeText(ContentOrderTextExtractor.GetText(page));
                if (body.Length > 0)
                {
                    blocks.Add(string.Format(PageMarkerFormat, page.Number) + "\n" + body);
                    characterCount += body.Length;
                }
                else
                {
                    blocks.Add(string.Format(PageMarkerFormat, page.Number));
                }
            }

            if (characterCount == 0)
            {
                return new CoursewareTextResult
                {
                    Status = CoursewareTextStatus.NoTextLayer,
                    Text = null,
                    Format = PdfFormat,
                    PageCount = pageCount,
                    CharacterCount = 0,
                    FailureReason = "PDF 没有可提取的文字层。"
                };
            }

            var text = string.Join("\n", blocks);
            return new CoursewareTextResult
            {
                Status = CoursewareTextStatus.Available,
                Text = text,
                Format = PdfFormat,
                PageCount = pageCount,
                CharacterCount = text.Length
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (PdfDocumentEncryptedException)
        {
            return Failed("PDF 已加密，无法提取文字。", PdfFormat);
        }
        catch (PdfDocumentFormatException)
        {
            return Failed("PDF 损坏或格式不正确。", PdfFormat);
        }
        catch (UnauthorizedAccessException)
        {
            return Failed("没有读取课件的权限。", PdfFormat);
        }
        catch (IOException)
        {
            return Failed("课件文件无法读取。", PdfFormat);
        }
        catch (Exception)
        {
            return Failed("PDF 解析失败。", PdfFormat);
        }
    }

    private CoursewareTextResult ExtractPptx(string fullPath, CancellationToken cancellationToken)
    {
        try
        {
            using var document = PresentationDocument.Open(fullPath, isEditable: false);
            var presentationPart = document.PresentationPart;
            var slideIdList = presentationPart?.Presentation?.SlideIdList;
            if (presentationPart is null || slideIdList is null)
            {
                return Failed("演示文稿结构不完整。", PptxFormat);
            }

            var blocks = new List<string>();
            var characterCount = 0;
            var slideCount = 0;
            foreach (var slideId in slideIdList.Elements<SlideId>())
            {
                cancellationToken.ThrowIfCancellationRequested();
                slideCount++;
                string body;
                if (string.IsNullOrWhiteSpace(slideId.RelationshipId?.Value) ||
                    presentationPart.GetPartById(slideId.RelationshipId!.Value!) is not SlidePart slidePart ||
                    slidePart.Slide is null)
                {
                    body = string.Empty;
                }
                else
                {
                    body = NormalizeText(ExtractSlideText(slidePart.Slide));
                }

                if (body.Length > 0)
                {
                    blocks.Add(string.Format(SlideMarkerFormat, slideCount) + "\n" + body);
                    characterCount += body.Length;
                }
                else
                {
                    blocks.Add(string.Format(SlideMarkerFormat, slideCount));
                }
            }

            if (characterCount == 0)
            {
                return new CoursewareTextResult
                {
                    Status = CoursewareTextStatus.NoTextLayer,
                    Text = null,
                    Format = PptxFormat,
                    PageCount = slideCount,
                    CharacterCount = 0,
                    FailureReason = "演示文稿没有可提取的文字。"
                };
            }

            var text = string.Join("\n", blocks);
            return new CoursewareTextResult
            {
                Status = CoursewareTextStatus.Available,
                Text = text,
                Format = PptxFormat,
                PageCount = slideCount,
                CharacterCount = text.Length
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (OpenXmlPackageException)
        {
            return Failed("演示文稿损坏或格式不正确。", PptxFormat);
        }
        catch (UnauthorizedAccessException)
        {
            return Failed("没有读取课件的权限。", PptxFormat);
        }
        catch (IOException)
        {
            return Failed("课件文件无法读取。", PptxFormat);
        }
        catch (Exception)
        {
            return Failed("演示文稿解析失败。", PptxFormat);
        }
    }

    /// <summary>按幻灯片内的段落顺序拼接 a:t 文字；同一段落内的 Run 直接相连，不额外插入空格。</summary>
    private static string ExtractSlideText(Slide slide)
    {
        var lines = new List<string>();
        foreach (var paragraph in slide.Descendants<A.Paragraph>())
        {
            var text = string.Concat(paragraph.Descendants<A.Text>().Select(item => item.Text));
            if (!string.IsNullOrWhiteSpace(text))
            {
                lines.Add(text);
            }
        }

        if (lines.Count == 0)
        {
            // 极端结构：文字不在段落中时仍然按文档顺序提取 a:t。
            var fallback = string.Concat(slide.Descendants<A.Text>().Select(item => item.Text));
            if (!string.IsNullOrWhiteSpace(fallback))
            {
                lines.Add(fallback);
            }
        }

        return string.Join("\n", lines);
    }

    /// <summary>统一换行、去掉行首尾空白和空行；不删除、不改写任何非空白字符。</summary>
    private static string NormalizeText(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var normalized = value.Replace("\r\n", "\n").Replace('\r', '\n').Replace('\u00A0', ' ');
        var builder = new StringBuilder(normalized.Length);
        var lines = normalized.Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index].Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (builder.Length > 0)
            {
                builder.Append('\n');
            }

            builder.Append(line);
        }

        return builder.ToString();
    }

    private CoursewareTextResult LogAndReturn(CoursewareTextResult result, long started)
    {
        try
        {
            var elapsed = Stopwatch.GetElapsedTime(started);
            var line =
                $"阶段=课件文字; 格式={result.Format ?? "未知"}; 状态={StatusLabel(result.Status)}; " +
                $"页数={result.PageCount?.ToString() ?? "未知"}; 字符数={result.CharacterCount}; " +
                $"耗时={elapsed.TotalMilliseconds:F0}毫秒";
            if (result.Status == CoursewareTextStatus.Available)
            {
                _logger.Info(ClassroomMessages.Module, line);
            }
            else
            {
                _logger.Warn(ClassroomMessages.Module, line);
            }
        }
        catch (Exception)
        {
            // 日志失败不影响提取结果。
        }

        return result;
    }

    private static string StatusLabel(CoursewareTextStatus status) => status switch
    {
        CoursewareTextStatus.Available => "可提取",
        CoursewareTextStatus.NoTextLayer => "无文字层",
        CoursewareTextStatus.UnsupportedFormat => "格式不支持",
        CoursewareTextStatus.ExtractionFailed => "提取失败",
        CoursewareTextStatus.NotRead => "未读取",
        CoursewareTextStatus.MultipleCoursewareUnconfirmed => "多课件未确认",
        _ => status.ToString()
    };

    private static CoursewareTextResult Unsupported(string? format, string reason) => new()
    {
        Status = CoursewareTextStatus.UnsupportedFormat,
        Text = null,
        Format = format,
        PageCount = null,
        CharacterCount = 0,
        FailureReason = reason
    };

    private static CoursewareTextResult Failed(string reason, string? format = null) => new()
    {
        Status = CoursewareTextStatus.ExtractionFailed,
        Text = null,
        Format = format,
        PageCount = null,
        CharacterCount = 0,
        FailureReason = reason
    };
}
