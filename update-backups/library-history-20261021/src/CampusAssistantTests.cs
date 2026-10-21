using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Shike;

public static class CampusAssistantTests
{
    public static void Run(Action<string, Action> test)
    {
        static void Assert(bool value) { if (!value) throw new Exception("Campus assistant assertion failed"); }
        static JsonElement J(string s) { using var d = JsonDocument.Parse(s); return d.RootElement.Clone(); }
        static void Throws(Action action) { bool thrown = false; try { action(); } catch { thrown = true; } Assert(thrown); }
        DateTime now = new(2026, 9, 22, 12, 0, 0);
        AssistantEntry Entry(string key = "1", bool done = false) => new() { Key = key, Title = "微积分第一周作业", Course = "微积分", Url = "https://courses.zju.edu.cn/course/1/content#/", Due = now.AddDays(2), Complete = done };
        test("Assistant import deduplicates across repeated snapshots and preserves notes and reminders", () => {
            var d = new AppData(); var e = Entry(); AssistantMerge.Apply(d, "courses", "account", new() { e }, now);
            var item = d.Items.Single(); item.Note = "个人解题思路"; item.Reminders = new() { 30 }; item.Attachments.Add(new() { File = "proof.png", Name = "证明" });
            e.Due = now.AddDays(3); AssistantMerge.Apply(d, "courses", "account", new() { e }, now.AddMinutes(1));
            Assert(d.Items.Count == 1 && item.Start == e.Due && item.Note == "个人解题思路" && item.Attachments.Count == 1 && item.Reminders.SequenceEqual(new[] { 30 }));
        });
        test("Remote submission and reopening update completion without overriding manual completion", () => {
            var d = new AppData(); var e = Entry(); AssistantMerge.Apply(d, "courses", "a", new() { e }, now); var i = d.Items.Single(); i.Done = true;
            AssistantMerge.Apply(d, "courses", "a", new() { e }, now); Assert(i.Done);
            e.Complete = true; AssistantMerge.Apply(d, "courses", "a", new() { e }, now); e.Complete = false; AssistantMerge.Apply(d, "courses", "a", new() { e }, now); Assert(!i.Done);
        });
        test("Deleted imported tasks remain hidden and never duplicate", () => {
            var d = new AppData(); AssistantMerge.Apply(d, "courses", "a", new() { Entry() }, now); d.Items[0].Cancelled = true;
            AssistantMerge.Apply(d, "courses", "a", new() { Entry() }, now); Assert(d.Items.Count == 1 && d.Items[0].Cancelled);
        });
        test("Missing due date suppresses calendar and restores original reminders when date returns", () => {
            var d = new AppData(); var e = Entry(); AssistantMerge.Apply(d, "courses", "a", new() { e }, now); e.Due = null;
            AssistantMerge.Apply(d, "courses", "a", new() { e }, now); Assert(!Engine.Between(d.Items, now, now.AddDays(7)).Any());
            e.Due = now.AddDays(3); AssistantMerge.Apply(d, "courses", "a", new() { e }, now); Assert(Engine.Between(d.Items, now, now.AddDays(7)).Count() == 1 && d.Items[0].Reminders!.Count == 2);
        });
        test("No-date task stays in assistant without fabricated calendar date", () => { var d = new AppData(); var e = Entry(); e.Due = null; AssistantMerge.Apply(d, "courses", "a", new() { e }, now); Assert(d.Items.Count == 0 && d.Assistant.Courses.Entries.Count == 1); });
        test("Returned books finish only matching account; missing coursework remains open", () => {
            var d = new AppData(); AssistantMerge.Apply(d, "library", "a", new() { Entry() }, now); AssistantMerge.Apply(d, "library", "b", new() { Entry() }, now); AssistantMerge.Apply(d, "courses", "a", new() { Entry() }, now);
            AssistantMerge.Apply(d, "library", "a", new(), now); AssistantMerge.Apply(d, "courses", "a", new(), now); Assert(d.Items.Count(i => i.Done) == 1 && d.Items[0].Done);
        });
        test("Invalid and duplicate snapshots leave cached items and freshness unchanged", () => {
            var d = new AppData(); AssistantMerge.Apply(d, "courses", "a", new() { Entry() }, now); string before = JsonSerializer.Serialize(d);
            Throws(() => AssistantMerge.Apply(d, "courses", "a", new() { Entry(), Entry() }, now.AddMinutes(1))); Assert(JsonSerializer.Serialize(d) == before);
            var bad = Entry("2"); bad.Url = "https://evil.example/"; Throws(() => AssistantMerge.Apply(d, "courses", "a", new() { bad }, now)); Assert(JsonSerializer.Serialize(d) == before);
        });
        test("Course parser excludes ordinary resources and retains overdue work and verified submissions", () => {
            var entries = AssistantParser.Course("1", "微积分", J("""{"activities":[{"id":1,"title":"过期作业","type":"homework","published":true,"end_time":"2026-09-01T12:00:00+08:00"},{"id":2,"title":"已交","type":"homework","published":true,"end_time":"2026-10-01T12:00:00+08:00"},{"id":3,"title":"教材","type":"material","published":true}]}"""), J("""{"exams":[]}"""), J("""{"homework_activities":[{"id":2,"status_code":"submitted"}]}"""), J("""{"exam_ids":[]}"""), J("""{"classrooms":[]}"""), now);
            Assert(entries.Count == 2 && entries.Single(e => e.Key.EndsWith(":2")).Complete && entries[0].Due < now);
        });
        test("Malformed course status is not treated as empty success", () => Throws(() => AssistantParser.Course("1", "", J("""{"activities":[]}"""), J("""{"exams":[]}"""), J("{}"), J("""{"exam_ids":[]}"""), J("""{"classrooms":[]}"""), now)));
        test("Library accepts single loan and parses date-only deadline and renewal eligibility", () => {
            var entries = AssistantParser.Loans(J("""{"data":{"bor-info":{"item-l":{"z13":{"z13-title":"线性代数"},"z30":{"z30-barcode":"123","z30-item-status":"11"},"z36":{"z36-due-date":"20261001","z36-no-renewal":"0"}}}}} """), now);
            Assert(entries.Count == 1 && entries[0].Due == new DateTime(2026, 10, 1, 23, 59, 0) && entries[0].Renewable);
        });
        test("Library malformed and error responses cannot mark loans returned", () => { Throws(() => AssistantParser.Loans(J("""{"data":{"bor-info":{}}}"""), now)); Throws(() => AssistantParser.Loans(J("""{"data":{"bor-info":{"error":"denied","item-l":[]}}}"""), now)); Assert(AssistantParser.Loans(J("""{"data":{"bor-info":{"item-l":[]}}}"""), now).Count == 0); });
        test("Only explicit server attendance status counts as success", () => { Assert(AssistantParser.SignSuccess(J("""{"status":"on_call"}"""))); Assert(AssistantParser.SignSuccess(J("""{"status_name":"on_call_fine"}"""))); Assert(!AssistantParser.SignSuccess(J("""{"success":true}"""))); Assert(!AssistantParser.SignSuccess(J("""{"status":"absent"}"""))); });
        test("Attendance rejects stale and inaccurate location", () => { Throws(() => new DevicePosition(30, 120, 5, DateTime.UtcNow.AddMinutes(-5)).Validate()); Throws(() => new DevicePosition(30, 120, 500, DateTime.UtcNow).Validate()); Throws(() => new DevicePosition(double.NaN, 120, 5, DateTime.UtcNow).Validate()); new DevicePosition(30, 120, 20, DateTime.UtcNow).Validate(); });
        test("Redirect allowlist refuses credentials on unknown hosts and unencrypted URLs", () => { Assert(CampusClient.Allowed(new Uri("https://courses.zju.edu.cn/api/todos"))); Assert(!CampusClient.Allowed(new Uri("http://courses.zju.edu.cn/"))); Assert(!CampusClient.Allowed(new Uri("https://courses.zju.edu.cn.evil.example/"))); Assert(!CampusClient.Allowed(new Uri("https://courses.zju.edu.cn:8443/"))); });
        test("Attendance cancellation prevents any network submission", () => {
            var handler = new FixtureHandler(_ => "{}"); using var client = new CampusClient(handler); var cts = new CancellationTokenSource(); cts.Cancel();
            Throws(() => client.Sign("1", "device", new DevicePosition(30, 120, 20, DateTime.UtcNow), cts.Token).GetAwaiter().GetResult()); Assert(handler.Count == 0);
        });
        test("Attendance sends exact coordinates once and requires status confirmation", () => {
            var handler = new FixtureHandler(req => { Assert(req.Method == HttpMethod.Put && req.RequestUri!.Host == "courses.zju.edu.cn"); var body = J(req.Content!.ReadAsStringAsync().GetAwaiter().GetResult()); Assert(body.GetProperty("latitude").GetDouble() == 30.25 && body.GetProperty("accuracy").GetDouble() == 15); return "{\"status\":\"on_call\"}"; });
            using var client = new CampusClient(handler); Assert(client.Sign("123", "device", new DevicePosition(30.25, 120.5, 15, DateTime.UtcNow), CancellationToken.None).GetAwaiter().GetResult() && handler.Count == 1);
        });
        test("Course request failure aborts complete snapshot", () => {
            var handler = new FixtureHandler(req => {
                string path = req.RequestUri!.AbsolutePath;
                if (path == "/user/index") return "ok";
                if (path == "/api/my-courses") return "{\"courses\":[{\"id\":1,\"name\":\"课程\"}]}";
                if (path.EndsWith("activities")) return "{\"activities\":[]}";
                throw new HttpRequestException("fixture failure");
            }); using var client = new CampusClient(handler); Throws(() => client.FetchCourses(CancellationToken.None).GetAwaiter().GetResult());
        });
        test("Assistant logs are bounded and old backups retain valid default assistant state", () => { var d = JsonSerializer.Deserialize<AppData>("{\"Version\":1}")!; for (int n = 0; n < 110; n++) AssistantMerge.Log(d, "event"); Assert(d.Assistant.SignLog.Count == 100); AssistantMerge.Validate(d); });
        test("CAS service reauthentication reuses identical credential and preserves service query", () => {
            using var handler = new AuthHandler(); using var client = new CampusClient(handler);
            client.Login(new("fixture-student", "fixture-password"), CancellationToken.None).GetAwaiter().GetResult();
            Assert(client.FetchCourses(CancellationToken.None).GetAwaiter().GetResult().Count == 0);
            Assert(handler.Posts.Count == 2 && handler.Posts[0] == handler.Posts[1] && handler.ServicePosts == 1);
        });
        test("Expired read session reauthenticates once and repeats only the read", () => {
            using var handler = new AuthHandler { ExpireRead = true }; using var client = new CampusClient(handler);
            client.Login(new("fixture-student", "fixture-password"), CancellationToken.None).GetAwaiter().GetResult();
            Assert(client.FetchCourses(CancellationToken.None).GetAwaiter().GetResult().Count == 0 && handler.ServicePosts == 2 && handler.Reads == 2);
        });
        test("Generic CAS portal redirect is never fetched and does not block service authentication", () => {
            foreach (string landing in new[] { "http://i.zju.edu.cn/", "https://portal.example.invalid/landing?ticket=fixture-secret" }) {
                using var handler = new AuthHandler { Landing = landing }; using var client = new CampusClient(handler);
                client.Login(new("fixture-student", "fixture-password"), CancellationToken.None).GetAwaiter().GetResult();
                Assert(client.FetchCourses(CancellationToken.None).GetAwaiter().GetResult().Count == 0 && handler.ServicePosts == 1);
            }
        });
        test("Legacy service redirect upgrades HTTPS without broadening host allowlist", () => {
            var secure = CampusClient.SecureServiceUri(new Uri("http://courses.zju.edu.cn/callback?ticket=fixture"));
            Assert(secure.Scheme == "https" && secure.Query == "?ticket=fixture" && CampusClient.Allowed(secure));
            Assert(!CampusClient.Allowed(CampusClient.SecureServiceUri(new Uri("http://evil.example/callback"))));
            Assert(!CampusClient.Allowed(CampusClient.SecureServiceUri(new Uri("http://courses.zju.edu.cn:8080/callback"))));
        });
        test("Current identity broker chain preserves service state and returns to course session", () => {
            using var handler = new AuthHandler { IdentityBridge = true }; using var client = new CampusClient(handler);
            client.Login(new("fixture-student", "fixture-password"), CancellationToken.None).GetAwaiter().GetResult();
            Assert(client.FetchCourses(CancellationToken.None).GetAwaiter().GetResult().Count == 0 && handler.IdentityHops == 3 && handler.ServicePosts == 1);
            Assert(CampusClient.Allowed(new Uri("https://identity.zju.edu.cn/auth/realms/zju/protocol/cas/login")));
            Assert(!CampusClient.Allowed(new Uri("https://identity.zju.edu.cn.evil.example/auth")));
        });
        test("Unavailable submission status preserves previous completion and still updates deadline", () => {
            var d = new AppData(); var e = Entry(done:true); AssistantMerge.Apply(d,"courses","a",new(){e},now);
            e.Complete=false; e.CompletionKnown=false; e.Due=now.AddDays(5); AssistantMerge.Apply(d,"courses","a",new(){e},now);
            Assert(d.Items.Single().Done && d.Items.Single().AssistantRemoteDone && d.Items.Single().Start==e.Due);
            e.Due=null; AssistantMerge.Apply(d,"courses","a",new(){e},now); Assert(d.Items.Single().Done);
        });
        test("HTTP 404 on optional status does not prevent authoritative assignment import", () => {
            using var handler = new AuthHandler { MissingSubmission = true }; using var client = new CampusClient(handler);
            client.Login(new("fixture-student","fixture-password"),CancellationToken.None).GetAwaiter().GetResult();
            var rows=client.FetchCourses(CancellationToken.None).GetAwaiter().GetResult();
            Assert(rows.Count==1 && !rows[0].CompletionKnown && client.CourseWarnings.Count==1);
            var d=new AppData();AssistantMerge.Apply(d,"courses","fixture",rows,now);Assert(d.Items.Count==1 && !d.Items[0].Done);
        });
        test("Rejected CAS form never becomes a successful connection", () => {
            using var handler = new AuthHandler { Reject = true }; using var client = new CampusClient(handler);
            Throws(() => client.Login(new("fixture-student", "fixture-password"), CancellationToken.None).GetAwaiter().GetResult()); Assert(client.Account == "");
        });
        test("Authentication diagnostics identify host and stage without exposing secrets", () => {
            using var client = new CampusClient(new FixtureHandler(_ => throw new HttpRequestException("secret-body")));
            try { client.Login(new("fixture-student", "fixture-password"), CancellationToken.None).GetAwaiter().GetResult(); throw new Exception("Expected connection failure"); }
            catch (InvalidDataException ex) { Assert(ex.Message.Contains("统一身份认证") && ex.Message.Contains("zjuam.zju.edu.cn") && !ex.Message.Contains("secret-body") && !ex.Message.Contains("fixture-password")); }
        });
    }
    sealed class AuthHandler : HttpMessageHandler
    {
        public bool Reject, ExpireRead, IdentityBridge, MissingSubmission;
        public int IdentityHops;
        public string Landing = "https://zjuam.zju.edu.cn/cas/index";
        public int Reads, ServicePosts;
        public List<string> Posts = new();
        const string Form = "<input value='execution-fixture' name='execution'>";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (!request.Headers.UserAgent.Any()) throw new Exception("Missing browser User-Agent");
            HttpResponseMessage Reply(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };
            HttpResponseMessage Redirect(string url) { var r = new HttpResponseMessage(HttpStatusCode.Found); r.Headers.Location = new Uri(url); return r; }
            var u = request.RequestUri!;
            if (u.AbsolutePath == "/cas/v2/getPubKey") return Reply("{\"modulus\":\"" + new string('f', 256) + "\",\"exponent\":\"010001\"}");
            if (u.AbsolutePath == "/cas/login")
            {
                if (request.Method == HttpMethod.Get) return Reply(Form);
                var form = await request.Content!.ReadAsStringAsync(cancellationToken);
                if (!form.Contains("username=fixture-student") || !form.Contains("authcode=") || form.Contains("fixture-password")) throw new Exception("Credential form incorrect");
                var cipher = form.Split('&').Single(x => x.StartsWith("password="))[9..];
                if (cipher.Length != 256) throw new Exception("RSA width does not match returned key");
                Posts.Add(form);
                if (Reject) return Reply(Form);
                if (u.Query.Length > 0) {
                    ServicePosts++;
                    if (IdentityBridge) {
                        if (!u.Query.Contains("state%3Dfixture-state")) throw new Exception("Identity state lost");
                        return Redirect("https://identity.zju.edu.cn/auth/realms/zju/broker/cas-client/endpoint?state=fixture-state&ticket=fixture-ticket");
                    }
                    return Redirect("https://courses.zju.edu.cn/callback?ticket=fixture-ticket");
                }
                return Redirect(Landing);
            }
            if (u.Host == "identity.zju.edu.cn") {
                IdentityHops++;
                if (u.AbsolutePath.EndsWith("/protocol/cas/login")) return Redirect("https://identity.zju.edu.cn/auth/realms/zju/broker/cas-client/login");
                if (u.AbsolutePath.EndsWith("/cas-client/login")) return Redirect("https://zjuam.zju.edu.cn/cas/login?service=https%3A%2F%2Fidentity.zju.edu.cn%2Fauth%2Frealms%2Fzju%2Fbroker%2Fcas-client%2Fendpoint%3Fstate%3Dfixture-state");
                if (u.AbsolutePath.EndsWith("/endpoint")) return Redirect("https://courses.zju.edu.cn/callback?ticket=course-ticket");
                throw new Exception("Unexpected broker path");
            }
            if (u.AbsolutePath == "/user/index" && IdentityBridge) return Redirect("https://identity.zju.edu.cn/auth/realms/zju/protocol/cas/login?service=https%3A%2F%2Fcourses.zju.edu.cn%2Fuser%2Findex");
            if (u.AbsolutePath == "/user/index") return Redirect("https://zjuam.zju.edu.cn/cas/login?service=https%3A%2F%2Fcourses.zju.edu.cn%2Fcallback");
            if (u.AbsolutePath == "/callback") return Reply("<html>Authenticated dashboard</html>");
            if (u.AbsolutePath == "/api/my-courses") { Reads++; if (ExpireRead && Reads == 1) return new(HttpStatusCode.Unauthorized); return Reply(MissingSubmission ? "{\"courses\":[{\"id\":1,\"name\":\"Course\"}]}" : "{\"courses\":[]}"); }
            if(MissingSubmission) {
                if(u.AbsolutePath.EndsWith("/activities")) return Reply("{\"activities\":[{\"id\":5,\"title\":\"Homework\",\"type\":\"homework\",\"published\":true,\"end_time\":\"2026-10-01T12:00:00+08:00\"}]}");
                if(u.AbsolutePath.EndsWith("/submission-status")) return new(HttpStatusCode.NotFound);
                if(u.AbsolutePath.EndsWith("/exams")) return Reply("{\"exams\":[]}");
                if(u.AbsolutePath.EndsWith("/submitted-exams")) return Reply("{\"exam_ids\":[]}");
                if(u.AbsolutePath.EndsWith("/classroom-list")) return Reply("{\"classrooms\":[]}");
            }
            throw new Exception("Unexpected fixture endpoint");
        }
    }
    sealed class FixtureHandler(Func<HttpRequestMessage, string> reply) : HttpMessageHandler
    {
        public int Count;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); Count++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(reply(request)) }); }
    }
}
