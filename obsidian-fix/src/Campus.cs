using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using static Shike.Theme;

namespace Shike;

public sealed class CampusActivity
{
    public string Url { get; set; } = "";
    public string Source { get; set; } = "";
    public string Category { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string Location { get; set; } = "";
    public DateTime? Start { get; set; }
    public DateTime? Published { get; set; }
    public DateTime Fetched { get; set; }
}
public sealed class CampusFeedState
{
    public string Name { get; set; } = "";
    public DateTime? Updated { get; set; }
    public string Status { get; set; } = "尚未更新";
}
public sealed record CampusSource(string Name, string Url, string Category, bool Lecture);

public static class CampusFeeds
{
    public static readonly CampusSource[] Sources = {
        new("数据科学研究中心", "https://cds.zju.edu.cn/xsjl.html", "讲座", true),
        new("公共体育与艺术部 · 社团", "https://tyys.zju.edu.cn/redir.php?catalog_id=172452&page=1", "社团", false),
        new("公共体育与艺术部 · 体育", "https://tyys.zju.edu.cn/redir.php?catalog_id=172451&page=1", "体育", false)
    };
    const RegexOptions Rx = RegexOptions.Singleline | RegexOptions.IgnoreCase;
    static Match Match(string input, string pattern) => Regex.Match(input, pattern, Rx, TimeSpan.FromSeconds(2));
    static string Capture(string input, string pattern) => Match(input, pattern).Groups[1].Value;
    public static string Plain(string text) => WebUtility.HtmlDecode(Regex.Replace(Regex.Replace(text, "<[^>]+>", " ", Rx, TimeSpan.FromSeconds(2)), @"\s+", " ")).Trim();
    static string ClassText(string html, string css) => Plain(Capture(html, "<p\\b[^>]*class=[\"']" + css + "[\"'][^>]*>(.*?)</p>"));
    public static bool SafeUrl(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == "https" && (u.Host == "zju.edu.cn" || u.Host.EndsWith(".zju.edu.cn", StringComparison.OrdinalIgnoreCase)) && u.IsDefaultPort && u.UserInfo.Length == 0;
    public static List<CampusActivity> Parse(CampusSource source, string html, DateTime fetched)
    {
        var result = new List<CampusActivity>();
        foreach (Match m in Regex.Matches(html, "<li\\b[^>]*class=[\"'][^\"']*" + (source.Lecture ? "culture-item" : "list-item") + "[^\"']*[\"'][^>]*>(.*?)</li>", Rx, TimeSpan.FromSeconds(3)))
        {
            var block = m.Groups[1].Value;
            var href = WebUtility.HtmlDecode(Capture(block, "<a\\b[^>]*href=[\"']([^\"']+)[\"']"));
            if (!Uri.TryCreate(new Uri(source.Url), href, out var uri) || !SafeUrl(uri.AbsoluteUri)) continue;
            string title = source.Lecture ? Plain(Capture(block, "<h3\\b[^>]*>(.*?)</h3>")) : ClassText(block, "title");
            if (title.Length == 0 || title.Length > 300) continue;
            var item = new CampusActivity { Url = uri.AbsoluteUri, Source = source.Name, Category = source.Category, Title = title, Fetched = fetched };
            if (source.Lecture)
            {
                var time = ClassText(block, "time");
                var date = Match(time, @"(\d{4})年\s*(\d{1,2})月\s*(\d{1,2})日\s*(\d{1,2})[:：](\d{2})");
                if (!date.Success) continue; // A pinned chat-group notice is not a timed lecture.
                if (!DateTime.TryParseExact(string.Join("-", date.Groups[1].Value, date.Groups[2].Value, date.Groups[3].Value) + " " + date.Groups[4].Value + ":" + date.Groups[5].Value, "yyyy-M-d H:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start)) continue;
                item.Start = start; item.Location = ClassText(block, "address");
                item.Description = "主讲人：" + ClassText(block, "person") + "\n官网未提供结束时间；加入日程时请确认时长。";
            }
            else
            {
                if (!DateTime.TryParseExact(ClassText(block, "y") + "-" + ClassText(block, "d"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var published)) continue;
                item.Published = published;
                item.Description = ClassText(block, "dec");
                if (item.Description.Length == 0) item.Description = "已保存官网通知摘要。活动时间、地点和报名要求请查看原文；原文可能跳转至公众号。";
            }
            result.Add(item);
        }
        if (result.Count == 0) throw new InvalidDataException("未识别到活动列表，保留已有缓存，请查看官网。");
        return result.DistinctBy(a => a.Url).ToList();
    }
    public static async Task<List<CampusActivity>> Fetch(CampusSource source)
    {
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20), MaxResponseContentBufferSize = 2 * 1024 * 1024 };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Shike/0.3");
        using var response = await client.GetAsync(source.Url);
        response.EnsureSuccessStatusCode();
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var html = (source.Lecture ? Encoding.UTF8 : Encoding.GetEncoding("gb18030")).GetString(bytes);
        return Parse(source, html, DateTime.Now);
    }
    public static void Merge(AppData data, IEnumerable<CampusActivity> incoming)
    {
        foreach (var item in incoming)
        {
            data.CampusActivities.RemoveAll(a => a.Url == item.Url);
            data.CampusActivities.Add(item);
        }
        data.CampusActivities = data.CampusActivities.OrderByDescending(a => a.Start ?? a.Published ?? a.Fetched).Take(1000).ToList();
    }
}

public sealed partial class MainWindow
{
    bool campusBusy; string campusFilter = "近期活动"; string campusCategory = "全部";
    async Task RefreshCampus()
    {
        if (campusBusy || preview) return;
        campusBusy = true; if (view == "校园活动") Render();
        int generation = syncGeneration;
        try
        {
            foreach (var source in CampusFeeds.Sources)
            {
                List<CampusActivity>? incoming = null; string status;
                try { incoming = await CampusFeeds.Fetch(source); status = $"已读取 {incoming.Count} 条"; }
                catch (Exception ex) { status = "更新失败，保留缓存 · " + (ex is HttpRequestException ? "网络或证书不可用" : ex is TaskCanceledException ? "连接超时" : ex.Message); }
                if (generation != syncGeneration || exiting) return;
                var before = System.Text.Json.JsonSerializer.Serialize(store.Data);
                try
                {
                    var state = store.Data.CampusSources.FirstOrDefault(s => s.Name == source.Name);
                    if (state == null) { state = new() { Name = source.Name }; store.Data.CampusSources.Add(state); }
                    if (incoming != null) { CampusFeeds.Merge(store.Data, incoming); state.Updated = DateTime.Now; }
                    state.Status = status; store.Save();
                }
                catch (Exception ex) { store.Data = System.Text.Json.JsonSerializer.Deserialize<AppData>(before)!; if (!exiting) MessageBox.Show(this, "活动信息未保存：" + ex.Message); return; }
                if (view == "校园活动") Render();
            }
        }
        finally { campusBusy = false; if (!exiting && view == "校园活动") Render(); }
    }
    void RenderCampus()
    {
        var p = Page();
        var refresh = Button(campusBusy ? "正在更新…" : "更新活动", async () => await RefreshCampus(), true); refresh.IsEnabled = !campusBusy;
        var range = Combo(new[] { "近期活动", "全部本地记录" }, campusFilter);
        var category = Combo(new[] { "全部", "讲座", "社团", "体育" }, campusCategory);
        range.Margin = category.Margin = new Thickness(0, 0, 12, 0);
        range.SelectionChanged += (_, _) => { campusFilter = range.SelectedItem!.ToString()!; Render(); };
        category.SelectionChanged += (_, _) => { campusCategory = category.SelectedItem!.ToString()!; Render(); };
        p.Children.Add(Card(Stack(Text("在课表之外，发现校园", 21, bold: true), Space(8), Text("近期：未来 60 天的讲座，以及近 30 天的社团、体育通知。通知发布日期不代表活动时间。", 12, Muted), Space(16), Row(range, category, refresh)), "#EDF2E9"));
        var auto = new CheckBox { Content = "每次启动时更新校园活动", IsChecked = store.Data.Settings.AutoCampusRefresh };
        auto.Click += (_, _) => Commit(() => store.Data.Settings.AutoCampusRefresh = auto.IsChecked == true); p.Children.Add(auto);
        foreach (var source in CampusFeeds.Sources)
        {
            var state = store.Data.CampusSources.FirstOrDefault(s => s.Name == source.Name);
            var age = state?.Updated is DateTime time ? $"{Math.Max(0, (int)(DateTime.Now - time).TotalMinutes)} 分钟前更新（{time:MM-dd HH:mm}）" : "尚未成功更新";
            p.Children.Add(Text(source.Name + " · " + age + " · " + (state?.Status ?? "点击更新活动即可保存到本地"), 11, Muted)); p.Children.Add(Space(5));
        }
        p.Children.Add(Space(14));
        var items = store.Data.CampusActivities.Where(a => campusCategory == "全部" || a.Category == campusCategory)
            .Where(a => campusFilter != "近期活动" || (a.Start is DateTime start ? start >= DateTime.Today && start < DateTime.Today.AddDays(61) : a.Published >= DateTime.Today.AddDays(-30)))
            .Where(a => string.IsNullOrWhiteSpace(search.Text) || (a.Title + a.Location + a.Description).Contains(search.Text.Trim(), StringComparison.OrdinalIgnoreCase))
            .OrderBy(a => a.Start == null).ThenBy(a => a.Start).ThenByDescending(a => a.Published).ToList();
        p.Children.Add(Text($"{items.Count} 条本地记录", 12, Muted)); p.Children.Add(Space());
        foreach (var a in items)
        {
            var added = store.Data.Items.FirstOrDefault(i => i.CampusUrl == a.Url);
            var when = a.Start is DateTime time ? time.ToString("MM月dd日 · HH:mm") : $"活动时间待确认 · 通知发布于 {a.Published:MM-dd}";
            var controls = Row(Button(added == null ? "＋ 加入日程" : "查看已加入日程", () =>
            {
                var existing = store.Data.Items.FirstOrDefault(i => i.CampusUrl == a.Url);
                if (existing != null) Edit(new(existing, existing.Start, existing.End));
                else Edit(null, "活动", a.Start, a);
            }, true), Button("查看原文 ↗", () =>
            {
                if (!CampusFeeds.SafeUrl(a.Url)) return;
                try { Process.Start(new ProcessStartInfo(a.Url) { UseShellExecute = true }); } catch (Exception ex) { MessageBox.Show(this, ex.Message); }
            }));
            p.Children.Add(Card(Stack(Text(a.Category + "  /  " + a.Source, 11, Green), Space(10), Text(a.Title, 18, bold: true), Space(10), Text(when + "  ·  " + (a.Location.Length > 0 ? a.Location : "地点请查看原文"), 13, Green), Space(10), Text(a.Description, 12, Muted), Space(16), controls)));
        }
        if (items.Count == 0) p.Children.Add(Empty("暂无符合条件的校园活动", "可以更新活动、切换分类或查看全部本地记录。断网时仍可查看已保存的信息。"));
    }
}
