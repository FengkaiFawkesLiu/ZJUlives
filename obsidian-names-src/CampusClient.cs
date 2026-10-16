using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Shike;

// School sessions live in memory. No HTTP bodies, tokens or credentials are logged.
public sealed class CampusClient : IDisposable
{
    const string Cas = "https://zjuam.zju.edu.cn/cas/";
    const string Courses = "https://courses.zju.edu.cn";
    const string Library = "https://api.lib.zju.edu.cn";
    readonly CookieContainer cookies = new();
    readonly HttpClient http;
    string libraryToken = "", borrower = "";
    bool coursesReady;
    SchoolCredential? credential;
    public List<string> CourseWarnings { get; } = new();
    public string Stage { get; private set; } = "学校连接";
    public string Account { get; private set; } = "";
    public CampusClient() { http = new HttpClient(new HttpClientHandler { CookieContainer = cookies, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(25) }; Configure(); }
    internal CampusClient(HttpMessageHandler handler) { http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(25) }; Configure(); }
    void Configure() { http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36 Shike/0.7.3"); }
    // Older school redirects can advertise HTTP for an endpoint that supports HTTPS.
    // Upgrade only exact known service hosts; never send credentials or tickets over HTTP.
    internal static Uri SecureServiceUri(Uri uri)
    {
        if (uri.Scheme == "http" && uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo) && new[] { "zjuam.zju.edu.cn", "identity.zju.edu.cn", "courses.zju.edu.cn", "api.lib.zju.edu.cn" }.Contains(uri.Host))
            return new UriBuilder(uri) { Scheme = "https", Port = -1 }.Uri;
        return uri;
    }
    public static bool Allowed(Uri u) => u.Scheme == "https" && u.IsDefaultPort && string.IsNullOrEmpty(u.UserInfo) && new[] { "zjuam.zju.edu.cn", "identity.zju.edu.cn", "courses.zju.edu.cn", "api.lib.zju.edu.cn" }.Contains(u.Host);
    async Task<HttpResponseMessage> Send(HttpRequestMessage request, CancellationToken ct)
    {
        if (!Allowed(request.RequestUri!)) throw new InvalidDataException(Stage + "：尚未支持跳转主机 " + request.RequestUri!.Host + "（" + request.RequestUri.Scheme + "）。本地记录保留。");
        try { return await http.SendAsync(request, ct); }
        catch (HttpRequestException ex) { throw new InvalidDataException(Stage + "：连接 " + request.RequestUri!.Host + " 失败（" + ex.HttpRequestError + "）。请检查校园网络或代理设置。", ex); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new InvalidDataException(Stage + "：连接 " + request.RequestUri!.Host + " 超时，请重试。"); }
    }
    async Task<string> Follow(Uri uri, CancellationToken ct, bool libraryCallback = false)
    {
        bool reauthenticated = false;
        for (int n = 0; n < 16; n++)
        {
            // Read OAuth code without sending it to the historical unencrypted callback.
            if (libraryCallback && uri.Host == "m.lib.zju.edu.cn" && uri.AbsolutePath == "/pages/wechat/auth")
            {
                var m = Regex.Match(uri.Query, @"(?:\?|&)code=([^&]+)");
                if (!m.Success) throw new InvalidDataException("图书馆授权未返回有效结果。");
                return Uri.UnescapeDataString(m.Groups[1].Value);
            }
            uri = SecureServiceUri(uri);
            using var req = new HttpRequestMessage(HttpMethod.Get, uri);
            using var res = await Send(req, ct);
            if ((int)res.StatusCode is >= 300 and < 400)
            {
                if (res.Headers.Location == null) throw new InvalidDataException("认证跳转不完整。");
                uri = new Uri(uri, res.Headers.Location); continue;
            }
            CheckStatus(res); var html = await res.Content.ReadAsStringAsync(ct);
            if (Execution(html).Length > 0 && uri.Host == "zjuam.zju.edu.cn" && uri.AbsolutePath == "/cas/login" && credential != null)
            {
                if (reauthenticated) throw new InvalidDataException(Stage + "：学校再次返回登录页，可能需要验证码或多因素认证。");
                reauthenticated = true;
                var destination = await Authenticate(uri, html, credential, ct);
                uri = destination ?? throw new InvalidDataException(Stage + "：认证未返回服务跳转地址。"); continue;
            }
            if (uri.Host == "courses.zju.edu.cn" && (Execution(html).Length > 0 || uri.AbsolutePath.Contains("/login", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("学在浙大：仍处于登录页面，尚未取得有效会话。");
            var meta = Regex.Match(html, "<meta[^>]+content=[\"'][^\"']*?URL=([^\"'>]+)", RegexOptions.IgnoreCase);
            if (meta.Success) { uri = new Uri(uri, WebUtility.HtmlDecode(meta.Groups[1].Value)); continue; }
            if (libraryCallback) throw new InvalidDataException("图书馆需要额外认证，请在官方网站完成后重试。");
            return html;
        }
        throw new InvalidDataException("学校认证跳转次数过多。");
    }
    static string Execution(string html)
    {
        var input = Regex.Matches(html, "<input\\b[^>]*>", RegexOptions.IgnoreCase).Cast<Match>().FirstOrDefault(m => Regex.IsMatch(m.Value, "name\\s*=\\s*[\"']execution[\"']", RegexOptions.IgnoreCase));
        return input == null ? "" : WebUtility.HtmlDecode(Regex.Match(input.Value, "value\\s*=\\s*[\"']([^\"']+)", RegexOptions.IgnoreCase).Groups[1].Value);
    }
    void CheckStatus(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode) throw new InvalidDataException(Stage + "：学校服务返回 HTTP " + (int)response.StatusCode + "，本地记录已保留。");
    }
    async Task<Uri?> Authenticate(Uri loginUri, string html, SchoolCredential secret, CancellationToken ct)
    {
        if (loginUri.Host != "zjuam.zju.edu.cn" || loginUri.AbsolutePath != "/cas/login" || !Allowed(loginUri)) throw new InvalidDataException("拒绝向非学校认证地址提交凭据。");
        var execution = Execution(html);
        if (execution.Length == 0) throw new InvalidDataException(Stage + "：无法识别认证页面，请检查校园网络或额外认证要求。");
        var key = await Json(Cas + "v2/getPubKey", ct);
        string hexModulus = AssistantParser.S(key, "modulus"), hexExponent = AssistantParser.S(key, "exponent");
        var modulus = BigInteger.Parse("0" + hexModulus, NumberStyles.HexNumber);
        var exponent = BigInteger.Parse("0" + hexExponent, NumberStyles.HexNumber);
        var encrypted = BigInteger.ModPow(new BigInteger(Encoding.UTF8.GetBytes(secret.Password), true, true), exponent, modulus).ToString("x").TrimStart('0').PadLeft(hexModulus.Length, '0');
        using var req = new HttpRequestMessage(HttpMethod.Post, loginUri) { Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["username"] = secret.Username, ["password"] = encrypted, ["execution"] = execution, ["_eventId"] = "submit", ["authcode"] = "", ["rememberMe"] = "false" }) };
        req.Headers.Referrer = loginUri;
        using var res = await Send(req, ct);
        if ((int)res.StatusCode is >= 300 and < 400 && res.Headers.Location != null) return new Uri(loginUri, res.Headers.Location);
        CheckStatus(res);
        var body = await res.Content.ReadAsStringAsync(ct);
        if (Execution(body).Length == 0 && cookies.GetCookies(new Uri(Cas)).Cast<Cookie>().Any(c => c.Name == "iPlanetDirectoryPro" && c.Value.Length > 0)) return new Uri(Cas);
        // A 200 login form is not an authenticated session, even if old cookies remain.
        throw new InvalidDataException(Stage + "：学校未接受认证。请检查共享账号密码；若学校要求验证码或二次认证，需要先在学校页面处理。");
    }
    public async Task Login(SchoolCredential secret, CancellationToken ct)
    {
        Stage = "统一身份认证"; credential = null; coursesReady = false; libraryToken = ""; borrower = "";
        var html = await Follow(new Uri(Cas + "login"), ct);
        // The generic CAS success redirect may point at the university portal rather
        // than a service we use. Do not visit it or validate it as an API endpoint.
        // The cookie jar is reused when visiting the fixed course/library entrypoints;
        // each service still has to complete its own authentication and valid API read.
        await Authenticate(new Uri(Cas + "login"), html, secret, ct);
        credential = secret;
        Account = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret.Username.Trim())))[..24];
    }
    async Task<JsonElement> Json(string url, CancellationToken ct, HttpMethod? method = null, object? payload = null, bool library = false)
    {
        using var req = new HttpRequestMessage(method ?? HttpMethod.Get, url);
        req.Headers.Add("Accept", "application/json");
        if (library && libraryToken.Length > 0) req.Headers.TryAddWithoutValidation("Authorization", libraryToken);
        if (payload != null) req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using var res = await Send(req, ct);
        if ((int)res.StatusCode is >= 300 and < 400 || res.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) throw new CampusSessionExpiredException(Stage + "：登录已失效或需要额外认证，请重新连接。");
        if (res.StatusCode == HttpStatusCode.NotFound) throw new CampusEndpointUnavailableException(Stage + "：学校接口已不可用。");
        CheckStatus(res);
        try { using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct)); return doc.RootElement.Clone(); }
        catch (JsonException) { throw new InvalidDataException("学校返回的内容无法识别，已保留本地数据。"); }
    }
    async Task EnsureCourses(CancellationToken ct)
    {
        if (coursesReady) return;
        Stage = "学在浙大认证";
        var html = await Follow(new Uri(Courses + "/user/index"), ct);
        if (Execution(html).Length > 0) throw new CampusSessionExpiredException("学在浙大仍在等待身份认证。");
        coursesReady = true;
    }
    async Task<JsonElement> CourseJson(string url, CancellationToken ct)
    {
        try { return await Json(url, ct); }
        catch (CampusSessionExpiredException) { coursesReady = false; await EnsureCourses(ct); return await Json(url, ct); }
    }
    async Task<(JsonElement Data, bool Known)> OptionalCourseStatus(string url, string field, CancellationToken ct)
    {
        try { return (await CourseJson(url, ct), true); }
        catch (CampusEndpointUnavailableException) {
            string warning = "部分提交状态接口不可用，请在官网核实完成情况";
            if (!CourseWarnings.Contains(warning)) CourseWarnings.Add(warning);
            using var doc = JsonDocument.Parse("{\"" + field + "\":[]}"); return (doc.RootElement.Clone(), false);
        }
    }
    public async Task<List<AssistantEntry>> FetchCourses(CancellationToken ct)
    {
        await EnsureCourses(ct); Stage = "读取课程待办"; CourseWarnings.Clear();
        var courses = new Dictionary<string, JsonElement>();
        string conditions = Uri.EscapeDataString("{\"role\":[],\"semester_id\":[],\"academic_year_id\":[],\"status\":[\"ongoing\",\"notStarted\"],\"course_type\":[],\"effectiveness\":[],\"published\":[],\"display_studio_list\":false}");
        for (int page = 1; ; page++)
        {
            if (page > 50) throw new InvalidDataException("课程列表过长，未覆盖本地数据。");
            var root = await CourseJson(Courses + $"/api/my-courses?page={page}&page_size=100&sort=all&normal=%7B%22version%22%3A7%2C%22apiVersion%22%3A%221.1.0%22%7D&conditions={conditions}&fields=id,name,course_code", ct);
            var rows = AssistantParser.Array(root, "courses");
            int added = 0;
            foreach (var c in rows) { string id = AssistantParser.Id(c); if (courses.TryAdd(id, c)) added++; }
            if (rows.Count < 100) break;
            if (added == 0) throw new InvalidDataException("课程分页未前进，已保留本地记录。");
        }
        var result = new List<AssistantEntry>();
        foreach (var (id, course) in courses)
        {
            var activities = await CourseJson(Courses + $"/api/courses/{id}/activities", ct);
            var exams = await CourseJson(Courses + $"/api/courses/{id}/exams", ct);
            var submitted = await OptionalCourseStatus(Courses + $"/api/course/{id}/homework/submission-status?no-intercept=true", "homework_activities", ct);
            var examStatus = await OptionalCourseStatus(Courses + $"/api/courses/{id}/submitted-exams?no-intercept=true", "exam_ids", ct);
            var classrooms = await CourseJson(Courses + $"/api/courses/{id}/classroom-list", ct);
            result.AddRange(AssistantParser.Course(id, AssistantParser.S(course, "name"), activities, exams, submitted.Data, examStatus.Data, classrooms, DateTime.Now, submitted.Known, examStatus.Known));
        }
        return result;
    }
    async Task EnsureLibrary(CancellationToken ct)
    {
        Stage = "图书馆认证";
        if (libraryToken.Length > 0) return;
        string code = await Follow(new Uri(Cas + "oauth2.0/authorize?response_type=code&client_id=EcZUPTTg7zcD6FpFPn&redirect_uri=http://m.lib.zju.edu.cn/pages/wechat/auth"), ct, true);
        var root = await Json(Library + "/moblib/binduni", ct, HttpMethod.Post, new { code, openid = "" });
        var data = root.GetProperty("data");
        libraryToken = AssistantParser.S(data, "token"); borrower = AssistantParser.S(data, "tlv_bor_id");
        if (libraryToken.Length == 0 || borrower.Length == 0) { libraryToken = ""; throw new InvalidDataException("图书馆授权失败。"); }
        await Json(Library + "/aleph/bor-auth?CON_LNG=chi", ct, library: true);
    }
    public async Task<List<AssistantEntry>> FetchLibrary(CancellationToken ct)
    {
        await EnsureLibrary(ct); Stage = "读取图书借阅";
        return AssistantParser.Loans(await Json(Library + "/aleph/bor_info?bor_id=" + Uri.EscapeDataString(borrower), ct, library: true), DateTime.Now);
    }
    public async Task Renew(string barcode, CancellationToken ct)
    {
        await EnsureLibrary(ct);
        var r = await Json(Library + "/aleph/renew?CON_LNG=chi&bor-id=" + Uri.EscapeDataString(borrower) + "&library=ZJU50&item_barcode=" + Uri.EscapeDataString(barcode), ct, library: true);
        if (!r.TryGetProperty("data", out var data) || !data.TryGetProperty("renew", out var renew) || AssistantParser.S(renew, "reply") != "ok") throw new InvalidDataException("学校未确认续借成功，请到图书馆网站核对。");
    }
    public async Task<List<JsonElement>> Rollcalls(CancellationToken ct) { await EnsureCourses(ct); return AssistantParser.Array(await CourseJson(Courses + "/api/radar/rollcalls", ct), "rollcalls"); }
    public async Task<bool> Sign(string id, string deviceId, DevicePosition position, CancellationToken ct)
    {
        position.Validate(); ct.ThrowIfCancellationRequested();
        var r = await Json(Courses + "/api/rollcall/" + Uri.EscapeDataString(id) + "/answer?api_version=1.1.2", ct, HttpMethod.Put,
            new { deviceId, latitude = position.Latitude, longitude = position.Longitude, accuracy = position.Accuracy, speed = (double?)null, altitude = (double?)null, altitudeAccuracy = (double?)null, heading = (double?)null });
        return AssistantParser.SignSuccess(r);
    }
    public void Dispose() { credential = null; http.Dispose(); libraryToken = ""; borrower = ""; }
}

public sealed class CampusEndpointUnavailableException(string message) : Exception(message);

public sealed class CampusSessionExpiredException(string message) : Exception(message);

public static class AssistantParser
{
    public static string S(JsonElement e, string key) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind != JsonValueKind.Null ? v.ToString() : "";
    public static bool B(JsonElement e, string key) => S(e, key) is "True" or "true" or "1";
    public static string Id(JsonElement e, string key = "id") { var s = S(e, key); if (!Regex.IsMatch(s, @"^[a-zA-Z0-9_-]{1,100}$")) throw new InvalidDataException("学校条目标识无效。"); return s; }
    public static List<JsonElement> Array(JsonElement e, string key)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(key, out var a) || a.ValueKind != JsonValueKind.Array) throw new InvalidDataException("学校列表格式发生变化，未清空本地记录。");
        return a.EnumerateArray().Select(x => x.Clone()).ToList();
    }
    public static DateTime? Date(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        if (!DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var d) || d.Year < 2000 || d.Year > 2200) throw new InvalidDataException("学校返回的截止时间无效。");
        return d.LocalDateTime;
    }
    public static List<AssistantEntry> Course(string course, string name, JsonElement activities, JsonElement exams, JsonElement submitted, JsonElement examStatus, JsonElement classrooms, DateTime now, bool homeworkKnown = true, bool examKnown = true)
    {
        var done = Array(submitted, "homework_activities").Where(x => S(x, "status_code") == "submitted").Select(x => Id(x)).ToHashSet();
        var examDone = Array(examStatus, "exam_ids").Select(x => x.ToString()).ToHashSet();
        var result = new List<AssistantEntry>();
        void Add(JsonElement e, string type, bool complete, string dueField = "end_time")
        {
            string id = Id(e);
            result.Add(new() { Key = course + ":" + type + ":" + id, Title = S(e, "title"), Course = name, Due = Date(S(e, dueField)), Complete = complete, CompletionKnown = type == "homework" ? homeworkKnown : type == "exam" ? examKnown : true, Url = "https://courses.zju.edu.cn/course/" + course + (type == "homework" ? "/learning-activity#/" + id : "/content#/") });
        }
        foreach (var e in Array(activities, "activities"))
            if (S(e, "type") == "homework" && B(e, "published") && !(Date(S(e, "start_time")) > now)) Add(e, "homework", done.Contains(Id(e)));
        foreach (var e in Array(exams, "exams"))
            if (B(e, "published") && !(Date(S(e, "start_time")) > now)) Add(e, "exam", examDone.Contains(Id(e)));
        foreach (var e in Array(classrooms, "classrooms"))
            if (S(e, "status") == "start" && !(Date(S(e, "start_at")) > now) && !(Date(S(e, "end_at")) < now)) Add(e, "interaction", false, "end_at");
        return result;
    }
    public static List<AssistantEntry> Loans(JsonElement root, DateTime now)
    {
        if (!root.TryGetProperty("data", out var data) || !data.TryGetProperty("bor-info", out var info) || info.ValueKind != JsonValueKind.Object || S(info, "error").Length > 0) throw new InvalidDataException("图书馆未返回有效借阅信息。");
        // An empty object is not evidence of zero loans.
        if (!info.TryGetProperty("item-l", out var loan))
        {
            if (!info.TryGetProperty("z303", out _) && !info.TryGetProperty("z304", out _)) throw new InvalidDataException("图书馆借阅快照不完整，未修改还书状态。");
            return new();
        }
        var rows = loan.ValueKind == JsonValueKind.Null ? new List<JsonElement>() : loan.ValueKind == JsonValueKind.Array ? loan.EnumerateArray().ToList() : loan.ValueKind == JsonValueKind.Object ? new() { loan } : throw new InvalidDataException("借阅列表格式异常。");
        var result = new List<AssistantEntry>();
        foreach (var row in rows)
        {
            var book = row.GetProperty("z13"); var copy = row.GetProperty("z30"); var state = row.GetProperty("z36");
            var barcode = S(copy, "z30-barcode");
            if (barcode.Length == 0 || !DateTime.TryParseExact(S(state, "z36-due-date"), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var due)) throw new InvalidDataException("借阅条目缺少条码或到期日。");
            var status = S(copy, "z30-item-status");
            result.Add(new() { Key = barcode, Barcode = barcode, Title = S(book, "z13-title"), Course = S(book, "z13-author"), Due = due.AddHours(23).AddMinutes(59), Url = "https://libweb.zju.edu.cn/", Renewable = due.Date >= now.Date && (S(state, "z36-letter-number") is "" or "0" or "00") && (status == "12" || status == "11" && S(state, "z36-no-renewal") == "0") });
        }
        return result;
    }
    public static bool SignSuccess(JsonElement e) => S(e, "status") is "on_call" or "on_call_fine" || S(e, "status_name") is "on_call" or "on_call_fine";
}
