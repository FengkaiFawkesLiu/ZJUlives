using System.Text.Json.Serialization;
using ZJUSS.Core.Classroom;


namespace ZJUSS.Core.Notes;

public static class NoteSectionTitles
{
    public const string LessonInfo = "课节信息";
    public const string KeyPoints = "课堂要点";
    public const string DetailedExplanation = "详细讲解";
    public const string ExamplesAndReminders = "例题与提醒";
    public const string TodoItems = "待办事项";
    public const string MaterialNotes = "材料说明";

    public static IReadOnlyList<string> Ordered { get; } = new[]
    {
        LessonInfo,
        KeyPoints,
        DetailedExplanation,
        ExamplesAndReminders,
        TodoItems,
        MaterialNotes
    };
}

public sealed class NoteCandidate
{
    public required string Id { get; init; }
    public required string Text { get; init; }

    /// <summary>建议截止日期，精确到天；无法确认时为空。</summary>
    public DateOnly? SuggestedDate { get; init; }

    public required string SourceLessonText { get; init; }
    public bool AddedToTodo { get; init; }
}


