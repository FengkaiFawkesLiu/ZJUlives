using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
namespace Shike;
public sealed class AssistantEntry
{
    public string Key { get; set; } = "";
    public string Title { get; set; } = "";
    public string Course { get; set; } = "";
    public string Url { get; set; } = "";
    public DateTime? Due { get; set; }
    public bool CompletionKnown { get; set; } = true;
    public bool Complete { get; set; }
    public bool Renewable { get; set; }
    public string Barcode { get; set; } = "";
}
public sealed class AssistantSource
{
    public string Account { get; set; } = "";
    public DateTime? Updated { get; set; }
    public string Status { get; set; } = "尚未连接";
    public List<AssistantEntry> Entries { get; set; } = new();
}
public sealed class CampusAssistantState
{
    public bool AutoRefresh { get; set; } = true;
    public bool PintiaEnabled { get; set; }
    public AssistantSource Courses { get; set; } = new();
    public AssistantSource Library { get; set; } = new();
    public AssistantSource Pintia { get; set; } = new();
    public List<string> SignLog { get; set; } = new();
    public HashSet<string> SignAttempted { get; set; } = new();
    public string DeviceId { get; set; } = Guid.NewGuid().ToString();
}
public static class AssistantMerge
{
    public const string LibraryWebsite = "https://libweb.zju.edu.cn/";
    public static void Apply(AppData data, string source, string account, List<AssistantEntry> entries, DateTime now)
    {
        if (source is not ("courses" or "library" or "pintia") || entries.Count > 20000 || entries.Any(e => string.IsNullOrEmpty(e.Key) || e.Key.Length > 300 || e.Due is DateTime date && (date.Year < 2000 || date.Year > 2200) || string.IsNullOrWhiteSpace(e.Title) || e.Title.Length > 270 || !SafeUrl(e.Url))) throw new InvalidDataException("校园助手数据格式异常，保留上次记录。");
        if (entries.Select(e => e.Key).Distinct().Count() != entries.Count) throw new InvalidDataException("学校返回重复条目，未修改本地记录。");
        var state = source == "courses" ? data.Assistant.Courses : source == "library" ? data.Assistant.Library : data.Assistant.Pintia;
        string prefix = source + ":" + account + ":";
        var present = new HashSet<string>();
        foreach (var entry in entries)
        {
            string key = prefix + entry.Key; present.Add(key);
            var item = data.Items.SingleOrDefault(i => i.AssistantKey == key);
            if (entry.Due == null) { if (item != null) { item.AssistantNoDue = true; if (entry.CompletionKnown) { if (entry.Complete) item.Done = true; else if (item.AssistantRemoteDone) item.Done = false; item.AssistantRemoteDone = entry.Complete; } } continue; }
            if (item == null)
            {
                item = new Item { Kind = "待办", AssistantKey = key, Reminders = new() { 1440, 60 }, Title = "待同步" }; data.Items.Add(item);
            }
            item.Title = (source == "library" ? "还书 · " : source == "pintia" ? "Pintia · " : "作业 · ") + entry.Title;
            item.Location = entry.Course; item.CampusUrl = entry.Url;
            item.AssistantNoDue = false; item.Start = entry.Due.Value; item.End = entry.Due.Value.AddMinutes(15);
            if (entry.CompletionKnown) { if (entry.Complete) item.Done = true; else if (item.AssistantRemoteDone) item.Done = false; item.AssistantRemoteDone = entry.Complete; }
            item.Modified = now;
        }
        // An authoritative library snapshot proves a book was returned. Missing coursework is not proof of submission.
        if (source == "library") foreach (var item in data.Items.Where(i => i.AssistantKey?.StartsWith(prefix, StringComparison.Ordinal) == true && !present.Contains(i.AssistantKey))) { item.Done = true; item.AssistantRemoteDone = true; }
        state.Entries = entries; state.Account = account; state.Updated = now; state.Status = "更新成功";
    }
    public static void Validate(AppData data)
    {
        var a = data.Assistant;
        if (a == null || a.Courses == null || a.Library == null || a.Pintia == null || a.SignLog == null || a.SignAttempted == null || !Guid.TryParse(a.DeviceId, out _) || a.SignLog.Count > 100 || a.SignAttempted.Count > 100000) throw new InvalidDataException("校园助手缓存无效。");
        foreach (var s in new[] { a.Courses, a.Library, a.Pintia })
            if (s.Entries == null || s.Entries.Count > 20000 || s.Account == null || s.Entries.Any(e => e == null || string.IsNullOrEmpty(e.Key) || string.IsNullOrWhiteSpace(e.Title) || !SafeUrl(e.Url))) throw new InvalidDataException("校园助手条目无效。");
        var keys = data.Items.Where(i => i.AssistantKey != null).Select(i => i.AssistantKey).ToList();
        if (keys.Count != keys.Distinct().Count()) throw new InvalidDataException("校园助手存在重复关联。");
    }
    public static bool SafeUrl(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == "https" && u.IsDefaultPort && string.IsNullOrEmpty(u.UserInfo) && new[] { "courses.zju.edu.cn", "m.lib.zju.edu.cn", "libweb.zju.edu.cn", "pintia.cn" }.Contains(u.Host);
    public static void Log(AppData data, string message) { data.Assistant.SignLog.Add(DateTime.Now.ToString("MM-dd HH:mm:ss") + " · " + message); data.Assistant.SignLog = data.Assistant.SignLog.TakeLast(100).ToList(); }
}
