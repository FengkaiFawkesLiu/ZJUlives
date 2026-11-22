using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ZJUSS.Core.Notes;

/// <summary>笔记模块共用的文本与日期处理；不记录或输出原文本。</summary>
internal static class NoteText
{
    private static readonly Regex SessionNumber = new(@"(\d+)", RegexOptions.Compiled);
    private static readonly Regex HeadingLine = new(@"^\s{0,3}#{1,6}\s", RegexOptions.Compiled);
    private static readonly Regex MarkdownImage = new(@"!\[[^\]]*\]\([^)]*\)", RegexOptions.Compiled);
    private static readonly Regex MarkdownLink = new(@"\[([^\]]*)\]\([^)]*\)", RegexOptions.Compiled);
    private static readonly Regex InlineCode = new(@"`+([^`]*)`+", RegexOptions.Compiled);
    private static readonly Regex Emphasis = new(@"[*_~>#]+", RegexOptions.Compiled);
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    public static bool TryParseLessonDate(string? text, out DateOnly date)
    {
        date = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var value = text.Trim();
        if (DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
        {
            return true;
        }

        return DateOnly.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out date) ||
               DateOnly.TryParse(value, CultureInfo.GetCultureInfo("zh-CN"), DateTimeStyles.None, out date);
    }

    public static int SessionOrder(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return int.MaxValue;
        }

        var match = SessionNumber.Match(text);
        return match.Success && int.TryParse(match.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : int.MaxValue;
    }

    /// <summary>导出时只转义 &lt; 和 &gt;，防止把危险 HTML 写入 Markdown 文件。</summary>
    public static string HtmlEscape(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        return text.Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal);
    }

    public static string BuildSummary(string? body, int maxLength = 80)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return string.Empty;
        }

        var keyPoints = ExtractSection(body, NoteSectionTitles.KeyPoints) ?? body;
        var lines = keyPoints.Split('\n');
        var builder = new StringBuilder();
        foreach (var rawLine in lines)
        {
            var line = rawLine;
            if (HeadingLine.IsMatch(line))
            {
                continue;
            }

            line = MarkdownImage.Replace(line, string.Empty);
            line = MarkdownLink.Replace(line, "$1");
            line = InlineCode.Replace(line, "$1");
            line = Emphasis.Replace(line, string.Empty);
            line = line.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (builder.Length > 0)
            {
                builder.Append(' ');
            }

            builder.Append(line);
            if (builder.Length >= maxLength)
            {
                break;
            }
        }

        var summary = Whitespace.Replace(builder.ToString(), " ").Trim();
        if (summary.Length > maxLength)
        {
            summary = summary[..maxLength].TrimEnd() + "…";
        }

        return summary;
    }

    public static string? ExtractSection(string body, string title)
    {
        var prefix = "## " + title;
        var start = body.IndexOf(prefix, StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        start += prefix.Length;
        if (start < body.Length && body[start] == '\r')
        {
            start++;
        }

        if (start < body.Length && body[start] == '\n')
        {
            start++;
        }

        var end = body.IndexOf("\n## ", start, StringComparison.Ordinal);
        var content = end < 0 ? body[start..] : body[start..end];
        return content.Trim();
    }
}
