namespace ZJUSS.Core.Classroom;

public enum CoursewareTextStatus
{
    Available,
    NoTextLayer,
    UnsupportedFormat,
    ExtractionFailed,
    NotRead,
    MultipleCoursewareUnconfirmed
}

/// <summary>一次笔记生成所需的课节材料；只读本机已保存内容，不写课堂索引。</summary>
public sealed class LessonMaterialSnapshot
{
    public required string ClassroomCourseId { get; init; }
    public required string LessonId { get; init; }
    public required string CourseName { get; init; }
    public required string Term { get; init; }
    public string? LessonDateText { get; init; }
    public string? SessionText { get; init; }
    public ClassroomMatchKind MatchKind { get; init; } = ClassroomMatchKind.Unmatched;
    public string? LearningCourseId { get; init; }
    public bool RemovedFromPlatform { get; init; }

    /// <summary>所选一份课件的原始文件名；没有可用课件时为空。</summary>
    public string? CoursewareFileName { get; init; }

    /// <summary>pdf / pptx / ppt；没有可用课件时为空。</summary>
    public string? CoursewareFormat { get; init; }

    public int? CoursewarePageCount { get; init; }

    public string? CoursewareText { get; init; }
    public CoursewareTextStatus CoursewareTextStatus { get; init; } = CoursewareTextStatus.NotRead;
    public string? CoursewareTextDetail { get; init; }

    /// <summary>课件文字由本机 OCR 识别；只用于材料说明，不影响唯一性或 API 协议。</summary>
    public bool CoursewareTextFromOcr { get; init; }

    public string? TranscriptText { get; init; }
    public bool TranscriptAvailable { get; init; }

    /// <summary>有可用的非空课件文字。</summary>
    public bool UsesCoursewareText => CoursewareTextStatus == CoursewareTextStatus.Available &&
                                      !string.IsNullOrWhiteSpace(CoursewareText);

    /// <summary>有可用的非空语音文字。</summary>
    public bool UsesTranscript => TranscriptAvailable && !string.IsNullOrWhiteSpace(TranscriptText);

    public bool HasMaterial => UsesCoursewareText || UsesTranscript;

    public string? MissingReason { get; init; }
}

public sealed class NoteMaterialResult
{
    public bool Success { get; init; }
    public ClassroomFailureReason Failure { get; init; }
    public string? Detail { get; init; }
    public LessonMaterialSnapshot? Material { get; init; }
}

public sealed class CoursewareTextResult
{
    public CoursewareTextStatus Status { get; init; }
    public string? Text { get; init; }

    /// <summary>pdf / pptx / ppt；无法判断时为空。</summary>
    public string? Format { get; init; }

    /// <summary>PDF 页数或 PPTX 幻灯片数；未读取或格式不支持时为空。</summary>
    public int? PageCount { get; init; }

    /// <summary>提取文字的总字符数，与 <see cref="Text"/> 的长度一致（含页/幻灯片标记和换行）。</summary>
    public int CharacterCount { get; init; }
    public string? FailureReason { get; init; }

    /// <summary>文字来自本机 OCR，而不是 PDF 文本层/PPTX 文字。</summary>
    public bool UsedOcr { get; init; }

    public bool IsAvailable => Status == CoursewareTextStatus.Available && !string.IsNullOrWhiteSpace(Text);
}

/// <summary>
/// 课件文字本地提取：PDF 文本层、PPTX 幻灯片文字；不做 OCR、不联网、不修改课件。
/// 只允许读取课堂内容根目录内由本程序保存的当前课节文件。
/// </summary>
public interface ICoursewareTextExtractor
{
    Task<CoursewareTextResult> ExtractAsync(
        string fullPath,
        string? originalFileName = null,
        CancellationToken cancellationToken = default);
}
