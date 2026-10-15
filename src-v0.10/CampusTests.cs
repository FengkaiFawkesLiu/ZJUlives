using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Shike;
public static class CampusTests
{
    public static void Run(Action<string, Action> test, string temp)
    {
        void Assert(bool value) { if (!value) throw new Exception("Assertion failed"); }
        var fixture = Path.Combine(AppContext.BaseDirectory, "fixtures");
        List<CampusActivity> Read(int index, string name) => CampusFeeds.Parse(CampusFeeds.Sources[index], File.ReadAllText(Path.Combine(fixture, name)), DateTime.Now);
        test("Actual official lecture HTML yields upcoming lectures with rooms", () => { var list = Read(0, "activities-cds.html"); Assert(list.Any(a => a.Start == new DateTime(2026, 9, 21, 16, 0, 0) && a.Location.Contains("1312"))); Assert(list.All(a => a.Start != null && !a.Title.Contains("钉钉群"))); });
        test("Actual club feed keeps publication date separate from event time", () => { var list = Read(1, "activities-arts.html"); Assert(list.Any(a => a.Title.Contains("武术队纳新") && a.Published == new DateTime(2026, 9, 8))); Assert(list.All(a => a.Start == null)); });
        test("Sports source parses dates and official links", () => { var list = Read(2, "activities-sports.html"); Assert(list.Count > 0 && list.All(a => CampusFeeds.SafeUrl(a.Url) && a.Category == "体育")); });
        test("Activity merge deduplicates and preserves personal calendar edits", () => { var data = new AppData(); var list = Read(0, "activities-cds.html"); data.Items.Add(new() { Title = "我的安排", CampusUrl = list[0].Url, Note = "个人笔记" }); CampusFeeds.Merge(data, list); CampusFeeds.Merge(data, list); Assert(data.CampusActivities.Count == list.Count && data.Items.Single().Note == "个人笔记"); });
        test("Malformed page fails without returning empty successful refresh", () => { try { CampusFeeds.Parse(CampusFeeds.Sources[0], "<html>login</html>", DateTime.Now); throw new Exception("Accepted malformed page"); } catch (InvalidDataException) { } });
        test("Only official HTTPS activity URLs accepted", () => Assert(!CampusFeeds.SafeUrl("https://zju.edu.cn.attacker.com/a") && !CampusFeeds.SafeUrl("file:///C:/test") && !CampusFeeds.SafeUrl("https://zju.edu.cn:444/a")));
        test("Activity cache survives local backup and restore", () => { var s = new Store(Path.Combine(temp, "campus")); CampusFeeds.Merge(s.Data, Read(0, "activities-cds.html")); s.Data.CampusSources.Add(new() { Name = "test", Updated = DateTime.Now }); var zip = Path.Combine(temp, "campus.zip"); s.Backup(zip); s.Data.CampusActivities.Clear(); s.Save(); s.Restore(zip); Assert(s.Data.CampusActivities.Count > 0 && new Store(s.Root).Data.CampusSources.Count == 1); });
        test("Generate dated public activity seed from fetched official pages", () => { var entries = Read(0, "activities-cds.html").Concat(Read(1, "activities-arts.html")).Concat(Read(2, "activities-sports.html")).ToList(); File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "campus-seed.json"), JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true })); Assert(entries.Count >= 20); });
    }
}
