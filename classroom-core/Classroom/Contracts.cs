using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZJUSS.Core.Classroom;

/// <summary>课堂内容（课节索引、语音文字、PPT）与读取状态的对外契约。</summary>
public enum ClassroomContentStatus
{
    /// <summary>本机还没有这部分内容。</summary>
    NotRead,

    /// <summary>本次正在读取或下载。</summary>
    Reading,

    /// <summary>已成功保存到本机。</summary>
    Read,

    /// <summary>文字或部分 PPT 成功，另一部分失败。</summary>
    PartiallyFailed,

    /// <summary>本次读取失败，没有写入新内容。</summary>
    Failed,

    /// <summary>平台当前课节清单里已没有这一节，但本机内容保留。</summary>
    RemovedFromPlatform,

    /// <summary>本次读取被使用者取消；已完成部分保留。</summary>
    Cancelled
}

/// <summary>智云课堂按需读取的失败分类；不使用启动同步的 SourceFailureReason/状态。</summary>
public enum ClassroomFailureReason
{
    None,

    /// <summary>无法连接。</summary>
    Unreachable,

    /// <summary>请求超时。</summary>
    Timeout,

    /// <summary>登录失败；不区分账号不存在和密码错误。</summary>
    LoginFailed,

    /// <summary>出现验证码或二次验证。</summary>
    VerificationRequired,

    /// <summary>课程未开放、无访问权限或需要申请。</summary>
    NoPermission,

    /// <summary>页面改版或数据结构不符。</summary>
    SchemaChanged,

    /// <summary>本机写入失败（磁盘空间不足、无写入权限、文件被占用）。</summary>
    StorageFailed,

    /// <summary>本次读取被取消；不作为错误。</summary>
    Cancelled,

    /// <summary>未找到对应课程或课节。</summary>
    NotFound,

    /// <summary>离线且本机没有缓存。</summary>
    Offline,

    Unknown
}

/// <summary>智云课堂课程与教务网课程的匹配结果。</summary>
public enum ClassroomMatchKind
{
    Matched,
    Unmatched
}

/// <summary>未匹配原因；可组合（例如两侧都出现多候选）。</summary>
[Flags]
public enum ClassroomUnmatchedReason
{
    None = 0,
    NoCandidate = 1,
    MultipleClassroomCandidates = 2,
    MultipleLearningCandidates = 4,
    MissingCourseName = 8,
    MissingTerm = 16,
    TextNotVerifiablyEqual = 32
}

/// <summary>智云课堂课程；字段按平台原文保存，缺失为空，不推算。</summary>
public sealed class ClassroomCourse
{
    public required string CourseId { get; init; }
    public required string Name { get; init; }
    public required string Term { get; init; }

    /// <summary>平台提供的可选信息（如教师、平台学期文案）；只用于显示，不参与匹配。</summary>
    public IReadOnlyList<string> PlatformInfo { get; init; } = Array.Empty<string>();
}

/// <summary>单个 PPT 文件及其读取状态。</summary>
public sealed class ClassroomPptFile
{
    /// <summary>平台文件标识；平台未提供时为空。</summary>
    public string? FileId { get; init; }

    /// <summary>平台提供的原始文件名；为空时由存储层给出保守的占位名。</summary>
    public string? OriginalFileName { get; init; }

    /// <summary>课堂内容根目录下的相对路径；尚未保存时为空。</summary>
    public string? RelativePath { get; init; }

    public long? SizeBytes { get; init; }
    public DateTimeOffset? SavedAt { get; init; }

    /// <summary>课件 PDF 的页数；原文件或尚未保存时为 null。</summary>
    public int? PageCount { get; init; }

    public ClassroomContentStatus Status { get; init; }
    public string? FailureReason { get; init; }
}

/// <summary>一节智云课堂课的课节信息；平台未提供的字段为空。</summary>
public sealed class ClassroomLesson
{
    public required string LessonId { get; init; }

    /// <summary>平台提供的日期原文（例如“2026-10-20”）；没有时为空。</summary>
    public string? DateText { get; init; }

    /// <summary>平台提供的节次或时间描述原文（例如“第 3-4 节”）；没有时为空。</summary>
    public string? SessionText { get; init; }

    public string? Title { get; init; }
    public IReadOnlyList<ClassroomPptFile> PptFiles { get; init; } = Array.Empty<ClassroomPptFile>();

    /// <summary>平台是否提供语音文字；未知时为 null，不当作“暂无”。</summary>
    public bool? TranscriptAvailable { get; init; }

    public ClassroomContentStatus TranscriptStatus { get; init; }

    /// <summary>本机已保存但平台当前清单中没有这一节。</summary>
    public bool RemovedFromPlatform { get; init; }

    /// <summary>
    /// 读取时填充的语音文字原文；不写入 index.json（从 transcript.txt 读取）。
    /// 未读取或读取失败时为 null，不能与“暂无”（空字符串）混淆。
    /// </summary>
    [JsonIgnore]
    public string? TranscriptText { get; init; }
}

/// <summary>本机课节索引中的一门智云课堂课程。</summary>
public sealed class ClassroomCourseEntry
{
    public required ClassroomCourse Course { get; init; }
    public string? LearningCourseId { get; init; }
    public ClassroomMatchKind MatchKind { get; init; } = ClassroomMatchKind.Unmatched;
    public ClassroomUnmatchedReason UnmatchedReason { get; init; } = ClassroomUnmatchedReason.None;
    public IReadOnlyList<ClassroomLesson> Lessons { get; init; } = Array.Empty<ClassroomLesson>();
}

/// <summary>课堂内容索引；独立于学习数据和备份。</summary>
public sealed class ClassroomIndex
{
    public const int CurrentVersion = 1;

    public int Version { get; init; } = CurrentVersion;
    public IReadOnlyList<ClassroomCourseEntry> Courses { get; init; } = Array.Empty<ClassroomCourseEntry>();

    /// <summary>旧版硬删除留下的课堂课程标识；当前版本只读兼容，不再写入。</summary>
    public IReadOnlyList<string> DeletedCourseIds { get; init; } = Array.Empty<string>();

    /// <summary>旧版硬删除留下的课堂课程对应的教务网课程标识；当前版本只读兼容，不再写入。</summary>
    public IReadOnlyList<string> DeletedLearningCourseIds { get; init; } = Array.Empty<string>();
}

/// <summary>单门智云课堂课程的匹配结论。</summary>
public sealed class ClassroomMatchResult
{
    public required ClassroomCourse Course { get; init; }
    public ClassroomMatchKind Kind { get; init; }
    public string? LearningCourseId { get; init; }
    public ClassroomUnmatchedReason Reason { get; init; }
}

/// <summary>一次匹配的全部结果；未匹配列表只包含未匹配课程。</summary>
public sealed class ClassroomMatchReport
{
    public IReadOnlyList<ClassroomMatchResult> Results { get; init; } = Array.Empty<ClassroomMatchResult>();
    public IReadOnlyList<ClassroomMatchResult> Unmatched { get; init; } = Array.Empty<ClassroomMatchResult>();
}

public enum ClassroomIndexLoadStatus
{
    Ok,
    Missing,
    Corrupted,
    Incompatible
}

public sealed class ClassroomIndexLoadResult
{
    public required ClassroomIndexLoadStatus Status { get; init; }
    public ClassroomIndex? Index { get; init; }
    public string? Detail { get; init; }

    public bool IsUsable => Status is ClassroomIndexLoadStatus.Ok or ClassroomIndexLoadStatus.Missing;
}

/// <summary>课堂内容存储失败分类。</summary>
public enum ClassroomStorageError
{
    None,
    PathRejected,
    WriteFailed,
    DiskFull,
    AccessDenied,
    IndexCorrupted,
    FileMissing,
    Unknown
}

public sealed class ClassroomStorageException : Exception
{
    public ClassroomStorageException(ClassroomStorageError error, string message, Exception? inner = null)
        : base(message, inner)
    {
        Error = error;
    }

    public ClassroomStorageError Error { get; }
}

/// <summary>
/// 课堂内容本机存储：索引、语音文字和 PPT 文件独立于学习数据与备份。
/// 契约：不加密、不自动清理、不删除课堂文件、不上传、不进入备份；只允许课堂内容根目录内的路径。
/// 课程软删除由 App 层偏好实现，仅隐藏条目并保留文件以支持撤销。
/// </summary>
public interface IClassroomContentStore
{
    string RootDirectory { get; }

    Task<ClassroomIndexLoadResult> LoadIndexAsync(CancellationToken cancellationToken = default);

    Task SaveIndexAsync(ClassroomIndex index, CancellationToken cancellationToken = default);

    /// <summary>按课节保存语音文字原文；失败时抛出 <see cref="ClassroomStorageException"/>。</summary>
    Task SaveTranscriptAsync(
        string classroomCourseId,
        string lessonId,
        string transcript,
        CancellationToken cancellationToken = default);

    Task<string> ReadTranscriptAsync(
        string classroomCourseId,
        string lessonId,
        CancellationToken cancellationToken = default);

    /// <summary>保存 PPT 原文件并返回带相对路径和状态的描述；同名文件不覆盖。</summary>
    Task<ClassroomPptFile> SavePptAsync(
        string classroomCourseId,
        string lessonId,
        ClassroomPptFile file,
        Stream content,
        CancellationToken cancellationToken = default);

    /// <summary>取得已保存 PPT 的完整路径；路径必须在课堂内容根目录内且文件存在。</summary>
    string ResolvePptPath(ClassroomPptFile file);
}

/// <summary>智云课堂采集结果；失败分类与课堂内容服务共用，避免第二套状态。</summary>
public sealed class ClassroomSourceResult<T>
{
    public bool Success { get; init; }
    public T? Value { get; init; }
    public ClassroomFailureReason Failure { get; init; }
    public string? Detail { get; init; }

    public static ClassroomSourceResult<T> Ok(T value) => new() { Success = true, Value = value };

    public static ClassroomSourceResult<T> Fail(ClassroomFailureReason failure, string? detail = null) =>
        new() { Success = false, Failure = failure, Detail = detail };
}

/// <summary>PPT 下载结果：文件内容与平台原始文件名。</summary>
public sealed class ClassroomPptDownload
{
    public required string OriginalFileName { get; init; }
    public required Stream Content { get; init; }
    public long? SizeBytes { get; init; }
}

/// <summary>M12 提供的智云课堂只读采集接口；不负责匹配、保存、缓存或界面。</summary>
public interface IClassroomSource
{
    Task<ClassroomSourceResult<IReadOnlyList<ClassroomCourse>>> GetCoursesAsync(
        CancellationToken cancellationToken = default);

    Task<ClassroomSourceResult<IReadOnlyList<ClassroomLesson>>> GetLessonsAsync(
        string classroomCourseId,
        CancellationToken cancellationToken = default);

    Task<ClassroomSourceResult<string>> GetTranscriptAsync(
        string classroomCourseId,
        string lessonId,
        CancellationToken cancellationToken = default);

    Task<ClassroomSourceResult<ClassroomPptDownload>> DownloadPptAsync(
        string classroomCourseId,
        string lessonId,
        ClassroomPptFile file,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 只读取课件的元数据（是否存在、页数），不下载图片或 PDF。
    /// 用于打开课节时先创建课件条目，由使用者决定何时下载。
    /// </summary>
    Task<ClassroomSourceResult<IReadOnlyList<ClassroomPptFile>>> GetPptInfoAsync(
        string classroomCourseId,
        string lessonId,
        CancellationToken cancellationToken = default);
}

/// <summary>未匹配课程入口的刷新结果。</summary>
public sealed class ClassroomUnmatchedResult
{
    public bool Success { get; init; }
    public ClassroomFailureReason Failure { get; init; }
    public string? Detail { get; init; }
    public IReadOnlyList<ClassroomCourseEntry> UnmatchedCourses { get; init; } = Array.Empty<ClassroomCourseEntry>();

    /// <summary>失败时是否仍显示上一次缓存的未匹配列表。</summary>
    public bool ShowingCachedList { get; init; }
}

/// <summary>课程详情页“上课记录”的课节清单结果。</summary>
public sealed class ClassroomLessonListResult
{
    public bool Success { get; init; }
    public ClassroomFailureReason Failure { get; init; }
    public string? Detail { get; init; }
    public string? ClassroomCourseId { get; init; }
    public IReadOnlyList<ClassroomLesson> Lessons { get; init; } = Array.Empty<ClassroomLesson>();

    /// <summary>未找到唯一对应的智云课堂课程。</summary>
    public bool CourseNotMatched { get; init; }

    /// <summary>使用者已删除该课程的课堂记录；不再从平台重新加入。</summary>
    public bool Deleted { get; init; }
}

/// <summary>单节课内容读取/重试结果；文字和 PPT 分别记录。</summary>
public sealed class ClassroomLessonContentResult
{
    public bool Success { get; init; }
    public ClassroomFailureReason Failure { get; init; }
    public string? Detail { get; init; }
    public ClassroomContentStatus TranscriptStatus { get; init; }
    public IReadOnlyList<ClassroomPptFile> PptFiles { get; init; } = Array.Empty<ClassroomPptFile>();

    /// <summary>本次读取后仍失败、可单独重试的部分说明。</summary>
    public IReadOnlyList<string> FailedParts { get; init; } = Array.Empty<string>();
}

public sealed class ClassroomPptOpenResult
{
    public bool Success { get; init; }
    public ClassroomFailureReason Failure { get; init; }
    public string? Detail { get; init; }
}

/// <summary>“上课记录”栏目使用的课节视图数据。</summary>
public sealed class ClassroomRecordsView
{
    public bool Success { get; init; }
    public ClassroomFailureReason Failure { get; init; }
    public string? Detail { get; init; }
    public string? CourseName { get; init; }
    public string? Term { get; init; }
    public bool CourseNotMatched { get; init; }

    /// <summary>旧版硬删除留下的标记；当前版本只读兼容。</summary>
    public bool Deleted { get; init; }
    public IReadOnlyList<ClassroomLesson> Lessons { get; init; } = Array.Empty<ClassroomLesson>();
}

public sealed class ClassroomReadProgress
{
    public required string Stage { get; init; }
    public string? ClassroomCourseId { get; init; }
    public string? LessonId { get; init; }
    public required string Message { get; init; }
}

/// <summary>
/// 课堂内容服务：入口刷新未匹配列表、进入课程刷新课节清单、点开课节读取内容、
/// 取消、按失败部分重试、打开 PPT、读取“上课记录”视图数据。
/// 不写学习数据、不触发备份、不使用启动同步状态。
/// </summary>
public interface IClassroomService
{
    event EventHandler<ClassroomReadProgress>? ProgressChanged;

    Task<ClassroomUnmatchedResult> RefreshUnmatchedCoursesAsync(CancellationToken cancellationToken = default);

    Task<ClassroomLessonListResult> RefreshCourseLessonsAsync(
        string learningCourseId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 未匹配课程（只在“智云课堂”入口出现）刷新课节清单：按智云课堂课程标识读取并合并本机索引。
    /// 与已匹配课程的刷新规则相同（新增未读、保留已读、平台移除标注、失败保留旧清单）。
    /// </summary>
    Task<ClassroomLessonListResult> RefreshUnmatchedCourseLessonsAsync(
        string classroomCourseId,
        CancellationToken cancellationToken = default);

    Task<ClassroomLessonContentResult> ReadLessonContentAsync(
        string classroomCourseId,
        string lessonId,
        CancellationToken cancellationToken = default);

    Task<ClassroomLessonContentResult> RetryFailedPartsAsync(
        string classroomCourseId,
        string lessonId,
        CancellationToken cancellationToken = default);

    Task<ClassroomPptOpenResult> OpenPptAsync(
        string classroomCourseId,
        string lessonId,
        string pptFileId,
        CancellationToken cancellationToken = default);

    Task<ClassroomRecordsView> GetRecordsAsync(
        string classroomCourseId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 取得一次笔记生成所需的课节材料。已读内容不重读；未读时按课节读取规则读取并下载一份课件，
    /// 再从一份课件提取文字。读取失败按缺少该类材料处理，另一类仍可返回。
    /// </summary>
    Task<NoteMaterialResult> GetOrReadNoteMaterialAsync(
        string classroomCourseId,
        string lessonId,
        CancellationToken cancellationToken = default);
}

/// <summary>课堂内容 JSON 约定：camelCase、枚举字符串、中文不转义、缩进输出。</summary>
public static class ClassroomJson
{
    public static readonly JsonSerializerOptions Options = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        options.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false));
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
