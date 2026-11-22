using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ZJUSS.Core.Classroom;
using ZJUSS.Core.NoteApi;
using ZJUSS.Core.Notes;

namespace Shike;

public sealed class CloudState
{
    public string? AccountHash { get; set; }
    public List<CloudCourse> Courses { get; set; } = new();
    public List<CloudJob> Jobs { get; set; } = new();
    public DateTime? Updated { get; set; }
}
public sealed class CloudCourse
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Term { get; set; } = "";
    public string Info { get; set; } = "";
    public string? CourseKey { get; set; }
    public bool ManualLink { get; set; }
    public List<CloudLesson> Lessons { get; set; } = new();
    public DateTime? Updated { get; set; }
}
public sealed class CloudLesson
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Date { get; set; } = "";
    public string Session { get; set; } = "";
    public bool Removed { get; set; }
    public bool TranscriptRead { get; set; }
    public bool FilesListed { get; set; }
    public List<ClassroomPptFile> Files { get; set; } = new();
    public string Label => string.Join(" · ", new[] { Date, Session, Title }.Where(s => s.Length > 0));
}
public sealed class CloudJob
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string CourseId { get; set; } = "";
    public string LessonId { get; set; } = "";
    public string State { get; set; } = "等待中";
    public string Detail { get; set; } = "";
    public List<string> FileKeys { get; set; } = new();
    public bool FilesConfirmed { get; set; }
    public string? NoteId { get; set; }
}
public sealed class CloudNoteSource
{
    public string CourseId { get; set; } = "";
    public string LessonId { get; set; } = "";
    public string JobId { get; set; } = "";
    public int Version { get; set; }
    public string Term { get; set; } = "";
    public DateTime Generated { get; set; }
    public List<CloudCandidate> Candidates { get; set; } = new();
}
public sealed class CloudCandidate
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Text { get; set; } = "";
    public DateTime? Date { get; set; }
    public string? TodoId { get; set; }
}

public static class ClassroomModel
{
    public static string FileKey(ClassroomPptFile f) => f.FileId ?? f.OriginalFileName ?? "课件.pdf";
    public static bool Pending(CloudJob j) => j.State is "等待中" or "进行中" or "待选择材料" or "已暂停";
    public static string CurrentTerm(Settings s) => $"{s.Year}-{s.Year + 1}-{(s.Semester == "3" ? 1 : s.Semester == "12" ? 2 : s.Semester == "16" ? 3 : 0)}";
    public static IEnumerable<Item> Versions(AppData data, string course, string lesson) => data.Items.Where(n => n.Kind == "笔记" && !n.Cancelled && n.ClassroomSource?.CourseId == course && n.ClassroomSource.LessonId == lesson).OrderByDescending(n => n.ClassroomSource!.Version);
    public static void Match(AppData data, CloudCourse course)
    {
        if (course.ManualLink) return;
        var matches = data.Items.Where(i => i.Kind == "课程" && !i.Cancelled && !i.SchoolRemoved && i.Title.Trim() == course.Name.Trim() && ScopeTerm(i.Scope) == course.Term && (data.Classroom.AccountHash == null || i.Scope!.StartsWith("zju-" + data.Classroom.AccountHash + "-", StringComparison.Ordinal)))
            .GroupBy(NotesModel.CourseKey).ToList();
        var peers = data.Classroom.Courses.Count(c => c.Name.Trim() == course.Name.Trim() && c.Term == course.Term);
        course.CourseKey = matches.Count == 1 && peers == 1 ? matches[0].Key : null;
    }
    public static string? ScopeTerm(string? scope)
    {
        if (scope == null) return null;
        var parts = scope.Split('-');
        if (parts.Length != 5 || parts[0] != "zju" || parts[4] != "course" || !int.TryParse(parts[2], out int year)) return null;
        int term = parts[3] == "3" ? 1 : parts[3] == "12" ? 2 : parts[3] == "16" ? 3 : 0;
        return term == 0 ? null : $"{year}-{year + 1}-{term}";
    }
    public static Item CreateNote(AppData data, CloudCourse course, CloudLesson lesson, CloudJob job, NoteParseResult result, List<Attachment> files)
    {
        // Stable job identity prevents duplicates after a crash between note commit and queue advancement.
        var existing = data.Items.FirstOrDefault(i => i.ClassroomSource?.JobId == job.Id);
        if (existing != null) return existing;
        const string libraryId = "zhiyun-classroom";
        if (!data.NoteLibraries.Any(l => l.Id == libraryId)) data.NoteLibraries.Add(new() { Id = libraryId, Name = "课堂笔记" });
        int version = data.Items.Where(n => n.ClassroomSource?.CourseId == course.Id && n.ClassroomSource.LessonId == lesson.Id).Select(n => n.ClassroomSource!.Version).DefaultIfEmpty().Max() + 1;
        var note = new Item { Kind = "笔记", LibraryId = libraryId, Title = result.Title + (version > 1 ? $" · 版本 {version}" : ""), NoteFormat = MarkdownNotes.Format, NoteBody = result.BodyMarkdown, Attachments = files,
            ClassroomSource = new() { CourseId = course.Id, LessonId = lesson.Id, JobId = job.Id, Term = course.Term, Version = version, Generated = DateTime.Now,
                Candidates = result.Candidates.Select(c => new CloudCandidate { Text = c.Text, Date = c.SuggestedDate?.ToDateTime(TimeOnly.MinValue) }).ToList() } };
        if (course.CourseKey != null) note.NoteLinks.Add(new() { Kind = "course", Key = course.CourseKey, Label = course.Name });
        data.Items.Add(note);
        return note;
    }
    public static Item ConfirmCandidate(AppData data, Item note, CloudCandidate candidate, string text, DateTime? due)
    {
        var existing = data.Items.FirstOrDefault(i => i.Id == candidate.TodoId);
        if (existing != null) return existing;
        var day = due?.Date ?? DateTime.Today;
        var todo = new Item { Kind = "待办", Title = text, Start = day, End = day.AddMinutes(1), AssistantNoDue = due == null, Note = "课堂笔记：" + note.Title + "\n原候选：" + candidate.Text };
        data.Items.Add(todo); candidate.TodoId = todo.Id;
        note.NoteLinks.Add(new() { Kind = "item", Key = NotesModel.Key(todo), Label = todo.Title });
        return todo;
    }
}

public sealed class CloudApiSettings
{
    public string Endpoint { get; set; } = "";
    public string Key { get; set; } = "";
    public string Model { get; set; } = "";
    public bool HttpConfirmed { get; set; }
    public NoteApiConfigSnapshot Snapshot() => new(Endpoint, Key, Model, HttpConfirmed);
}
public sealed class CloudApiVault
{
    readonly string path;
    static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Shike.ClassroomApi.v1");
    public CloudApiVault(string root) => path = Path.Combine(root, "classroom-api.dpapi");
    public CloudApiSettings? Read()
    {
        if (!File.Exists(path)) return null;
        var bytes = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser);
        try { return JsonSerializer.Deserialize<CloudApiSettings>(bytes) ?? throw new InvalidDataException("AI 配置为空。"); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    public void Save(CloudApiSettings settings)
    {
        if (!NoteApiEndpointPolicy.TryValidate(settings.Endpoint, out var uri, out _, out bool risk) || uri!.UserInfo.Length > 0 || uri.Fragment.Length > 0 || string.IsNullOrWhiteSpace(settings.Key) || string.IsNullOrWhiteSpace(settings.Model)) throw new InvalidDataException("请填写有效的 API 完整地址、密钥和模型。");
        if (risk && !settings.HttpConfirmed) throw new InvalidDataException("远程 HTTP 会明文传输密钥和课堂文字，需要确认风险。");
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(settings);
        try { File.WriteAllBytes(path + ".tmp", ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser)); File.Move(path + ".tmp", path, true); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    public void Clear() { if (File.Exists(path)) File.Delete(path); }
}
