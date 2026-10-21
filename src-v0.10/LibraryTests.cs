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
internal static class LibraryTests
{
    internal const string Summary = """
        <!-- filename: bor-info-single-adm -->
        <!-- <meta http-equiv="refresh" content="0; URL=https://opac.zju.edu.cn/F/session?func=logout"> -->
        <meta http-equiv="refresh" content="1200; URL=https://opac.zju.edu.cn/F/session?func=logout">
        <a href="javascript:replacePage('https://opac.zju.edu.cn:443/F/session?func=bor-loan&amp;adm_library=ZJU50');">0&nbsp;</a>
        """;
    internal static string Current(string title = "线性代数", string due = "2026-11-21", string identity = "ZJU50000000001000001") => """
        <!-- filename: bor-loan-head --><table><tr><th>序号</th><th></th><th>题名</th><th>著者</th><th>出版年</th><th>应还日期</th><!--<th>应还时间</th>--><th>罚款</th><th>分馆</th><th>索书号</th></tr>
        """ + $"<tr><td>1</td><td><input type='checkbox' name='{identity}'></td><td>{title}</td><td>作者 A</td><td>2020</td><td>{due}</td><td>0</td><td>主馆</td><td>O151/1</td></tr></table>";
    internal static string History(int total = 2) => """
        <!--filename: bor-history-loan-head-->
        """ + $"<p>列出了<span>总共 {total} 条中的 2 条</span>外借信息。</p>" + """
        <table><tr><th>No.</th><th>著者</th><th>题名</th><th>年</th><th>应还日期</th><th>应还时间</th><th>归还日期</th><th>归还时间</th><th>罚款</th><th>分馆</th></tr>
        <tr><td>1</td><td>作者 B</td><td>同一本书</td><td>2020</td><td>21/10/2026</td><td></td><td>20/10/2026</td><td></td><td>0</td><td>主馆</td></tr>
        <tr><td>2</td><td>作者 B</td><td>同一本书</td><td>2020</td><td>2026-09-01</td><td></td><td>2026-08-30</td><td></td><td>0</td><td>主馆</td></tr></table>
        """;
    internal static string Empty(bool history) => "<!-- filename: " + (history ? "bor-history-loan" : "bor-loan") + "-no-loan --> <p>没有任何单册。</p>";
    public static void Run(Action<string, Action> test)
    {
        static void Assert(bool value) { if (!value) throw new Exception("Library assertion failed"); }
        static void Throws(Action action) { try { action(); } catch (InvalidDataException) { return; } catch (CampusSessionExpiredException) { return; } throw new Exception("Expected library rejection"); }
        test("OPAC selects explicit management library and numeric count from replacePage anchor", () => {
            var targets = OpacParser.Targets(Summary); Assert(targets.Count == 1 && targets[0].Count == 0 && targets[0].Library == "ZJU50" && targets[0].Url.Scheme == "https");
            Throws(() => OpacParser.Targets(Summary.Replace("adm_library=ZJU50", "adm_library=")));
            Throws(() => OpacParser.Targets(Summary.Replace("opac.zju.edu.cn", "opac.zju.edu.cn.evil.example")));
            Throws(() => OpacParser.Targets("<!-- filename: login-session -->"));
        });
        test("OPAC parses real current-loan column layout with commented columns and stable identity", () => {
            var a = OpacParser.Read(Current("A &amp; B"), "ZJU50", false).Entries.Single();
            var b = OpacParser.Read(Current("A &amp; B", "2026-12-21"), "ZJU50", false).Entries.Single();
            Assert(a.Title == "A & B" && a.Branch == "主馆" && a.CallNumber == "O151/1" && a.Due == new DateTime(2026, 11, 21, 23, 59, 0) && a.Key == b.Key && !a.Renewable);
        });
        test("OPAC parses repeat historical loans independently and preserves actual return dates", () => {
            var page = OpacParser.Read(History(), "ZJU50", true);
            Assert(page.Entries.Count == 2 && page.Entries.Select(e => e.Key).Distinct().Count() == 2 && page.Total == 2 && page.Entries[0].ReturnedAt == new DateTime(2026,10,20));
        });
        test("OPAC explicitly distinguishes empty current and history pages from invalid HTML", () => {
            Assert(OpacParser.Read(Empty(false), "ZJU50", false).Entries.Count == 0 && OpacParser.Read(Empty(true), "ZJU50", true).Entries.Count == 0);
            Throws(() => OpacParser.Read(Empty(false), "ZJU50", true));
            Throws(() => OpacParser.Read("<html>Bad Gateway</html>", "ZJU50", false));
            Throws(() => OpacParser.Read("<!-- filename: bor-loan-head --><table></table>", "ZJU50", false));
            Throws(() => OpacParser.Read(Empty(false) + "<!-- filename: bor-loan-body -->", "ZJU50", false));
        });
        test("OPAC rejects missing or malformed due dates, shifted columns and duplicate current records", () => {
            Throws(() => OpacParser.Read(Current(due:""), "ZJU50", false));
            Throws(() => OpacParser.Read(Current(due:"not-a-date"), "ZJU50", false));
            Throws(() => OpacParser.Read(Current().Replace("<td>主馆</td>", ""), "ZJU50", false));
            Throws(() => OpacParser.Read(Current() + Current(), "ZJU50", false));
        });
        test("OPAC reports website history limit and rejects count mismatches", () => {
            var page = OpacParser.Read(History(50), "ZJU50", true); Assert(page.Total == 50 && page.Entries.Count == 2);
            Throws(() => OpacParser.Read(History().Replace("中的 2 条", "中的 3 条"), "ZJU50", true));
        });
        test("History merge never creates reminders or marks current books returned", () => {
            var data = new AppData(); var now = DateTime.Now;
            AssistantMerge.Apply(data, "library", "a", OpacParser.Read(Current(), "ZJU50", false).Entries, now);
            string before = JsonSerializer.Serialize(data.Items);
            AssistantMerge.Apply(data, "library-history", "a", OpacParser.Read(History(), "ZJU50", true).Entries, now);
            AssistantMerge.Apply(data, "library-history", "a", new(), now);
            Assert(JsonSerializer.Serialize(data.Items) == before && data.Assistant.Library.Entries.Count == 1 && data.Assistant.LibraryHistory.Updated == now);
            AssistantMerge.Validate(data);
        });
        test("Old saved assistant state gains independent history cache without migration loss", () => {
            var data = JsonSerializer.Deserialize<AppData>("{\"Version\":1,\"Assistant\":{\"Library\":{\"Status\":\"old\"}}}")!;
            AssistantMerge.Validate(data); Assert(data.Assistant.Library.Status == "old" && data.Assistant.LibraryHistory.Updated == null);
        });
        test("OPAC full empty read visits official library and both lists, never mobile API or logout", () => {
            using var handler = new OpacHandler(); using var client = new CampusClient(handler);
            Assert(client.FetchLibrary(CancellationToken.None).GetAwaiter().GetResult().Count == 0);
            Assert(client.FetchLibraryHistory(CancellationToken.None).GetAwaiter().GetResult().Count == 0 && client.LibraryHistoryTotal == 0);
            Assert(handler.Count == 3);
        });
        test("OPAC incomplete current snapshot fails without preventing independent history read", () => {
            using var handler = new OpacHandler { ExpectedCount = 1 }; using var client = new CampusClient(handler);
            Throws(() => client.FetchLibrary(CancellationToken.None).GetAwaiter().GetResult());
            Assert(client.FetchLibraryHistory(CancellationToken.None).GetAwaiter().GetResult().Count == 0);
        });
        test("OPAC cancellation prevents all network requests", () => {
            using var handler = new OpacHandler(); using var client = new CampusClient(handler); using var cts = new CancellationTokenSource(); cts.Cancel();
            try { client.FetchLibrary(cts.Token).GetAwaiter().GetResult(); throw new Exception("Expected cancellation"); } catch (OperationCanceledException) { Assert(handler.Count == 0); }
        });
    }
    sealed class OpacHandler : HttpMessageHandler
    {
        public int Count, ExpectedCount;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); Count++;
            var uri = request.RequestUri!;
            if (uri.Host != "opac.zju.edu.cn" || request.Method != HttpMethod.Get || uri.Query.Contains("logout")) throw new Exception("Unexpected library request");
            string body = uri.AbsolutePath == "/zjusso/" ? Summary.Replace(">0&nbsp;", ">" + ExpectedCount + "&nbsp;")
                : uri.Query == "?func=bor-loan&adm_library=ZJU50" ? Empty(false)
                : uri.Query == "?func=bor-history-loan&adm_library=ZJU50" ? Empty(true) : throw new Exception("Wrong library/function");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }
}
