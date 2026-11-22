using System.Globalization;
using System.Text;
using System.Text.Json;
using ZJUSS.Core.Classroom;
using ZJUSS.Core.Adapters.Shared;
using ZJUSS.Core.Models;

namespace ZJUSS.Core.Adapters.Classroom;

/// <summary>一个可用的智云课堂学期区间；只保留规范化后的学期号和请求所需字段。</summary>
internal sealed record ClassroomTermInfo(
    string Term,
    DateOnly BeginDate,
    DateOnly EndDate,
    string? UserId);

/// <summary>周课表里的单条课程记录；课程/课节的平台原文，未做合并或推算。</summary>
internal sealed record ClassroomWeekCourse(
    string CourseId,
    string CourseName,
    IReadOnlyList<string> PlatformInfo,
    string LessonId,
    string? Day,
    long? StartAt,
    long? EndAt,
    bool IsApply,
    int? Status,
    string? SessionText = null);

/// <summary>课件幻灯片索引里的一项；按 CreatedSec 升序后即为页面顺序。</summary>
internal sealed record ClassroomPptImageSource(string Url, long CreatedSec);

/// <summary>
/// 智云课堂响应解析。只做结构与字段映射：不写文件、不联网、不记录原文，
/// 结构不符统一抛出 <see cref="AdapterFailureException"/>（SchemaChanged）。
/// </summary>
internal static class ClassroomJsonParser
{
    public static IReadOnlyList<ClassroomTermInfo> ParseTerms(string json, string phase)
    {
        using var document = ParseDocument(json, phase);
        var root = document.RootElement;
        RequireSuccess(root, phase);
        var result = RequireProperty(root, "result", JsonValueKind.Object, phase);
        var data = RequireProperty(result, "data", JsonValueKind.Object, phase);

        var terms = new List<ClassroomTermInfo>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in data.EnumerateObject())
        {
            if (!IsTermGroupName(group.Name))
            {
                continue;
            }

            if (group.Value.ValueKind != JsonValueKind.Array)
            {
                throw Schema(phase, $"学期分组 {group.Name} 不是数组。");
            }

            foreach (var item in group.Value.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    throw Schema(phase, "学期分组中存在非对象条目。");
                }

                var year = ReadScalar(item, "year");
                var season = ReadScalar(item, "season");
                var beginText = ReadScalar(item, "begin_date");
                var endText = ReadScalar(item, "end_date");
                if (string.IsNullOrWhiteSpace(year) ||
                    string.IsNullOrWhiteSpace(season) ||
                    !TryParseDate(beginText, out var begin) ||
                    !TryParseDate(endText, out var end) ||
                    begin > end)
                {
                    // 无法规范化的季节或无效日期不参与读取，也不猜默认学期。
                    continue;
                }

                var term = SemesterTerm.FromYearAndSeason(year, season);
                if (term is null)
                {
                    continue;
                }

                var key = term + "|" + begin.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) +
                          "|" + end.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                if (!seen.Add(key))
                {
                    continue;
                }

                var userId = ReadScalar(item, "user_id");
                terms.Add(new ClassroomTermInfo(
                    term,
                    begin,
                    end,
                    string.IsNullOrWhiteSpace(userId) ? null : userId.Trim()));
            }
        }

        return terms;
    }

    public static IReadOnlyList<ClassroomWeekCourse> ParseWeekSchedules(
        string json,
        ClassroomTermInfo term,
        string phase)
    {
        ArgumentNullException.ThrowIfNull(term);

        using var document = ParseDocument(json, phase);
        var root = document.RootElement;
        RequireSuccess(root, phase);
        if (!root.TryGetProperty("result", out var result))
        {
            throw Schema(phase, "缺少 result 或类型不符。");
        }

        JsonElement list;
        if (result.ValueKind == JsonValueKind.Array)
        {
            list = result;
        }
        else if (result.ValueKind == JsonValueKind.Null)
        {
            return Array.Empty<ClassroomWeekCourse>();
        }
        else if (result.ValueKind == JsonValueKind.Object)
        {
            if (result.TryGetProperty("list", out var direct))
            {
                if (direct.ValueKind == JsonValueKind.Array)
                {
                    list = direct;
                }
                else if (direct.ValueKind == JsonValueKind.Null)
                {
                    return Array.Empty<ClassroomWeekCourse>();
                }
                else
                {
                    throw Schema(phase, "缺少 list 或类型不符。");
                }
            }
            else if (result.TryGetProperty("data", out var data) &&
                     data.ValueKind == JsonValueKind.Object &&
                     data.TryGetProperty("list", out var nested) &&
                     nested.ValueKind == JsonValueKind.Array)
            {
                list = nested;
            }
            else
            {
                // success=true 但没有 list/data 时按“该学期没有课程”处理，避免把空学期当成整体结构失败。
                return Array.Empty<ClassroomWeekCourse>();
            }
        }
        else
        {
            throw Schema(phase, "缺少 list 或类型不符。");
        }

        var courses = new List<ClassroomWeekCourse>();
        foreach (var dayItem in list.EnumerateArray())
        {
            if (dayItem.ValueKind != JsonValueKind.Object)
            {
                throw Schema(phase, "课表日期条目不是对象。");
            }

            var day = ReadDay(dayItem);
            AppendDayCourses(dayItem, day, courses, phase);
        }

        return courses;
    }

    /// <summary>
    /// 解析 get-my-course-month/day 响应。该接口不返回 {success,result} 包装，
    /// 而是直接返回 {code?, list:[...]} 或 list 对象；参考公开实现的 normalizeCourses。
    /// </summary>
    public static IReadOnlyList<ClassroomWeekCourse> ParseMyCourseMonth(
        string json,
        ClassroomTermInfo term,
        string phase)
    {
        ArgumentNullException.ThrowIfNull(term);

        using var document = ParseDocument(json, phase);
        var root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("success", out var success) &&
            success.ValueKind == JsonValueKind.False)
        {
            throw Schema(phase, "课程月历接口返回 success=false。");
        }

        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("code", out var code) &&
            code.ValueKind == JsonValueKind.Number &&
            code.TryGetInt32(out var codeValue) &&
            codeValue != 0)
        {
            throw Schema(phase, $"课程月历接口返回 code={codeValue}。");
        }

        JsonElement list;
        if (root.ValueKind == JsonValueKind.Object)
        {
            if (!root.TryGetProperty("list", out list) || list.ValueKind == JsonValueKind.Null)
            {
                return Array.Empty<ClassroomWeekCourse>();
            }
        }
        else if (root.ValueKind == JsonValueKind.Array)
        {
            list = root;
        }
        else
        {
            throw Schema(phase, "课程月历响应不是对象或数组。");
        }

        var courses = new List<ClassroomWeekCourse>();
        if (list.ValueKind == JsonValueKind.Array)
        {
            foreach (var dayItem in list.EnumerateArray())
            {
                if (dayItem.ValueKind == JsonValueKind.Object)
                {
                    var day = ReadDay(dayItem);
                    AppendMonthCourses(dayItem, day, courses, phase);
                }
            }
        }
        else if (list.ValueKind == JsonValueKind.Object)
        {
            foreach (var dayProperty in list.EnumerateObject())
            {
                if (dayProperty.Value.ValueKind == JsonValueKind.Object)
                {
                    AppendMonthCourses(dayProperty.Value, dayProperty.Name, courses, phase);
                }
            }
        }
        else
        {
            throw Schema(phase, "课程月历 list 不是数组或对象。");
        }

        return courses;
    }

    /// <summary>
    /// 课程月历的课程字段与周课表不同：参考实现使用 id/course_id/sub_id/title/course_name/teacher。
    /// 这里做兼容映射，不强制某一种字段名。
    /// </summary>
    private static void AppendMonthCourses(
        JsonElement dayItem,
        string? day,
        List<ClassroomWeekCourse> courses,
        string phase)
    {
        if (!dayItem.TryGetProperty("course", out var courseElement) ||
            courseElement.ValueKind == JsonValueKind.Null)
        {
            return;
        }

        // 真实月历响应的 list 项通常是 { section, course }，日期/时间可能在 section 中。
        dayItem.TryGetProperty("section", out var section);
        var sectionIsObject = section.ValueKind == JsonValueKind.Object;
        var sectionDay = sectionIsObject
            ? FirstNonEmpty(
                ReadDay(section),
                ExtractDate(ReadScalar(section, "start_time")),
                ExtractDate(ReadScalar(section, "time_text")),
                ExtractDate(ReadScalar(section, "class_time")))
            : null;
        var sectionStartAt = sectionIsObject ? ReadStartAt(section) : null;
        var sectionEndAt = sectionIsObject ? ReadEndAt(section) : null;
        var sectionSession = sectionIsObject
            ? ReadSessionText(section, sectionStartAt, sectionEndAt)
            : null;
        if (section.ValueKind == JsonValueKind.String)
        {
            var text = section.GetString();
            sectionDay = FirstNonEmpty(sectionDay, ExtractDate(text));
            sectionSession = FirstNonEmpty(sectionSession, ExtractTimeRange(text));
        }

        if (courseElement.ValueKind == JsonValueKind.Object)
        {
            AppendMonthCourse(
                courseElement,
                day,
                sectionDay,
                sectionStartAt,
                sectionEndAt,
                sectionSession,
                courses,
                phase);
            return;
        }

        if (courseElement.ValueKind != JsonValueKind.Array)
        {
            throw Schema(phase, "课程月历日期条目的 course 不是数组或对象。");
        }

        foreach (var course in courseElement.EnumerateArray())
        {
            AppendMonthCourse(
                course,
                day,
                sectionDay,
                sectionStartAt,
                sectionEndAt,
                sectionSession,
                courses,
                phase);
        }
    }

    private static void AppendMonthCourse(
        JsonElement course,
        string? day,
        string? sectionDay,
        long? sectionStartAt,
        long? sectionEndAt,
        string? sectionSession,
        List<ClassroomWeekCourse> courses,
        string phase)
    {
        if (course.ValueKind != JsonValueKind.Object)
        {
            throw Schema(phase, "课程月历课程条目不是对象。");
        }

        var lessonId = FirstNonEmpty(
            ReadScalar(course, "sub_id"),
            ReadScalar(course, "id"));
        var courseId = FirstNonEmpty(
            ReadScalar(course, "course_id"),
            ReadScalar(course, "id"));
        var courseName = FirstNonEmpty(
            ReadScalar(course, "title"),
            ReadScalar(course, "course_name"),
            ReadScalar(course, "course_title"));
        if (lessonId is null || courseId is null || courseName is null)
        {
            throw Schema(phase, "课程月历条目缺少 sub_id/id、course_id 或 title/course_name。");
        }

        var platformInfo = new List<string>(3);
        AddOptional(platformInfo, course, "teacher_name");
        AddOptional(platformInfo, course, "teacher");
        AddOptional(platformInfo, course, "realname");
        AddOptional(platformInfo, course, "room_name");
        AddOptional(platformInfo, course, "status_label");

        var loose = TryExtractLooseTime(course);
        var startAt = ReadStartAt(course) ?? sectionStartAt ?? loose.StartAt;
        var endAt = ReadEndAt(course) ?? sectionEndAt ?? loose.EndAt;
        var sessionText = ReadSessionText(course, startAt, endAt)
                          ?? sectionSession
                          ?? loose.Session;

        var effectiveDay = FirstNonEmpty(
            day,
            sectionDay,
            ReadDay(course),
            ExtractDate(ReadScalar(course, "start_time")),
            ExtractDate(ReadScalar(course, "time_text")),
            ExtractDate(ReadScalar(course, "class_time")),
            ExtractDate(ReadScalar(course, "start")),
            ExtractDate(ReadScalar(course, "begin")),
            ExtractDate(ReadScalar(course, "date_time")),
            loose.Day);

        courses.Add(new ClassroomWeekCourse(
            courseId.Trim(),
            courseName.Trim(),
            platformInfo,
            lessonId.Trim(),
            string.IsNullOrWhiteSpace(effectiveDay) ? null : effectiveDay.Trim(),
            startAt,
            endAt,
            ReadBool(course, "is_apply"),
            ReadInt(course, "status"),
            sessionText));
    }

    private static string? ExtractTimeRange(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var match = System.Text.RegularExpressions.Regex.Match(
            value,
            @"(\d{1,2}:\d{2})\s*[-~—至]\s*(\d{1,2}:\d{2})");
        return match.Success ? match.Groups[1].Value + "-" + match.Groups[2].Value : null;
    }

    private static string? ReadDay(JsonElement element) => FirstNonEmpty(
        ReadScalar(element, "day"),
        ReadScalar(element, "date"),
        ReadScalar(element, "course_date"),
        ReadScalar(element, "class_date"),
        ReadScalar(element, "lesson_date"),
        ReadScalar(element, "teaching_date"),
        ReadScalar(element, "start_date"),
        ReadScalar(element, "begin_date"),
        ReadScalar(element, "start_day"),
        ReadScalar(element, "date_str"));

    /// <summary>
    /// 仅用于本机脱敏诊断：输出课表响应的字段名和日期/时间字段值，输出课程名称、教师或用户标识。
    /// 正常路径不调用；只在解析出的课节全部缺少日期和时间时记录一次。
    /// </summary>
    internal static string DescribeScheduleShape(string json, string phase)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return $"rootType={root.ValueKind}";
            }

            var rootKeys = KeysOf(root, 16);
            JsonElement list;
            if (root.TryGetProperty("result", out var result) &&
                result.ValueKind == JsonValueKind.Object &&
                result.TryGetProperty("list", out var nested))
            {
                list = nested;
            }
            else if (!root.TryGetProperty("list", out list))
            {
                return $"root={rootKeys}; list=missing";
            }

            if (list.ValueKind == JsonValueKind.Object)
            {
                var firstProperty = list.EnumerateObject().FirstOrDefault();
                var item = firstProperty.Value;
                return $"root={rootKeys}; listType=object; itemKey={Safe(firstProperty.Name)}; " +
                       $"itemKeys={KeysOf(item, 30)}; {DescribeTimes(item)}";
            }

            if (list.ValueKind != JsonValueKind.Array || list.GetArrayLength() == 0)
            {
                return $"root={rootKeys}; listType={list.ValueKind}; length=0";
            }

            var itemKeys = new HashSet<string>(StringComparer.Ordinal);
            var courseKeys = new HashSet<string>(StringComparer.Ordinal);
            var courseSamples = new List<string>();
            var courseCount = 0;
            string? sectionText = null;
            foreach (var item in list.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                foreach (var property in item.EnumerateObject())
                {
                    itemKeys.Add(property.Name);
                }

                if (sectionText is null && item.TryGetProperty("section", out var section))
                {
                    sectionText = section.ValueKind == JsonValueKind.Object
                        ? $"sectionKeys={KeysOf(section, 30)}; {DescribeTimes(section)}"
                        : $"sectionType={section.ValueKind}; sectionValue={Safe(section.ToString())}";
                }

                if (!item.TryGetProperty("course", out var courses))
                {
                    continue;
                }

                var courseItems = courses.ValueKind == JsonValueKind.Array
                    ? courses.EnumerateArray()
                    : courses.ValueKind == JsonValueKind.Object
                        ? new[] { courses }.AsEnumerable()
                        : Enumerable.Empty<JsonElement>();
                foreach (var course in courseItems)
                {
                    if (course.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    courseCount++;
                    foreach (var property in course.EnumerateObject())
                    {
                        courseKeys.Add(property.Name);
                    }

                    if (courseSamples.Count < 3)
                    {
                        courseSamples.Add(DescribeTimes(course));
                    }
                }
            }

            return $"root={rootKeys}; items={list.GetArrayLength()}; itemKeys={string.Join(",", itemKeys.Take(30))}; " +
                   $"{sectionText ?? "section=missing"}; courseCount={courseCount}; " +
                   $"courseKeys={string.Join(",", courseKeys.Take(60))}; " +
                   $"courseTimes={string.Join(" | ", courseSamples)}";
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            return $"shapeUnavailable={exception.GetType().Name}";
        }
    }

    private static string KeysOf(JsonElement element, int max) =>
        element.ValueKind == JsonValueKind.Object
            ? string.Join(",", element.EnumerateObject().Select(property => property.Name).Take(max))
            : "type=" + element.ValueKind;

    private static string DescribeTimes(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return "times=type=" + element.ValueKind;
        }

        string[] names =
        [
            "day", "date", "course_date", "class_date", "lesson_date", "start_date", "begin_date",
            "start_at", "end_at", "begin_time", "end_time", "start_time", "time_text", "class_time",
            "start_timestamp", "end_timestamp", "start_ts", "end_ts"
        ];
        var parts = new List<string>();
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value))
            {
                continue;
            }

            var text = value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : value.ValueKind == JsonValueKind.Number
                    ? value.GetRawText()
                    : value.ValueKind.ToString();
            parts.Add(name + "=" + Safe(text));
        }

        return parts.Count == 0 ? "times=none" : "times=" + string.Join(",", parts);
    }

    private static string Safe(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "\"\"";
        }

        var text = value.Trim();
        return text.Length <= 40 ? text : text[..40] + "…";
    }

    /// <summary>
    /// 最后的保守回退：只在已知字段都没取到日期/时间时，扫描对象里的日期字符串、
    /// 时间范围和 Unix 时间戳。不读取课程名、教师等文本，避免把无关内容当成日期。
    /// </summary>
    private static (string? Day, long? StartAt, long? EndAt, string? Session) TryExtractLooseTime(
        JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return (null, null, null, null);
        }

        string? day = null;
        string? session = null;
        var stamps = new List<long>();
        var clocks = new List<string>();
        foreach (var property in element.EnumerateObject())
        {
            var value = property.Value;
            if (value.ValueKind == JsonValueKind.String)
            {
                var text = value.GetString();
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                day ??= ExtractDate(text);
                session ??= ExtractTimeRange(text);
                var clock = ExtractClock(text);
                if (clock is not null)
                {
                    clocks.Add(clock);
                }

                if (TryParseDateLike(text, out var parsedStamp))
                {
                    stamps.Add(parsedStamp);
                }
            }
            else if (value.ValueKind == JsonValueKind.Number)
            {
                var text = value.GetRawText();
                if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) &&
                    number > 1_000_000_000)
                {
                    var seconds = (long)number;
                    if (seconds > 100_000_000_000)
                    {
                        seconds /= 1000;
                    }

                    // 只接受 2000–2100 之间的时间戳，避免把课程/课节数字标识当成日期。
                    if (seconds is >= 946_684_800 and <= 4_102_444_800)
                    {
                        stamps.Add(seconds);
                    }
                }
            }
        }

        if (session is null && clocks.Count >= 2)
        {
            session = clocks[0] + "-" + clocks[1];
        }

        stamps = stamps.Where(stamp => stamp > 0).Distinct().OrderBy(stamp => stamp).ToList();
        if (stamps.Count > 0)
        {
            var start = stamps[0];
            var end = stamps.Count > 1 ? stamps[^1] : (long?)null;
            return (day, start, end, session);
        }

        return (day, null, null, session);
    }

    private static string? ExtractDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var match = System.Text.RegularExpressions.Regex.Match(
            value,
            @"\d{4}-\d{1,2}-\d{1,2}");
        return match.Success ? match.Value : null;
    }

    private static string? ExtractClock(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var match = System.Text.RegularExpressions.Regex.Match(
            value,
            @"\d{1,2}:\d{2}");
        return match.Success ? match.Value : null;
    }

    /// <summary>平台不同接口使用 start_at/begin_time/start_time 等不同字段；逐项回退，不猜测。</summary>
    private static long? ReadStartAt(JsonElement element) =>
        FirstUnixSeconds(
            element,
            "start_at",
            "begin_time",
            "start_timestamp",
            "begin_timestamp",
            "start_ts",
            "begin_ts",
            "start_sec",
            "begin_sec",
            "startTime",
            "beginTime",
            "start_time",
            "start",
            "begin");

    private static long? ReadEndAt(JsonElement element) =>
        FirstUnixSeconds(
            element,
            "end_at",
            "end_time",
            "end_timestamp",
            "finish_timestamp",
            "end_ts",
            "finish_ts",
            "end_sec",
            "finish_sec",
            "endTime",
            "finishTime",
            "end",
            "finish");

    private static long? FirstUnixSeconds(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            var value = TryReadUnixSeconds(element, name);
            if (value is not null)
            {
                return value;
            }
        }

        foreach (var name in names)
        {
            if (TryParseDateLike(ReadScalar(element, name), out var instant))
            {
                return instant;
            }
        }

        return null;
    }

    private static string? ReadSessionText(JsonElement course, long? startAt, long? endAt)
    {
        var explicitText = FirstNonEmpty(
            ReadScalar(course, "time_text"),
            ReadScalar(course, "class_time"),
            ReadScalar(course, "time"),
            ReadScalar(course, "time_range"));
        if (explicitText is not null)
        {
            return explicitText;
        }

        var combined = CombineClock(
            ReadScalar(course, "start_time"),
            ReadScalar(course, "end_time"));
        if (combined is not null)
        {
            return combined;
        }

        var startClock = ExtractClock(ReadScalar(course, "start_time"));
        var endClock = ExtractClock(ReadScalar(course, "end_time"));
        if (startClock is not null && endClock is not null)
        {
            return startClock + "-" + endClock;
        }

        // 没有字符串时间时仍保留原始 Unix 时间，由课堂内容服务按本机时区格式化。
        return null;
    }

    private static bool TryParseDateLike(string? value, out long unixSeconds)
    {
        unixSeconds = 0;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var text = value.Trim();
        if (text.IndexOfAny(new[] { '-', '/', 'T' }) < 0)
        {
            return false;
        }

        if (!DateTimeOffset.TryParse(
                text,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces,
                out var instant) &&
            !DateTimeOffset.TryParse(
                text,
                CultureInfo.GetCultureInfo("zh-CN"),
                DateTimeStyles.AllowWhiteSpaces,
                out instant))
        {
            return false;
        }

        unixSeconds = instant.ToUnixTimeSeconds();
        return true;
    }

    private static string? CombineClock(string? start, string? end)
    {
        if (string.IsNullOrWhiteSpace(start) || string.IsNullOrWhiteSpace(end))
        {
            return null;
        }

        var startText = start.Trim();
        var endText = end.Trim();
        return IsClock(startText) && IsClock(endText) ? $"{startText}-{endText}" : null;
    }

    private static bool IsClock(string value) =>
        System.Text.RegularExpressions.Regex.IsMatch(value, @"^\d{1,2}:\d{2}(:\d{2})?$");

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static void AppendDayCourses(
        JsonElement dayItem,
        string? day,
        List<ClassroomWeekCourse> courses,
        string phase)
    {
        if (!dayItem.TryGetProperty("course", out var courseArray) ||
            courseArray.ValueKind == JsonValueKind.Null)
        {
            // 平台对没有课程的日期可能省略 course 或返回 null；这是有效空日期，不是结构失败。
            return;
        }

        if (courseArray.ValueKind != JsonValueKind.Array)
        {
            throw Schema(phase, "课表日期条目的 course 不是数组。");
        }

        foreach (var course in courseArray.EnumerateArray())
        {
            if (course.ValueKind != JsonValueKind.Object)
            {
                throw Schema(phase, "课表课程条目不是对象。");
            }

            var lessonId = RequiredText(course, "id", phase, trim: true);
            var courseId = RequiredText(course, "course_id", phase, trim: true);
            var courseName = RequiredText(course, "course_title", phase, trim: false);
            var platformInfo = new List<string>(3);
            AddOptional(platformInfo, course, "teacher_name");
            AddOptional(platformInfo, course, "room_name");
            AddOptional(platformInfo, course, "status_label");

            var loose = TryExtractLooseTime(course);
            var startAt = ReadStartAt(course) ?? loose.StartAt;
            var endAt = ReadEndAt(course) ?? loose.EndAt;
            var sessionText = ReadSessionText(course, startAt, endAt) ?? loose.Session;
            var effectiveDay = FirstNonEmpty(
                day,
                ReadDay(course),
                ExtractDate(ReadScalar(course, "start_time")),
                ExtractDate(ReadScalar(course, "begin_time_text")),
                ExtractDate(ReadScalar(course, "time_text")),
                ExtractDate(ReadScalar(course, "class_time")),
                loose.Day);

            courses.Add(new ClassroomWeekCourse(
                courseId,
                courseName,
                platformInfo,
                lessonId,
                string.IsNullOrWhiteSpace(effectiveDay) ? null : effectiveDay.Trim(),
                startAt,
                endAt,
                ReadBool(course, "is_apply"),
                ReadInt(course, "status"),
                sessionText));
        }
    }

    /// <summary>
    /// 解析语音文字并按平台原文输出。返回空字符串表示“暂无”，不是失败；
    /// 时间点只用于显示，正文逐字保留。
    /// </summary>
    public static string ParseTranscript(string json, string phase)
    {
        using var document = ParseDocument(json, phase);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw Schema(phase, "语音文字响应不是对象。");
        }

        var code = ReadInt(root, "code");
        if (code is null)
        {
            throw Schema(phase, "语音文字响应缺少 code。");
        }

        if (code == 10002)
        {
            return string.Empty;
        }

        if (code != 0)
        {
            throw Schema(phase, $"语音文字返回 code={code}。");
        }

        if (!root.TryGetProperty("list", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            throw Schema(phase, "语音文字响应缺少 list 数组。");
        }

        if (list.GetArrayLength() == 0)
        {
            return string.Empty;
        }

        var first = list[0];
        if (first.ValueKind != JsonValueKind.Object)
        {
            throw Schema(phase, "语音文字 list 条目不是对象。");
        }

        if (!first.TryGetProperty("all_content", out var content))
        {
            throw Schema(phase, "语音文字缺少 all_content。");
        }

        if (content.ValueKind == JsonValueKind.Null)
        {
            return string.Empty;
        }

        if (content.ValueKind != JsonValueKind.Array)
        {
            throw Schema(phase, "all_content 不是数组。");
        }

        if (content.GetArrayLength() == 0)
        {
            return string.Empty;
        }

        var transType = ReadScalar(first, "trans_type");
        if (transType is null)
        {
            throw Schema(phase, "语音文字缺少 trans_type。");
        }

        return transType.Trim() switch
        {
            "ainew" => ParseAinew(content, phase),
            "ai" => ParseAi(content, phase),
            _ => throw Schema(phase, $"未知的语音文字类型 {transType.Trim()}。")
        };
    }

    /// <summary>读取 search-ppt 响应的 total；缺失或格式异常返回 null（按单页处理）。</summary>
    public static int? TryReadPptTotal(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object
                ? ReadInt(document.RootElement, "total")
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>解析 search-ppt 的 list；content 是 JSON 字符串，按 created_sec 升序输出。</summary>
    public static IReadOnlyList<ClassroomPptImageSource> ParsePptList(string json, string phase)
    {
        using var document = ParseDocument(json, phase);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw Schema(phase, "课件响应不是对象。");
        }

        var code = ReadInt(root, "code");
        if (code is not null && code != 0)
        {
            throw Schema(phase, $"课件返回 code={code}。");
        }

        if (!root.TryGetProperty("list", out var list) ||
            list.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return Array.Empty<ClassroomPptImageSource>();
        }

        if (list.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<ClassroomPptImageSource>();
        }

        var images = new List<ClassroomPptImageSource>(list.GetArrayLength());
        foreach (var item in list.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (!item.TryGetProperty("content", out var contentElement))
            {
                continue;
            }

            JsonDocument? contentDocument = null;
            JsonElement content;
            if (contentElement.ValueKind == JsonValueKind.String)
            {
                try
                {
                    contentDocument = JsonDocument.Parse(contentElement.GetString() ?? string.Empty);
                }
                catch (JsonException)
                {
                    continue;
                }

                content = contentDocument.RootElement;
            }
            else
            {
                content = contentElement;
            }

            using (contentDocument)
            {
                if (content.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var url = ReadScalar(content, "pptimgurl");
                if (string.IsNullOrWhiteSpace(url))
                {
                    continue;
                }

                // 参考实现不依赖 created_sec；没有它时保留平台 list 顺序。
                var created = ReadLong(content, "created_sec") ?? 0;
                images.Add(new ClassroomPptImageSource(url.Trim(), created));
            }
        }

        return images;
    }

    /// <summary>从用户信息响应中尽力读取平台 user_id；结构不固定，找不到返回 null。</summary>
    public static string? TryReadUserId(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return FindUserId(document.RootElement, depth: 0);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        {
            return null;
        }
    }

    private static string? FindUserId(JsonElement element, int depth)
    {
        if (element.ValueKind != JsonValueKind.Object || depth > 3)
        {
            return null;
        }

        foreach (var name in new[] { "user_id", "userId" })
        {
            var value = ReadScalar(element, name);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        if (element.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
        {
            foreach (var name in new[] { "user_id", "userId", "id" })
            {
                var value = ReadScalar(data, name);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value.Trim();
                }
            }
        }

        foreach (var property in element.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Object &&
                property.Name is "data" or "result" or "user" or "info")
            {
                var nested = FindUserId(property.Value, depth + 1);
                if (nested is not null)
                {
                    return nested;
                }
            }
        }

        return null;
    }

    /// <summary>从 JWT 载荷读取 user_id；失败返回 null，不记录 token 或载荷内容。</summary>
    public static string? TryReadJwtUserId(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        try
        {
            var parts = token.Split('.');
            if (parts.Length < 2)
            {
                return null;
            }

            var payload = Base64UrlDecode(parts[1]);
            using var document = JsonDocument.Parse(payload);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var userId = ReadScalar(document.RootElement, "user_id");
            return string.IsNullOrWhiteSpace(userId) ? null : userId.Trim();
        }
        catch (Exception exception) when (
            exception is FormatException or JsonException or ArgumentException or OverflowException)
        {
            return null;
        }
    }

    private static string ParseAinew(JsonElement content, string phase)
    {
        var builder = new StringBuilder();
        foreach (var item in content.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                throw Schema(phase, "ainew 条目不是对象。");
            }

            if (!item.TryGetProperty("Text", out var textProperty) ||
                textProperty.ValueKind != JsonValueKind.String)
            {
                throw Schema(phase, "ainew 条目缺少字符串 Text。");
            }

            var text = textProperty.GetString() ?? string.Empty;
            if (text.Length == 0)
            {
                continue;
            }

            if (!TryReadSeconds(item, "BeginSec", out var begin))
            {
                throw Schema(phase, "ainew 条目缺少数字 BeginSec。");
            }

            AppendSegment(builder, begin, text);
        }

        return builder.ToString();
    }

    private static string ParseAi(JsonElement content, string phase)
    {
        var builder = new StringBuilder();
        foreach (var item in content.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                throw Schema(phase, "ai 条目不是对象。");
            }

            // 平台 ai 变体没有核实样本：只接受明确存在的 Text/TransText，不猜其他字段。
            var hasTextProperty = false;
            string? text = null;
            foreach (var name in new[] { "Text", "TransText" })
            {
                if (!item.TryGetProperty(name, out var property))
                {
                    continue;
                }

                hasTextProperty = true;
                if (property.ValueKind == JsonValueKind.Null)
                {
                    continue;
                }

                if (property.ValueKind != JsonValueKind.String)
                {
                    throw Schema(phase, $"ai 条目的 {name} 不是字符串。");
                }

                var value = property.GetString();
                if (string.IsNullOrEmpty(value))
                {
                    continue;
                }

                text ??= value;
            }

            if (!hasTextProperty)
            {
                throw Schema(phase, "ai 条目缺少文本字段 Text/TransText。");
            }

            if (string.IsNullOrEmpty(text))
            {
                continue;
            }

            double? seconds = null;
            foreach (var name in new[] { "time", "BeginSec" })
            {
                if (TryReadSeconds(item, name, out var value))
                {
                    seconds = value;
                    break;
                }
            }

            if (seconds is null && item.TryGetProperty("time", out _))
            {
                throw Schema(phase, "ai 条目的 time 无法解析。");
            }

            AppendSegment(builder, seconds, text);
        }

        return builder.ToString();
    }

    private static void AppendSegment(StringBuilder builder, double? seconds, string text)
    {
        if (builder.Length > 0)
        {
            builder.Append('\n');
        }

        if (seconds is { } value)
        {
            builder.Append('[').Append(FormatSeconds(value)).Append("] ");
        }

        builder.Append(text);
    }

    private static string FormatSeconds(double seconds)
    {
        var clamped = Math.Max(0, Math.Min(seconds, long.MaxValue));
        var total = (long)Math.Floor(clamped);
        var minutes = total / 60;
        var remainder = total % 60;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{minutes:D2}:{remainder:D2}");
    }

    private static bool TryParseDate(string? text, out DateOnly value)
    {
        value = default;
        return !string.IsNullOrWhiteSpace(text) &&
               DateOnly.TryParseExact(
                   text.Trim(),
                   "yyyy-MM-dd",
                   CultureInfo.InvariantCulture,
                   DateTimeStyles.None,
                   out value);
    }

    private static bool IsTermGroupName(string name)
    {
        if (name.Length < 2 || (name[0] != 'a' && name[0] != 'A'))
        {
            return false;
        }

        for (var index = 1; index < name.Length; index++)
        {
            if (!char.IsAsciiDigit(name[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static JsonDocument ParseDocument(string json, string phase)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw Schema(phase, "响应为空。");
        }

        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException exception)
        {
            throw new AdapterFailureException(
                SourceFailureReason.SchemaChanged,
                phase,
                "响应不是有效 JSON。",
                exception);
        }
    }

    private static void RequireSuccess(JsonElement root, string phase)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw Schema(phase, "响应不是对象。");
        }

        if (!root.TryGetProperty("success", out var success) ||
            success.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw Schema(phase, "响应缺少布尔 success。");
        }

        if (success.ValueKind != JsonValueKind.True)
        {
            throw Schema(phase, "接口返回 success=false。");
        }
    }

    private static JsonElement RequireProperty(
        JsonElement element,
        string name,
        JsonValueKind kind,
        string phase)
    {
        if (!element.TryGetProperty(name, out var property) || property.ValueKind != kind)
        {
            throw Schema(phase, $"缺少 {name} 或类型不符。");
        }

        return property;
    }

    private static string RequiredText(
        JsonElement element,
        string name,
        string phase,
        bool trim)
    {
        var value = ReadScalar(element, name);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw Schema(phase, $"缺少必需字段 {name}。");
        }

        return trim ? value.Trim() : value;
    }

    private static void AddOptional(List<string> values, JsonElement element, string name)
    {
        var value = ReadScalar(element, name);
        if (!string.IsNullOrWhiteSpace(value))
        {
            values.Add(value.Trim());
        }
    }

    private static string? ReadScalar(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString(),
            JsonValueKind.Number => property.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
    }

    private static int? ReadInt(JsonElement element, string name)
    {
        var text = ReadScalar(element, name);
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    private static long? ReadLong(JsonElement element, string name)
    {
        var text = ReadScalar(element, name);
        return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    private static bool ReadBool(JsonElement element, string name)
    {
        var text = ReadScalar(element, name);
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return text.Trim() switch
        {
            "true" or "1" => true,
            "false" or "0" => false,
            _ => false
        };
    }

    private static long? TryReadUnixSeconds(JsonElement element, string name)
    {
        var text = ReadScalar(element, name);
        if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            // 少数响应把时间戳写成浮点字符串。
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var doubleValue) ||
                doubleValue <= 0)
            {
                return null;
            }

            value = (long)doubleValue;
        }

        if (value <= 0)
        {
            return null;
        }

        // 兼容毫秒时间戳；正常秒级时间戳约 1.7e9，毫秒约 1.7e12。
        if (value > 100_000_000_000)
        {
            value /= 1000;
        }

        return value > 0 ? value : null;
    }

    private static bool TryReadSeconds(JsonElement element, string name, out double value)
    {
        value = 0;
        if (!element.TryGetProperty(name, out var property))
        {
            return false;
        }

        switch (property.ValueKind)
        {
            case JsonValueKind.Number when property.TryGetDouble(out value):
                return true;
            case JsonValueKind.String:
                return TryParseTimeText(property.GetString(), out value);
            default:
                return false;
        }
    }

    /// <summary>把 "65.4"、"00:03"、"01:02:03" 统一转成秒；只用于显示时间点。</summary>
    private static bool TryParseTimeText(string? text, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.Trim();
        if (double.TryParse(
                trimmed,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out value))
        {
            return true;
        }

        var parts = trimmed.Split(':');
        if (parts.Length is not (2 or 3) ||
            !parts.All(part => int.TryParse(
                part,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out _)))
        {
            return false;
        }

        var numbers = parts
            .Select(part => int.Parse(part, NumberStyles.Integer, CultureInfo.InvariantCulture))
            .ToArray();
        value = parts.Length == 2
            ? numbers[0] * 60d + numbers[1]
            : numbers[0] * 3600d + numbers[1] * 60d + numbers[2];
        return value >= 0;
    }

    private static byte[] Base64UrlDecode(string value)
    {
        var normalized = value.Replace('-', '+').Replace('_', '/');
        normalized = (normalized.Length % 4) switch
        {
            2 => normalized + "==",
            3 => normalized + "=",
            _ => normalized
        };
        return Convert.FromBase64String(normalized);
    }

    private static AdapterFailureException Schema(string phase, string message) =>
        new(SourceFailureReason.SchemaChanged, phase, message);
}
