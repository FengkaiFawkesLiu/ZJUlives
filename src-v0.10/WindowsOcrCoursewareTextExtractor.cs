using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using System.IO;
using Windows.Data.Pdf;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;
using ZJUSS.Core.Classroom;
using ZJUSS.Core.Logging;
using ZJUSS.Core.Messages;

namespace Shike;

/// <summary>
/// 课件文字提取：先用 Core 的 PDF 文本层/PPTX 提取；只有 PDF 没有文本层时，
/// 才在本机用 Windows 自带 OCR 识别幻灯片图片。全程不联网、不上传、不修改原文件。
/// 文本 PDF/PPTX 的原有路径完全不受影响。
/// </summary>
public sealed class WindowsOcrCoursewareTextExtractor : ICoursewareTextExtractor
{
    private const string PdfFormat = "pdf";
    private readonly CoursewareTextExtractor _textLayerExtractor;
    private readonly IAppLogger _logger;

    public WindowsOcrCoursewareTextExtractor(IAppLogger? logger = null)
    {
        _logger = logger ?? NullAppLogger.Instance;
        _textLayerExtractor = new CoursewareTextExtractor(_logger);
    }

    public async Task<CoursewareTextResult> ExtractAsync(
        string fullPath,
        string? originalFileName = null,
        CancellationToken cancellationToken = default)
    {
        var result = await _textLayerExtractor
            .ExtractAsync(fullPath, originalFileName, cancellationToken)
            .ConfigureAwait(false);

        if (result.Status != CoursewareTextStatus.NoTextLayer ||
            !string.Equals(result.Format, PdfFormat, StringComparison.OrdinalIgnoreCase))
        {
            return result;
        }

        return await TryOcrPdfAsync(fullPath, result.PageCount ?? 1, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<CoursewareTextResult> TryOcrPdfAsync(
        string fullPath,
        int pageCount,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            var engine = CreateEngine();
            if (engine is null)
            {
                return Failed(
                    CoursewareTextStatus.NoTextLayer,
                    pageCount,
                    "系统没有可用的中文 OCR 语言包。");
            }

            // WPF 不是 UWP 打包应用，StorageFile.GetFileFromPathAsync 会拒绝普通文件路径；
            // 直接读入内存流，再交给 WinRT PDF 渲染，避免 broadFileSystemAccess 限制。
            var bytes = await File.ReadAllBytesAsync(fullPath, cancellationToken).ConfigureAwait(false);
            using var pdfStream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(pdfStream.GetOutputStreamAt(0)))
            {
                writer.WriteBytes(bytes);
                await writer.StoreAsync();
                await writer.FlushAsync();
                writer.DetachStream();
            }

            pdfStream.Seek(0);
            var document = await PdfDocument.LoadFromStreamAsync(pdfStream);
            var pages = new List<string>((int)document.PageCount);
            var characterCount = 0;
            for (uint index = 0; index < document.PageCount; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var page = document.GetPage(index);
                using var stream = new InMemoryRandomAccessStream();
                var width = (uint)Math.Clamp(page.Size.Width * 3.0, 1000, 3200);
                await page.RenderToStreamAsync(
                    stream,
                    new PdfPageRenderOptions { DestinationWidth = width });
                stream.Seek(0);

                var decoder = await BitmapDecoder.CreateAsync(stream);
                using var bitmap = await decoder.GetSoftwareBitmapAsync();
                var recognized = await engine.RecognizeAsync(bitmap);
                var text = NormalizeOcrText(
                    string.Join(
                        "\n",
                        recognized.Lines
                            .Select(line => line.Text.Trim())
                            .Where(line => line.Length > 0)));
                if (text.Length > 0)
                {
                    pages.Add($"[第{index + 1}页]\n{text}");
                    characterCount += text.Length;
                }
                else
                {
                    pages.Add($"[第{index + 1}页]");
                }
            }

            if (characterCount == 0)
            {
                return Failed(
                    CoursewareTextStatus.NoTextLayer,
                    pageCount,
                    "本机 OCR 没有识别到文字。");
            }

            var body = string.Join("\n", pages);
            Log(pageCount, body.Length, Stopwatch.GetElapsedTime(started));
            return new CoursewareTextResult
            {
                Status = CoursewareTextStatus.Available,
                Text = body,
                Format = PdfFormat,
                PageCount = pageCount,
                CharacterCount = body.Length,
                UsedOcr = true
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.Warn(
                ClassroomMessages.Module,
                $"阶段=课件文字OCR; 状态=识别失败; 页数={pageCount}; " +
                $"类型={exception.GetType().Name}; HResult=0x{exception.HResult:X8}");
            return Failed(
                CoursewareTextStatus.ExtractionFailed,
                pageCount,
                "本机 OCR 识别失败。");
        }
    }

    /// <summary>Windows OCR 会在中文字符之间插入空格；只去掉中文/中文标点之间的空格，保留代码和英文空格。</summary>
    private static string NormalizeOcrText(string text) =>
        System.Text.RegularExpressions.Regex.Replace(
            text,
            @"(?<=[\u4e00-\u9fff，。；：！？、（）《》“”‘’])\s+(?=[\u4e00-\u9fff，。；：！？、（）《》“”‘’])",
            string.Empty);

    private static OcrEngine? CreateEngine()
    {
        foreach (var tag in new[] { "zh-Hans", "zh-CN" })
        {
            var engine = OcrEngine.TryCreateFromLanguage(new Language(tag));
            if (engine is not null)
            {
                return engine;
            }
        }

        return OcrEngine.TryCreateFromUserProfileLanguages();
    }

    private void Log(int pageCount, int characterCount, TimeSpan elapsed)
    {
        try
        {
            _logger.Info(
                ClassroomMessages.Module,
                $"阶段=课件文字OCR; 状态=可提取; 页数={pageCount}; 字符数={characterCount}; " +
                $"耗时={elapsed.TotalMilliseconds:F0}毫秒");
        }
        catch (Exception)
        {
            // 日志失败不影响提取结果。
        }
    }

    private static CoursewareTextResult Failed(
        CoursewareTextStatus status,
        int pageCount,
        string reason) => new()
        {
            Status = status,
            Text = null,
            Format = PdfFormat,
            PageCount = pageCount,
            CharacterCount = 0,
            FailureReason = reason
        };
}

