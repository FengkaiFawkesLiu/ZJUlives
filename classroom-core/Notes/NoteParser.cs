using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ZJUSS.Core.Classroom;

namespace ZJUSS.Core.Notes;

/// <summary>解析一次 API 返回所需的课程与材料信息；不含材料原文。</summary>
public sealed class NoteParseContext
{
    public required string CourseName { get; init; }
    public required string Term { get; init; }
    public string? LessonDateText { get; init; }
    public string? SessionText { get; init; }
    public ClassroomMatchKind MatchKind { get; init; } = ClassroomMatchKind.Matched;
    public bool RemovedFromPlatform { get; init; }
    public bool UsedCoursewareText { get; init; }
    public bool UsedTranscript { get; init; }
    public bool CoursewarePresent { get; init; }
    public bool CoursewareTextFromOcr { get; init; }
    public CoursewareTextStatus CoursewareTextStatus { get; init; } = CoursewareTextStatus.NotRead;
    public DateTimeOffset GeneratedAt { get; init; }
}

public sealed class NoteParseResult
{
    public bool Success { get; init; }
    public string? FailureDetail { get; init; }
    public string Title { get; init; } = string.Empty;
    public string BodyMarkdown { get; init; } = string.Empty;
    public IReadOnlyList<NoteCandidate> Candidates { get; init; } = Array.Empty<NoteCandidate>();

    /// <summary>候选 JSON 缺失或格式错误；笔记仍可保存，候选为空。</summary>
    public bool CandidateExtractionFailed { get; init; }

    public string? CandidateWarning { get; init; }
    public int SkippedCandidateCount { get; init; }
}

/// <summary>
/// 把 API 返回的 Markdown 原文整理为固定六节，并提取末尾 json 候选块。
/// 正文为空或没有任何可识别的固定小节时失败；候选失败不影响正文。
/// </summary>
public static class NoteParser
{
    private static readonly Regex HeadingPattern = new(
        @"^[ \t]{0,3}(?<hashes>#{1,6})[ \t]*(?<text>.+?)[ \t]*$",
        RegexOptions.Compiled);

    private static readonly Regex FencePattern = new(
        @"[ \t]*```(?<lang>[A-Za-z0-9_+-]*)[ \t]*\r?\n(?<body>.*?)```",
        RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Dictionary<string, string> CanonicalTitles = BuildCanonicalTitles();

    private const string FillerNotMentioned = "材料未提及";
    private const string FillerNone = "暂无";

    public static NoteParseResult Parse(string? content, NoteParseContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (string.IsNullOrWhiteSpace(content))
        {
            return Failure("API 返回内容为空，未保存笔记。");
        }

        var text = content.Replace("\r\n", "\n", StringComparison.Ordinal);
        var withoutCandidates = RemoveCandidateBlock(text, out var candidateJson, out var candidateBlockFound);

        if (string.IsNullOrWhiteSpace(withoutCandidates))
        {
            return Failure("API 返回内容为空，未保存笔记。");
        }

        var sections = ExtractSections(withoutCandidates, out var extra, out var recognizedCount);
        if (recognizedCount == 0)
        {
            return Failure("API 返回内容没有可识别的笔记小节，未保存笔记。");
        }

        var title = BuildTitle(context);
        var usedCourseware = context.UsedCoursewareText;
        var usedTranscript = context.UsedTranscript;

        if (!usedCourseware && !usedTranscript)
        {
            return Failure("本节没有可用于生成笔记的课件文字或语音文字。");
        }

        var keyPoints = SectionOr(sections, NoteSectionTitles.KeyPoints, FillerNotMentioned);
        var detailed = SectionOr(sections, NoteSectionTitles.DetailedExplanation, FillerNotMentioned);
        if (!string.IsNullOrWhiteSpace(extra))
        {
            detailed = string.IsNullOrWhiteSpace(detailed) || detailed == FillerNotMentioned
                ? extra.Trim()
                : detailed.Trim() + "\n\n" + extra.Trim();
        }

        var examples = SectionOr(sections, NoteSectionTitles.ExamplesAndReminders, FillerNotMentioned);
        var todos = SectionOr(sections, NoteSectionTitles.TodoItems, FillerNone);

        var body = BuildBody(title, context, keyPoints, detailed, examples, todos);

        var candidateResult = candidateBlockFound
            ? ParseCandidates(candidateJson, context)
            : CandidateParseOutcome.Failed;

        return new NoteParseResult
        {
            Success = true,
            Title = title,
            BodyMarkdown = body,
            Candidates = candidateResult.Candidates,
            CandidateExtractionFailed = candidateBlockFound && candidateResult.Success
                ? false
                : true,
            CandidateWarning = candidateBlockFound && candidateResult.Success
                ? (candidateResult.Skipped > 0 ? "部分候选格式不完整，已跳过。" : null)
                : "待办提取失败",
            SkippedCandidateCount = candidateResult.Skipped
        };
    }

    private static NoteParseResult Failure(string detail) => new()
    {
        Success = false,
        FailureDetail = detail,
        CandidateExtractionFailed = true
    };

    private static string BuildTitle(NoteParseContext context)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(context.CourseName))
        {
            parts.Add(context.CourseName.Trim());
        }

        if (!string.IsNullOrWhiteSpace(context.LessonDateText))
        {
            parts.Add(context.LessonDateText.Trim());
        }

        if (!string.IsNullOrWhiteSpace(context.SessionText))
        {
            parts.Add(context.SessionText.Trim());
        }

        return parts.Count == 0 ? "课程笔记" : string.Join(" ", parts);
    }

    private static string BuildBody(
        string title,
        NoteParseContext context,
        string keyPoints,
        string detailed,
        string examples,
        string todos)
    {
        var builder = new StringBuilder();
        builder.Append("# ").Append(title).Append("\n\n");

        builder.Append("## ").Append(NoteSectionTitles.LessonInfo).Append('\n');
        builder.Append("- 课程名称：").Append(ValueOrUnknown(context.CourseName)).Append('\n');
        builder.Append("- 学期：").Append(ValueOrUnknown(context.Term)).Append('\n');
        builder.Append("- 课节日期：").Append(ValueOrUnknown(context.LessonDateText)).Append('\n');
        builder.Append("- 节次：").Append(ValueOrUnknown(context.SessionText)).Append('\n');
        builder.Append("- 生成时间：")
            .Append(context.GeneratedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture))
            .Append('\n');
        builder.Append("- 使用材料：").Append(MaterialLabel(context)).Append('\n');
        if (context.MatchKind == ClassroomMatchKind.Unmatched)
        {
            builder.Append("- 匹配状态：未匹配（智云课堂课程）\n");
        }

        if (context.RemovedFromPlatform)
        {
            builder.Append("- 平台状态：平台已移除，本笔记依据本机保存的材料生成\n");
        }

        builder.Append('\n');

        builder.Append("## ").Append(NoteSectionTitles.KeyPoints).Append('\n')
            .Append(keyPoints.Trim()).Append("\n\n");
        builder.Append("## ").Append(NoteSectionTitles.DetailedExplanation).Append('\n')
            .Append(detailed.Trim()).Append("\n\n");
        builder.Append("## ").Append(NoteSectionTitles.ExamplesAndReminders).Append('\n')
            .Append(examples.Trim()).Append("\n\n");
        builder.Append("## ").Append(NoteSectionTitles.TodoItems).Append('\n')
            .Append(todos.Trim()).Append("\n\n");
        builder.Append("## ").Append(NoteSectionTitles.MaterialNotes).Append('\n')
            .Append(BuildMaterialNotes(context).Trim()).Append('\n');

        return builder.ToString();
    }

    private static string ValueOrUnknown(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "未提供" : value.Trim();

    private static string MaterialLabel(NoteParseContext context)
    {
        if (context.UsedCoursewareText && context.UsedTranscript)
        {
            return "课件文字和语音文字";
        }

        if (context.UsedCoursewareText)
        {
            return "课件文字";
        }

        return context.UsedTranscript ? "语音文字" : "无";
    }

    private static string BuildMaterialNotes(NoteParseContext context)
    {
        string message;
        if (context.UsedCoursewareText && context.UsedTranscript)
        {
            message = "本节使用课件文字和语音文字生成。";
        }
        else if (context.UsedCoursewareText)
        {
            message = "本节仅使用课件文字生成；本节没有语音文字。";
        }
        else if (context.UsedTranscript)
        {
            message = context.CoursewareTextStatus is CoursewareTextStatus.NoTextLayer or
                CoursewareTextStatus.UnsupportedFormat or
                CoursewareTextStatus.ExtractionFailed or
                CoursewareTextStatus.MultipleCoursewareUnconfirmed
                ? "本节仅使用语音文字生成；课件没有可提取的文字。"
                : context.CoursewarePresent
                    ? "本节仅使用语音文字生成；课件文字不可用。"
                    : "本节仅使用语音文字生成；本节没有可用的课件文字。";
        }
        else
        {
            message = "本节没有可用于生成笔记的课件文字或语音文字。";
        }

        if (context.CoursewareTextFromOcr && context.UsedCoursewareText)
        {
            message += " 课件文字由本机 OCR 识别，可能存在错字或遗漏，请以原课件为准。";
        }

        if (context.RemovedFromPlatform)
        {
            message += " 课节已从平台移除，本笔记依据本机保存的材料生成。";
        }

        return message;
    }

    private static string SectionOr(
        IReadOnlyDictionary<string, string> sections,
        string title,
        string fallback) =>
        sections.TryGetValue(title, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : fallback;

    private static Dictionary<string, string> ExtractSections(
        string text,
        out string extra,
        out int recognizedCount)
    {
        var sections = new Dictionary<string, StringBuilder>(StringComparer.Ordinal);
        var extraBuilder = new StringBuilder();
        string? current = null;
        var inExtra = false;
        var inCodeFence = false;
        var count = 0;

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd();
            var trimmed = line.TrimStart(' ', '\t');
            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                inCodeFence = !inCodeFence;
                AppendSectionLine(sections, current, inExtra, extraBuilder, line);
                continue;
            }

            var heading = inCodeFence ? null : ParseHeading(line);
            if (heading is not null)
            {
                var normalized = NormalizeHeading(heading.Value.Text);
                if (CanonicalTitles.TryGetValue(normalized, out var canonical))
                {
                    current = canonical;
                    inExtra = false;
                    count++;
                    if (!sections.ContainsKey(canonical))
                    {
                        sections[canonical] = new StringBuilder();
                    }
                }
                else if (heading.Value.Level == 1)
                {
                    // 顶层标题由程序重新生成，避免重复。
                    current = null;
                    inExtra = false;
                }
                else
                {
                    current = null;
                    inExtra = true;
                    extraBuilder.Append(line).Append('\n');
                }

                continue;
            }

            AppendSectionLine(sections, current, inExtra, extraBuilder, line);
        }

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in sections)
        {
            result[pair.Key] = pair.Value.ToString().Trim();
        }

        extra = extraBuilder.ToString().Trim();
        recognizedCount = count;
        return result;
    }

    private static void AppendSectionLine(
        Dictionary<string, StringBuilder> sections,
        string? current,
        bool inExtra,
        StringBuilder extraBuilder,
        string line)
    {
        if (inExtra)
        {
            extraBuilder.Append(line).Append('\n');
        }
        else if (current is not null && sections.TryGetValue(current, out var builder))
        {
            builder.Append(line).Append('\n');
        }
    }

    private static (int Level, string Text)? ParseHeading(string line)
    {
        var trimmed = line.TrimStart(' ', '\t');
        if (!trimmed.StartsWith('#'))
        {
            return null;
        }

        var match = HeadingPattern.Match(line);
        if (!match.Success)
        {
            return null;
        }

        return (match.Groups["hashes"].Value.Length, match.Groups["text"].Value);
    }

    private static string NormalizeHeading(string text)
    {
        var value = text.Trim();
        value = value.Trim('*', '_', '`', ' ').Trim();
        value = value.TrimEnd(':', '：').Trim();
        return value;
    }

    private static string RemoveCandidateBlock(string text, out string? json, out bool found)
    {
        json = null;
        found = false;

        Match? candidate = null;
        Match? lastFence = null;
        foreach (Match match in FencePattern.Matches(text))
        {
            lastFence = match;
        }

        if (lastFence is not null &&
            string.Equals(lastFence.Groups["lang"].Value, "json", StringComparison.OrdinalIgnoreCase))
        {
            candidate = lastFence;
        }

        if (candidate is not null)
        {
            json = candidate.Groups["body"].Value;
            found = true;
            return text.Remove(candidate.Index, candidate.Length);
        }

        // 末尾没有闭合围栏时仍当作候选块处理，避免把 JSON 留在正文中。
        var open = text.LastIndexOf("```json", StringComparison.OrdinalIgnoreCase);
        if (open >= 0)
        {
            var tail = text[(open + 7)..];
            if (!tail.Contains("```", StringComparison.Ordinal))
            {
                json = tail.Trim();
                found = true;
                return text[..open].TrimEnd();
            }
        }

        return text;
    }

    private readonly struct CandidateParseOutcome
    {
        public static readonly CandidateParseOutcome Failed = new(false, Array.Empty<NoteCandidate>(), 0);

        public CandidateParseOutcome(bool success, IReadOnlyList<NoteCandidate> candidates, int skipped)
        {
            Success = success;
            Candidates = candidates;
            Skipped = skipped;
        }

        public bool Success { get; }
        public IReadOnlyList<NoteCandidate> Candidates { get; }
        public int Skipped { get; }
    }

    private static CandidateParseOutcome ParseCandidates(string? json, NoteParseContext context)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new CandidateParseOutcome(true, Array.Empty<NoteCandidate>(), 0);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return CandidateParseOutcome.Failed;
        }

        using (document)
        {
            var root = document.RootElement;
            JsonElement array;
            if (root.ValueKind == JsonValueKind.Array)
            {
                array = root;
            }
            else if (root.ValueKind == JsonValueKind.Object)
            {
                if (!TryGetProperty(root, "candidates", out array) &&
                    !TryGetProperty(root, "todos", out array) &&
                    !TryGetProperty(root, "待办候选", out array))
                {
                    return CandidateParseOutcome.Failed;
                }

                if (array.ValueKind == JsonValueKind.Null)
                {
                    return new CandidateParseOutcome(true, Array.Empty<NoteCandidate>(), 0);
                }
            }
            else
            {
                return CandidateParseOutcome.Failed;
            }

            if (array.ValueKind != JsonValueKind.Array)
            {
                return CandidateParseOutcome.Failed;
            }

            var candidates = new List<NoteCandidate>();
            var skipped = 0;
            foreach (var item in array.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    skipped++;
                    continue;
                }

                if (!TryGetProperty(item, "text", out var textElement) ||
                    textElement.ValueKind != JsonValueKind.String)
                {
                    skipped++;
                    continue;
                }

                var text = textElement.GetString()?.Trim();
                if (string.IsNullOrWhiteSpace(text))
                {
                    skipped++;
                    continue;
                }

                DateOnly? suggested = null;
                if (TryGetProperty(item, "date", out var dateElement) &&
                    dateElement.ValueKind is not JsonValueKind.Null)
                {
                    if (dateElement.ValueKind == JsonValueKind.String)
                    {
                        var dateText = dateElement.GetString();
                        if (!string.IsNullOrWhiteSpace(dateText))
                        {
                            if (!NoteText.TryParseLessonDate(dateText, out var parsed))
                            {
                                skipped++;
                                continue;
                            }

                            suggested = parsed;
                        }
                    }
                    else
                    {
                        skipped++;
                        continue;
                    }
                }

                candidates.Add(new NoteCandidate
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Text = text,
                    SuggestedDate = suggested,
                    SourceLessonText = BuildSourceText(context),
                    AddedToTodo = false
                });
            }

            return new CandidateParseOutcome(true, candidates, skipped);
        }
    }

    private static string BuildSourceText(NoteParseContext context)
    {
        var builder = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(context.CourseName))
        {
            builder.Append(context.CourseName.Trim());
        }

        var date = context.LessonDateText?.Trim();
        var session = context.SessionText?.Trim();
        if (!string.IsNullOrWhiteSpace(date) || !string.IsNullOrWhiteSpace(session))
        {
            if (builder.Length > 0)
            {
                builder.Append(" · ");
            }

            builder.Append(string.Join(" ", new[] { date, session }.Where(v => !string.IsNullOrWhiteSpace(v))));
        }

        return builder.ToString();
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static Dictionary<string, string> BuildCanonicalTitles()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var title in NoteSectionTitles.Ordered)
        {
            map[title] = title;
        }

        return map;
    }
}
