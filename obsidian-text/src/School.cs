using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Shike;

// The interface is deliberately small: future local providers return normalized records;
// they do not receive access to notes, attachments or the local database.
public interface ISchoolProvider : IDisposable
{
    Task Login(string username, string password);
    Task<SchoolBatch> Fetch(Settings settings, string captcha);
    Task<byte[]> Captcha();
}
public sealed record SchoolBatch(List<SchoolRecord> Courses, List<SchoolRecord> Exams, List<string> Warnings);
public sealed class CaptchaRequiredException : Exception { public CaptchaRequiredException() : base("教务系统需要验证码，请填写后重新读取。") { } }

public sealed class ZjuUndergraduateProvider : ISchoolProvider
{
    const string Cas = "https://zjuam.zju.edu.cn/cas/";
    const string Jw = "https://zdbk.zju.edu.cn/jwglxt/";
    readonly CookieContainer cookies = new();
    readonly HttpClient client;
    public ZjuUndergraduateProvider()
    {
        client = new HttpClient(new HttpClientHandler { CookieContainer = cookies, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(25) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) Shike/0.2");
    }
    static void CheckHost(Uri uri)
    {
        if (uri.Scheme != "https" || (uri.Host != "zjuam.zju.edu.cn" && uri.Host != "zdbk.zju.edu.cn")) throw new InvalidOperationException("学校返回了未支持的跳转地址，已停止连接。");
    }
    async Task<HttpResponseMessage> GetFollowing(Uri uri)
    {
        for (int n = 0; n < 10; n++)
        {
            CheckHost(uri);
            var res = await client.GetAsync(uri);
            if ((int)res.StatusCode is >= 300 and < 400 && res.Headers.Location != null)
            {
                var next = new Uri(uri, res.Headers.Location);
                // Some CAS deployments advertise the same school callback with HTTP.
                if (next.Scheme == "http" && next.Host == "zdbk.zju.edu.cn") next = new UriBuilder(next) { Scheme = "https", Port = -1 }.Uri;
                uri = next; res.Dispose(); continue;
            }
            res.EnsureSuccessStatusCode(); return res;
        }
        throw new InvalidOperationException("学校登录跳转次数过多，请稍后重试。");
    }
    public async Task Login(string username, string password)
    {
        using var page = await client.GetAsync(Cas + "login"); page.EnsureSuccessStatusCode();
        var html = await page.Content.ReadAsStringAsync();
        var input = Regex.Matches(html, "<input\\b[^>]*>", RegexOptions.IgnoreCase).Cast<Match>().FirstOrDefault(m => Regex.IsMatch(m.Value, "name\\s*=\\s*[\"']execution[\"']", RegexOptions.IgnoreCase));
        var execution = input == null ? "" : Regex.Match(input.Value, "value\\s*=\\s*[\"']([^\"']+)", RegexOptions.IgnoreCase).Groups[1].Value;
        if (execution.Length == 0) throw new InvalidOperationException("无法识别学校登录页。可能需要校园网络，或学校认证方式已变更。");
        using var pub = await client.GetAsync(Cas + "v2/getPubKey"); pub.EnsureSuccessStatusCode();
        using var key = JsonDocument.Parse(await pub.Content.ReadAsStringAsync());
        var modulus = BigInteger.Parse("0" + key.RootElement.GetProperty("modulus").GetString(), NumberStyles.HexNumber);
        var exponent = BigInteger.Parse("0" + key.RootElement.GetProperty("exponent").GetString(), NumberStyles.HexNumber);
        var number = new BigInteger(Encoding.UTF8.GetBytes(password), true, true);
        var encrypted = BigInteger.ModPow(number, exponent, modulus).ToString("x").TrimStart('0').PadLeft(128, '0');
        using var login = await client.PostAsync(Cas + "login", new FormUrlEncodedContent(new Dictionary<string, string> { ["username"] = username, ["password"] = encrypted, ["execution"] = WebUtility.HtmlDecode(execution), ["_eventId"] = "submit", ["rememberMe"] = "false" }));
        var sso = cookies.GetCookies(new Uri(Cas)).Cast<Cookie>().FirstOrDefault(c => c.Name == "iPlanetDirectoryPro" && c.Value.Length > 0);
        if (sso == null) throw new InvalidOperationException("学校认证未成功。请检查学号、密码或学校的额外认证要求。本版本不绕过验证码或多因素认证。");
        var url = Cas + "login?service=" + Uri.EscapeDataString(Jw + "xtgl/login_ssologin.html");
        using var access = await GetFollowing(new Uri(url));
        if (!cookies.GetCookies(new Uri(Jw)).Cast<Cookie>().Any(c => c.Name == "JSESSIONID")) throw new InvalidOperationException("未取得本科教务会话，请检查校园网络或重新登录。");
    }
    async Task<string> Post(string relative, Dictionary<string, string> form)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, Jw + relative);
        req.Headers.Referrer = new Uri(Jw + "xtgl/index_initMenu.html");
        req.Headers.Add("X-Requested-With", "XMLHttpRequest");
        req.Content = new FormUrlEncodedContent(form);
        using var res = await client.SendAsync(req);
        if ((int)res.StatusCode is >= 300 and < 400) throw new InvalidOperationException("学校会话已过期，请重新连接。");
        res.EnsureSuccessStatusCode();
        var text = await res.Content.ReadAsStringAsync();
        if (text.Contains("captcha_error")) throw new CaptchaRequiredException();
        if (text.TrimStart().StartsWith('<')) throw new InvalidOperationException("学校返回了登录页或错误页，请重新连接。");
        return text;
    }
    public async Task<SchoolBatch> Fetch(Settings settings, string captcha)
    {
        var table = await Post("kbcx/xskbcx_cxXsKb.html", new() { ["xnm"] = settings.Year.ToString(), ["xqm"] = settings.Semester, ["captcha_value"] = captcha });
        var exams = await Post("xskscx/kscx_cxXsgrksIndex.html?doType=query&queryModel.showCount=5000", new() { ["xnm"] = settings.Year.ToString(), ["xqm"] = settings.Semester });
        return SchoolParser.Parse(table, exams, settings);
    }
    public async Task<byte[]> Captcha() => await client.GetByteArrayAsync(Jw + "kaptcha?time=" + DateTimeOffset.Now.ToUnixTimeMilliseconds());
    public void Dispose() => client.Dispose();
}

public static class SchoolParser
{
    static string Clean(string value) => Regex.Replace(WebUtility.HtmlDecode(Regex.Replace(WebUtility.HtmlDecode(value), "<[^>]*>", "")), @"[\t ]+", " ").Trim();
    public static string CourseLocation(JsonElement row, string[] lines)
    {
        string room = Clean(S(row, "cdmc", "jasmc", "jsmc", "skdd", "jxdd", "jxcdmc"));
        if (room.Length == 0 && lines.Length >= 4) room = Clean(lines[3].Split("zwf")[0]);
        room = Regex.Replace(room, @"^(上课地点|地点|教室)\s*[:：]\s*", "");
        string campus = Clean(S(row, "xqmc", "xiaoqumc"));
        if (campus.Length > 0 && !room.Contains(campus.Replace("校区", ""))) room = campus + (room.Length > 0 ? " · " + room : " · 教室待公布");
        return room;
    }
    static string S(JsonElement r, params string[] keys)
    {
        foreach (var k in keys) if (r.TryGetProperty(k, out var v) && v.ValueKind != JsonValueKind.Null && v.ToString().Length > 0) return v.ToString();
        return "";
    }
    public static List<(TimeSpan Start, TimeSpan End)> Periods(string text)
    {
        var result = new List<(TimeSpan, TimeSpan)>();
        foreach (var p in text.Replace('；', ';').Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var halves = p.Split('-');
            if (halves.Length != 2 || !TimeSpan.TryParseExact(halves[0], new[] { "h\\:mm", "hh\\:mm" }, CultureInfo.InvariantCulture, out var start) || !TimeSpan.TryParseExact(halves[1], new[] { "h\\:mm", "hh\\:mm" }, CultureInfo.InvariantCulture, out var end) || end <= start || end.TotalHours >= 24)
                throw new InvalidOperationException("节次时间格式错误，请使用 08:00-08:45;08:50-09:35。");
            result.Add((start, end));
        }
        if (result.Count == 0) throw new InvalidOperationException("请填写节次时间。");
        return result;
    }
    public static SchoolBatch Parse(string timetable, string exams, Settings settings)
    {
        var lessons = new List<SchoolRecord>(); var examList = new List<SchoolRecord>(); var warnings = new List<string>();
        var periods = Periods(settings.Periods);
        using var coursesDoc = JsonDocument.Parse(timetable);
        if (!coursesDoc.RootElement.TryGetProperty("kbList", out var rows) || rows.ValueKind != JsonValueKind.Array) throw new InvalidOperationException("学校课表格式未识别，未更改本地数据。");
        int rowNumber = 0;
        foreach (var row in rows.EnumerateArray())
        {
            rowNumber++;
            if (S(row, "sfyjskc") == "1") { warnings.Add($"第 {rowNumber} 条课程为研究生课程，未导入。"); continue; }
            var content = Regex.Replace(WebUtility.HtmlDecode(S(row, "kcb")), @"<br\s*/?>|</p>|</div>", "\n", RegexOptions.IgnoreCase);
            var lines = Regex.Replace(content, "<[^>]+>", "").Replace("\r", "").Split('\n');
            string name = S(row, "kcmc"); if (name == "" && lines.Length > 0) name = lines[0].Trim();
            string location = CourseLocation(row, lines);
            string teacher = Clean(S(row, "xm", "jsxm")); if (teacher.Length == 0 && lines.Length >= 3) teacher = Clean(lines[2]);
            if (name.Length == 0 || !int.TryParse(S(row, "xqj"), out int weekday) || weekday < 1 || weekday > 7 || !int.TryParse(S(row, "djj"), out int first) || !int.TryParse(S(row, "skcd"), out int duration) || first < 1 || duration < 1 || first + duration - 1 > periods.Count)
            { warnings.Add($"第 {rowNumber} 条课程无法识别名称、星期或节次，未导入。"); continue; }
            var half = S(row, "xxq"); var dsz = S(row, "dsz"); var zcd = S(row, "zcd", "zcs");
            if (dsz != "" && dsz != "0" && dsz != "1" && dsz != "2") { warnings.Add($"{name}：单双周规则无法识别，未导入。"); continue; }
            if (zcd.Length > 0 && !Regex.IsMatch(zcd, @"^\d+(-\d+)?(,\d+(-\d+)?)*(周)?(\([单双]\))?$")) { warnings.Add($"{name}：周次范围无法识别，未导入。"); continue; }
            var schoolId = S(row, "jxb_id", "kch_id", "kch", "xkkh"); if (schoolId == "") schoolId = name;
            for (int week = 1; week <= settings.Weeks; week++)
            {
                bool firstHalf = half.Contains('秋') || half.Contains('春'), secondHalf = half.Contains('冬') || half.Contains('夏');
                if (firstHalf && !secondHalf && week >= settings.SecondHalfWeek || secondHalf && !firstHalf && week < settings.SecondHalfWeek) continue;
                if (dsz == "0" && week % 2 == 0 || dsz == "1" && week % 2 == 1) continue;
                if (zcd.Contains('单') && week % 2 == 0 || zcd.Contains('双') && week % 2 == 1) continue;
                if (zcd.Length > 0)
                {
                    bool included = false;
                    foreach (Match m in Regex.Matches(zcd.Split('周')[0], @"(\d+)(?:-(\d+))?"))
                    { var a = int.Parse(m.Groups[1].Value); var b = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : a; included |= week >= a && week <= b; }
                    if (!included) continue;
                }
                var day = settings.TermMonday.Date.AddDays((week - 1) * 7 + weekday - 1);
                lessons.Add(new() { Key = "course-" + Engine.Hash($"{schoolId}|{weekday}|{first}|{week}"), Kind = "课程", Title = name, Start = day + periods[first - 1].Start, End = day + periods[first + duration - 2].End, Location = location, Teacher = teacher });
            }
        }
        using var examsDoc = JsonDocument.Parse(exams);
        if (!examsDoc.RootElement.TryGetProperty("items", out var examRows) || examRows.ValueKind != JsonValueKind.Array) throw new InvalidOperationException("学校考试数据格式未识别，未更改本地数据。");
        foreach (var row in examRows.EnumerateArray())
        {
            string name = S(row, "kcmc", "kc_mc", "name");
            var time = S(row, "kssj", "kssjms", "ksrq");
            var dateMatch = Regex.Match(time, @"(20\d{2})[-/年](\d{1,2})[-/月](\d{1,2})");
            var hours = Regex.Matches(time, @"(\d{1,2})[:：](\d{2})");
            if (name == "" || !dateMatch.Success || hours.Count < 2) { warnings.Add($"考试「{(name == "" ? "未命名" : name)}」尚无可识别的完整时间，未导入。请在教务系统核对。"); continue; }
            try
            {
                var day = new DateTime(int.Parse(dateMatch.Groups[1].Value), int.Parse(dateMatch.Groups[2].Value), int.Parse(dateMatch.Groups[3].Value));
                var start = day + new TimeSpan(int.Parse(hours[0].Groups[1].Value), int.Parse(hours[0].Groups[2].Value), 0);
                var end = day + new TimeSpan(int.Parse(hours[1].Groups[1].Value), int.Parse(hours[1].Groups[2].Value), 0);
                if (end <= start || end.Date != day || start.Date != day) throw new FormatException();
                var id = S(row, "ksqz_id", "ks_id", "xkkh", "kch_id");
                if (id == "") id = name;
                var type = S(row, "kslxmc", "kslx", "ksxz");
                examList.Add(new() { Key = "exam-" + Engine.Hash(id + "|" + type), Title = name, Kind = "考试", Start = start, End = end, Location = S(row, "cdmc", "jsmc", "ksdd", "jasmc") + (S(row, "zwxh") == "" ? "" : " · 座位 " + S(row, "zwxh")) });
            }
            catch (Exception ex) when (ex is FormatException or ArgumentOutOfRangeException) { warnings.Add($"考试「{name}」时间不合法，未导入。"); }
        }
        var duplicates = lessons.GroupBy(l => l.Key).Where(g => g.Count() > 1).ToList();
        foreach (var g in duplicates) if (g.Skip(1).Any(x => !x.Same(g.First()))) throw new InvalidOperationException("课表存在无法区分的重复课程，请先使用手动记录。");
        if (examList.GroupBy(e => e.Key).Any(g => g.Count() > 1)) throw new InvalidOperationException("考试数据存在无法区分的重复记录，未导入，请在学校系统核对。");
        return new(lessons.DistinctBy(l => l.Key).Select(l => AcademicCalendar.Adjust(l, settings.ApplyAcademicHolidays)).ToList(), examList, warnings);
    }
}
