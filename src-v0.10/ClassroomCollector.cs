using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ZJUSS.Core.Access;
using ZJUSS.Core.Adapters.Classroom;
using ZJUSS.Core.Classroom;
using ZJUSS.Core.Security;

namespace Shike;

public sealed class CloudCredentials : ICredentialProvider
{
    readonly SchoolCredential credential;
    public CloudCredentials(SchoolCredential credential) => this.credential = credential;
    public bool IsUnlocked => true;
    public string Account => credential.Username;
    public event EventHandler? CredentialsSaved { add { } remove { } }
    public SchoolCredentials GetCredentials() => new(credential.Username, credential.Password);
}
public sealed class CloudMaterials
{
    public string Transcript { get; set; } = "";
    public string Text { get; set; } = "";
    public bool Ocr { get; set; }
    public List<string> Missing { get; set; } = new();
    public List<ClassroomPptFile> Files { get; set; } = new();
}

// UI serializes operations and persists metadata after each completed part. Bytes remain in classroom/.
public sealed class ClassroomCollector : IDisposable
{
    readonly HttpClient? http;
    readonly IClassroomSource source;
    readonly ICoursewareTextExtractor extractor;
    public LocalClassroomContentStore Content { get; }
    public ClassroomCollector(string root, SchoolCredential credential)
    {
        http = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer(), AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(45) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Shike/0.10");
        source = new ClassroomSourceAdapter(http, new CloudCredentials(credential), timeZone: TimeZoneInfo.FindSystemTimeZoneById("China Standard Time"));
        Content = new LocalClassroomContentStore(Path.Combine(root, "classroom"));
        extractor = new WindowsOcrCoursewareTextExtractor();
    }
    public ClassroomCollector(string root, IClassroomSource source, ICoursewareTextExtractor extractor)
    { this.source = source; this.extractor = extractor; Content = new LocalClassroomContentStore(Path.Combine(root, "classroom")); }

    public async Task RefreshCourses(AppData data, CancellationToken token)
    {
        var result = await source.GetCoursesAsync(token); token.ThrowIfCancellationRequested();
        Require(result.Success, result.Detail);
        var courses = result.Value ?? Array.Empty<ClassroomCourse>();
        foreach (var item in courses)
        {
            var course = data.Classroom.Courses.SingleOrDefault(c => c.Id == item.CourseId);
            if (course == null) { course = new() { Id = item.CourseId }; data.Classroom.Courses.Add(course); }
            course.Name = item.Name; course.Term = item.Term; course.Info = string.Join(" · ", item.PlatformInfo);
        }
        foreach (var course in data.Classroom.Courses) ClassroomModel.Match(data, course);
        data.Classroom.Updated = DateTime.Now;
    }
    public async Task RefreshLessons(CloudCourse course, CancellationToken token)
    {
        var result = await source.GetLessonsAsync(course.Id, token); token.ThrowIfCancellationRequested(); Require(result.Success, result.Detail);
        var lessons = result.Value ?? Array.Empty<ClassroomLesson>();
        foreach (var item in lessons)
        {
            var lesson = course.Lessons.SingleOrDefault(l => l.Id == item.LessonId);
            if (lesson == null) { lesson = new() { Id = item.LessonId }; course.Lessons.Add(lesson); }
            lesson.Date = item.DateText ?? ""; lesson.Session = item.SessionText ?? ""; lesson.Title = item.Title ?? "课堂记录"; lesson.Removed = false;
        }
        var ids = lessons.Select(l => l.LessonId).ToHashSet();
        foreach (var lesson in course.Lessons.Where(l => !ids.Contains(l.Id))) lesson.Removed = true;
        course.Updated = DateTime.Now;
    }
    public async Task Inspect(CloudCourse course, CloudLesson lesson, Action checkpoint, CancellationToken token)
    {
        if (lesson.TranscriptRead)
        {
            try { await Content.ReadTranscriptAsync(course.Id, lesson.Id, token); }
            catch (ClassroomStorageException) { lesson.TranscriptRead = false; }
        }
        if (!lesson.FilesListed)
        {
            var result = await source.GetPptInfoAsync(course.Id, lesson.Id, token); token.ThrowIfCancellationRequested(); Require(result.Success, result.Detail);
            lesson.Files = (result.Value ?? Array.Empty<ClassroomPptFile>()).ToList(); lesson.FilesListed = true; checkpoint();
        }
        if (!lesson.TranscriptRead)
        {
            var result = await source.GetTranscriptAsync(course.Id, lesson.Id, token); token.ThrowIfCancellationRequested(); Require(result.Success, result.Detail);
            await Content.SaveTranscriptAsync(course.Id, lesson.Id, result.Value ?? "", token);
            lesson.TranscriptRead = true; checkpoint();
        }
    }
    public async Task<CloudMaterials> Materials(CloudCourse course, CloudLesson lesson, CloudJob job, Action checkpoint, CancellationToken token)
    {
        var materials = new CloudMaterials();
        if (lesson.TranscriptRead) materials.Transcript = await Content.ReadTranscriptAsync(course.Id, lesson.Id, token);
        if (string.IsNullOrWhiteSpace(materials.Transcript)) materials.Missing.Add("缺少语音文字");
        foreach (var key in job.FileKeys)
        {
            var file = lesson.Files.SingleOrDefault(f => ClassroomModel.FileKey(f) == key) ?? throw new InvalidDataException("已选课件不在当前清单中，请重新选择。");
            string path;
            try { path = Content.ResolvePptPath(file); }
            catch (Exception ex) when (ex is ClassroomStorageException or FileNotFoundException)
            {
                var result = await source.DownloadPptAsync(course.Id, lesson.Id, file, token); token.ThrowIfCancellationRequested(); Require(result.Success && result.Value != null, result.Detail);
                using (var stream = result.Value!.Content)
                    file = await Content.SavePptAsync(course.Id, lesson.Id, file, stream, token);
                int index = lesson.Files.FindIndex(f => ClassroomModel.FileKey(f) == key); lesson.Files[index] = file; checkpoint();
                path = Content.ResolvePptPath(file);
            }
            var extracted = await extractor.ExtractAsync(path, file.OriginalFileName, token);
            materials.Files.Add(file);
            if (extracted.IsAvailable) { materials.Text += "\n\n[课件：" + file.OriginalFileName + "]\n" + extracted.Text; materials.Ocr |= extracted.UsedOcr; }
            else materials.Missing.Add((file.OriginalFileName ?? "课件") + "：" + (extracted.FailureReason ?? "无法提取文字"));
        }
        if (job.FileKeys.Count == 0) materials.Missing.Add("未使用课件");
        return materials;
    }
    static void Require(bool success, string? detail) { if (!success) throw new InvalidOperationException(detail ?? "课堂读取失败，请检查学校连接后重试。"); }
    public void Dispose() => http?.Dispose();
}
