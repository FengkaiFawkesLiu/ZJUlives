using System.Text;
using ZJUSS.Core.Classroom;
using ZJUSS.Core.Messages;

namespace ZJUSS.Core.Logging;

/// <summary>
/// 智云课堂读取的脱敏日志字段。只包含模块、阶段、课程/课节标识、内容类别、状态、
/// 失败类别、文件数量和耗时；按构造方式不包含语音文字、PPT 内容或账号密码。
/// </summary>
public sealed class ClassroomLogRecord
{
    public required string Phase { get; init; }
    public string? ClassroomCourseId { get; init; }
    public string? LessonId { get; init; }
    public string? ContentKind { get; init; }
    public ClassroomContentStatus? Status { get; init; }
    public ClassroomFailureReason? Failure { get; init; }
    public int? FileCount { get; init; }
    public TimeSpan? Elapsed { get; init; }
}

public static class ClassroomLog
{
    private const int MaxIdentifierLength = 64;

    public static string Format(ClassroomLogRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        var parts = new List<string> { "阶段=" + SafeText(record.Phase) };
        Add(parts, "课程", record.ClassroomCourseId);
        Add(parts, "课节", record.LessonId);
        if (!string.IsNullOrWhiteSpace(record.ContentKind))
        {
            parts.Add("内容=" + ClassroomMessages.ContentLabel(record.ContentKind));
        }

        if (record.Status is { } status)
        {
            parts.Add("状态=" + ClassroomMessages.StatusLabel(status));
        }

        if (record.Failure is { } failure && failure != ClassroomFailureReason.None)
        {
            parts.Add("类别=" + failure);
        }

        if (record.FileCount is { } fileCount)
        {
            parts.Add("文件数=" + fileCount);
        }

        if (record.Elapsed is { } elapsed)
        {
            parts.Add($"耗时={elapsed.TotalMilliseconds:F0}毫秒");
        }

        var line = $"{ClassroomMessages.Module}: {string.Join("; ", parts)}";
        return line;
    }

    private static void Add(List<string> parts, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            parts.Add(label + "=" + SafeText(value));
        }
    }

    /// <summary>
    /// 标识符只保留字母、数字、连字符、下划线和点，并限制长度，避免把任意文本写进日志；
    /// 形似账号、密码、Cookie 或令牌的片段整段隐藏。
    /// </summary>
    private static string SafeText(string value)
    {
        var builder = new StringBuilder(Math.Min(value.Length, MaxIdentifierLength));
        foreach (var character in value)
        {
            if (builder.Length >= MaxIdentifierLength)
            {
                builder.Append('…');
                break;
            }

            builder.Append(char.IsLetterOrDigit(character) || character is '-' or '_' or '.'
                ? character
                : '_');
        }

        var text = builder.ToString();
        foreach (var marker in SensitiveMarkers)
        {
            if (text.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return "[已隐藏]";
            }
        }

        return text;
    }

    private static readonly string[] SensitiveMarkers =
    {
        "password", "passwd", "pwd", "cookie", "token", "secret", "credential", "account", "密码", "账号"
    };
}
