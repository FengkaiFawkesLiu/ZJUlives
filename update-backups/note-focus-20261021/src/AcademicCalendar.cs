using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Shike;

public sealed record AcademicDay(string Label, string Detail, bool Holiday);

public static class AcademicCalendar
{
    public const string SourceUrl = "https://zdbk.zju.edu.cn/jwglxt/xtgl/xwck_ckLoginNews.html?xwbh=5ADCDCE300FB9DBBE06329B3CA0A2097";
    public const string SourceTitle = "2026年下半年部分节假日及学校活动期间教学安排（2026-09-07）";
    public const string Coverage = "校本部本科 · 2026 下半年通知，含 2027-01-04 补课";
    static DateTime D(int month, int day, int year = 2026) => new(year, month, day);
    // These are explicit course exchanges, not national employee workday rules.
    static readonly Dictionary<DateTime, DateTime> Moves = new()
    {
        [D(10, 6)] = D(9, 20), [D(9, 20)] = D(10, 6),
        [D(10, 7)] = D(10, 10), [D(10, 10)] = D(10, 7),
        [D(10, 2)] = D(10, 17), [D(12, 31)] = D(1, 4, 2027)
    };
    static readonly HashSet<DateTime> Suspensions = new() { D(9, 25), D(10, 1), D(10, 3), D(10, 5) };
    public static AcademicDay? Day(DateTime date)
    {
        date = date.Date;
        if (date >= D(9, 25) && date <= D(9, 27)) return new("中秋假期", date == D(9, 25) ? "9/25 停课；9/26–27 周末课程按原安排，以开课单位通知为准。" : "假期中的周末课程不一律取消，按原安排及开课单位通知执行。", true);
        if (date >= D(10, 1) && date <= D(10, 7)) return new("国庆假期", date == D(10, 6) ? "与 9/20 对调：今天按原 9/20 的课程安排。" : date == D(10, 7) ? "与 10/10 对调：今天按原 10/10 的课程安排。" : date == D(10, 4) ? "原周日课程不一律取消，以开课单位通知为准。" : date == D(10, 2) ? "今日课程移至 10/17 补上。" : "学校通知：今日停课。", true);
        if (date == D(9, 20)) return new("调课 · 按 10/6", "与 10/6 对调课程，使用原 10/6 的周次及节次；地点以学校最新通知为准。", false);
        if (date == D(10, 10)) return new("调课 · 按 10/7", "与 10/7 对调课程，使用原 10/7 的周次及节次。", false);
        if (date == D(10, 17)) return new("补课 · 补 10/2", "补 10/2 的本科课程。原有同日课程若与补课重叠，请核对开课单位通知。", false);
        if (date == D(12, 31)) return new("浙江大学学生节", "今日停课，移至 2027/1/4 补课。", true);
        if (date == D(1, 4, 2027)) return new("补课 · 补 12/31", "补 2026/12/31 的课程；如与原有安排重叠，请核对开课单位通知。", false);
        return null;
    }
    public static SchoolRecord Adjust(SchoolRecord source, bool enabled)
    {
        var r = JsonSerializer.Deserialize<SchoolRecord>(JsonSerializer.Serialize(source))!;
        if (r.Kind != "课程") return r;
        r.BaseStart ??= r.Start; r.BaseEnd ??= r.End;
        r.Start = r.BaseStart.Value; r.End = r.BaseEnd.Value; r.Suspended = false; r.Adjustment = "";
        if (!enabled) return r;
        if (Moves.TryGetValue(r.Start.Date, out var target))
        {
            var delta = target - r.Start.Date; r.Start += delta; r.End += delta;
            r.Adjustment = $"学校调补课 · 原 {r.BaseStart:MM/dd} → {target:MM/dd}";
        }
        else if (Suspensions.Contains(r.Start.Date)) { r.Suspended = true; r.Adjustment = "学校节假日停课 · " + r.Start.ToString("MM/dd"); }
        return r;
    }
    public static int ApplyCached(AppData data)
    {
        int conflicts = 0;
        foreach (var group in data.Items.Where(i => i.Kind == "课程" && i.SourceKey != null && i.Original != null && !i.SchoolRemoved && !i.PendingRemoval).GroupBy(i => i.Scope).ToList())
        {
            var adjusted = group.Select(i => Adjust(i.Pending ?? i.Original!, data.Settings.ApplyAcademicHolidays)).ToList();
            conflicts += SchoolMerge.Apply(data, group.Key!, adjusted, false).Conflicts;
        }
        return conflicts;
    }
    public static void MigrateCached(Store store)
    {
        string before = JsonSerializer.Serialize(store.Data);
        ApplyCached(store.Data);
        if (before == JsonSerializer.Serialize(store.Data)) return;
        var adjusted = store.Data;
        store.Data = JsonSerializer.Deserialize<AppData>(before)!;
        store.Backup(Path.Combine(store.Root, "校历调整前-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".zip"));
        store.Data = adjusted;
        try { store.Save(); } catch { store.Data = JsonSerializer.Deserialize<AppData>(before)!; throw; }
    }
    // Verify all relevant clauses, not just a date or a holiday name. A changed notice is
    // not silently reinterpreted; local verified rules remain available with a warning.
    public static bool MatchesKnownNotice(string html)
    {
        string text = Regex.Replace(WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]*>", "")), @"\s+", "").Replace('（', '(').Replace('）', ')');
        var required = new[]
        {
            "2026年下半年部分节假日", "9月25日(周五)至27日(周日)放假", "9月25日(周五)停课",
            "10月1日(周四)至7日(周三)放假", "10月1日(周四)至3日(周六)、5日(周一)停课",
            "10月6日(周二)、10月7日(周三)分别与9月20日(周日)、10月10日(周六)的课对调",
            "10月17日(周六)补10月2日(周五)的课", "12月31日(周四)停课，安排在2027年1月4日(周一)补课"
        };
        return required.All(text.Contains);
    }
    public static async Task<bool> CheckNotice()
    {
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(15) };
        using var result = await client.GetAsync(SourceUrl); result.EnsureSuccessStatusCode();
        return MatchesKnownNotice(await result.Content.ReadAsStringAsync());
    }
}

public static class Freshness
{
    public static string Format(DateTime? lastSuccess, DateTime now)
    {
        if (lastSuccess == null) return "尚未成功更新";
        var minutes = Math.Max(0, (long)(now - lastSuccess.Value).TotalMinutes);
        return minutes == 0 ? "信息更新于刚刚（不足 1 分钟）" : $"信息更新于 {minutes:N0} 分钟前";
    }
}
