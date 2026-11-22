using ZJUSS.Core.Classroom;

namespace ZJUSS.Core.Messages;

/// <summary>
/// 智云课堂读取状态、阶段和失败分类的中文文案映射。
/// 只负责文案，不依赖 WPF，也不读写网络或文件。
/// </summary>
public static class ClassroomMessages
{
    public const string Module = "智云课堂";

    public static string StatusLabel(ClassroomContentStatus status) => status switch
    {
        ClassroomContentStatus.NotRead => "未读取",
        ClassroomContentStatus.Reading => "读取中",
        ClassroomContentStatus.Read => "已读取",
        ClassroomContentStatus.PartiallyFailed => "部分失败",
        ClassroomContentStatus.Failed => "读取失败",
        ClassroomContentStatus.RemovedFromPlatform => "平台已移除",
        ClassroomContentStatus.Cancelled => "已取消",
        _ => "未读取"
    };

    /// <summary>内容类别文案；用于界面和日志，不记录文字或 PPT 内容本身。</summary>
    public static string ContentLabel(string contentKind) => contentKind switch
    {
        ClassroomContentKind.Transcript => "语音文字",
        ClassroomContentKind.Ppt => "PPT 文件",
        ClassroomContentKind.LessonList => "课节清单",
        ClassroomContentKind.CourseList => "课程列表",
        _ => "课堂内容"
    };

    public static string PartiallyFailedDetail(int successParts, int failedParts) =>
        $"成功 {successParts} 项，失败 {failedParts} 项；成功部分可查看，失败部分可单独重试。";

    public static string RemovedLessonDetail(string lessonLabel) =>
        $"“{lessonLabel}”不在平台的当前课节清单中，本机保存的内容仍然保留。";
}

/// <summary>课堂内容类别常量；避免在日志和界面里传自由文本。</summary>
public static class ClassroomContentKind
{
    public const string CourseList = "CourseList";
    public const string LessonList = "LessonList";
    public const string Transcript = "Transcript";
    public const string Ppt = "Ppt";
}

