using System.Globalization;

namespace ZJUSS.Core.Adapters.Classroom;

/// <summary>
/// 智云课堂只读采集使用的固定地址。token 只作为参数传入，不保存在本类型；
/// 任何日志都不得打印这里生成的完整 URL（yjapi 的 query 中带 token）。
/// </summary>
internal static class ClassroomEndpoints
{
    public const string TenantCode = "112";

    public static readonly Uri ClassroomRoot = new("https://classroom.zju.edu.cn/");

    /// <summary>登录桥：CAS 成功后由它把 classroom 的 _token 写进 Cookie。tenant_code 固定 112。</summary>
    public const string LoginBridgeUrl =
        "https://tgmedia.cmc.zju.edu.cn/index.php?r=auth/login&auType=cmc&tenant_code=112" +
        "&forward=https%3A%2F%2Fclassroom.zju.edu.cn%2F";

    public static readonly Uri CasLoginEntry = new(
        "https://zjuam.zju.edu.cn/cas/login?service=" + Uri.EscapeDataString(LoginBridgeUrl));

    /// <summary>学期列表；不需要 token。</summary>
    public static readonly Uri TermYears = new(
        "https://yjapi.cmc.zju.edu.cn/courseapi/index.php/v2/course/get-term-years?tenant=112");

    /// <summary>当前登录用户的简要信息；用于取得周课表所需的平台 user_id（响应格式不固定，解析失败则忽略）。</summary>
    public static Uri UserInfo(string token) => new(
        "https://classroom.zju.edu.cn/userapi/v1/infosimple?token=" + Uri.EscapeDataString(token));

    /// <summary>当前登录用户的课程月历；只依赖 Bearer token，不需要 user_id。</summary>
    public static Uri MyCourseMonth(string month) => new(
        "https://classroom.zju.edu.cn/courseapi/v2/course-live/get-my-course-month?month=" +
        Uri.EscapeDataString(month));

    /// <summary>周课表/回放列表；token 在 query，一次传该学期的起止日期。</summary>
    public static Uri WeekSchedules(string userId, DateOnly start, DateOnly end, string token) => new(
        "https://yjapi.cmc.zju.edu.cn/courseapi/v2/schedule/get-week-schedules" +
        "?user_id=" + Uri.EscapeDataString(userId) +
        "&tenant_id=" + TenantCode +
        "&start_at=" + start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) +
        "&end_at=" + end.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) +
        "&token=" + Uri.EscapeDataString(token));

    /// <summary>语音文字；先不带 token，失败时再带 query token。</summary>
    public static Uri Transcript(string lessonId, string? token = null)
    {
        var url = "https://yjapi.cmc.zju.edu.cn/courseapi/v3/web-socket/search-trans-result" +
                  "?sub_id=" + Uri.EscapeDataString(lessonId) +
                  "&format=json";
        if (!string.IsNullOrEmpty(token))
        {
            url += "&token=" + Uri.EscapeDataString(token);
        }

        return new Uri(url);
    }

    /// <summary>课件幻灯片索引；token 放 Authorization: Bearer，不放 query。</summary>
    public static Uri SearchPpt(string courseId, string lessonId, int page = 1) => new(
        "https://classroom.zju.edu.cn/pptnote/v1/schedule/search-ppt" +
        "?course_id=" + Uri.EscapeDataString(courseId) +
        "&sub_id=" + Uri.EscapeDataString(lessonId) +
        "&page=" + page.ToString(CultureInfo.InvariantCulture) +
        "&per_page=100");
}
