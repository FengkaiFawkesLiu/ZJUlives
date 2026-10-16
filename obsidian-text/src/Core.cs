using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Shike;

public sealed class Attachment
{
    public string Name { get; set; } = "";
    public string File { get; set; } = "";
}

public sealed class Item
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Kind { get; set; } = "活动";
    public string Title { get; set; } = "";
    public DateTime Start { get; set; } = DateTime.Today.AddHours(9);
    public DateTime End { get; set; } = DateTime.Today.AddHours(10);
    public string Location { get; set; } = "";
    public string Note { get; set; } = "";
    public string LibraryId { get; set; } = "default";
    public string NoteFormat { get; set; } = "plain-v1";
    public string NoteBody { get; set; } = "";
    public string NoteInk { get; set; } = "";
    public List<NoteLink> NoteLinks { get; set; } = new();
    public List<Attachment> Attachments { get; set; } = new();
    public string? ParentId { get; set; }
    public string? CampusUrl { get; set; }
    public bool Done { get; set; }
    public bool Cancelled { get; set; }
    public string Repeat { get; set; } = "不重复";
    public DateTime Until { get; set; } = DateTime.Today.AddMonths(4);
    public List<string> Excluded { get; set; } = new();
    public List<int>? Reminders { get; set; }
    public string? AssistantKey { get; set; }
    public bool AssistantNoDue { get; set; }
    public bool AssistantRemoteDone { get; set; }
    public string? SourceKey { get; set; }
    public string? Scope { get; set; }
    public SchoolRecord? Original { get; set; }
    public bool LocalOverride { get; set; }
    public SchoolRecord? Pending { get; set; }
    public bool PendingRemoval { get; set; }
    public bool SchoolRemoved { get; set; }
    public bool SchoolSuspended { get; set; }
    public string SchoolAdjustment { get; set; } = "";
    public string Teacher { get; set; } = "";
    public DateTime Modified { get; set; } = DateTime.Now;
    public bool HasConflict => Pending != null || PendingRemoval;
    public Item Copy() => JsonSerializer.Deserialize<Item>(JsonSerializer.Serialize(this))!;
}

public sealed class SchoolRecord
{
    public string Key { get; set; } = "";
    public string Kind { get; set; } = "课程";
    public string Title { get; set; } = "";
    public DateTime Start { get; set; }
    public DateTime End { get; set; }
    public string Location { get; set; } = "";
    public string Teacher { get; set; } = "";
    public DateTime? BaseStart { get; set; }
    public DateTime? BaseEnd { get; set; }
    public bool Suspended { get; set; }
    public string Adjustment { get; set; } = "";
    public bool Same(SchoolRecord? other) => other != null && Title == other.Title && Kind == other.Kind && Start == other.Start && End == other.End && Location == other.Location && Teacher == other.Teacher && Suspended == other.Suspended && Adjustment == other.Adjustment;
    public static SchoolRecord From(Item item) => new() { Key = item.SourceKey ?? item.Id, Kind = item.Kind, Title = item.Title, Start = item.Start, End = item.End, Location = item.Location, Teacher = item.Teacher, Suspended = item.SchoolSuspended, Adjustment = item.SchoolAdjustment };
}

public sealed class Settings
{
    public bool CourseReminder { get; set; } = false;
    public int CourseMinutes { get; set; } = 15;
    public string ExamMinutes { get; set; } = "1440,60";
    public string LastView { get; set; } = "今日概览";
    public int Year { get; set; } = DateTime.Today.Month >= 8 ? DateTime.Today.Year : DateTime.Today.Year - 1;
    public string Semester { get; set; } = "3";
    public DateTime TermMonday { get; set; } = Engine.Monday(DateTime.Today);
    public int Weeks { get; set; } = 16;
    public int SecondHalfWeek { get; set; } = 9;
    public string Periods { get; set; } = PeriodSchedule.Official;
    public DateTime? LastSync { get; set; }
    public bool AutoCampusRefresh { get; set; } = true;
    public bool AutoSyncOnStartup { get; set; } = true;
    public bool TermConfigured { get; set; }
    public bool ApplyAcademicHolidays { get; set; } = true;
    public DateTime? LastHolidayCheck { get; set; }
    public string HolidayCheckStatus { get; set; } = "内置：2026-09-17 核对的校本部通知";
}

public sealed class AppData
{
    public CampusAssistantState Assistant { get; set; } = new();
    public int Version { get; set; } = 1;
    public int NoteSchemaVersion { get; set; }
    public List<NoteLibrary> NoteLibraries { get; set; } = new() { new() { Id = "default", Name = "我的笔记" } };
    public Settings Settings { get; set; } = new();
    public List<Item> Items { get; set; } = new();
    public List<CampusActivity> CampusActivities { get; set; } = new();
    public List<CampusFeedState> CampusSources { get; set; } = new();
    public HashSet<string> Fired { get; set; } = new();
    public List<string> Alerts { get; set; } = new();
}

public sealed record Occurrence(Item Item, DateTime Start, DateTime End)
{
    public string Key => Item.Id + ":" + Start.ToString("O");
}

public static class Engine
{
    public static DateTime Monday(DateTime day) => day.Date.AddDays(-(((int)day.DayOfWeek + 6) % 7));
    public static bool Overlaps(Occurrence a, Occurrence b) => a.Key != b.Key && a.Start < b.End && b.Start < a.End;
    public static IEnumerable<Occurrence> Between(IEnumerable<Item> items, DateTime from, DateTime to)
    {
        foreach (var i in items.Where(x => !x.Cancelled && !x.AssistantNoDue && x.Kind != "笔记" && !(x.SchoolSuspended && !x.LocalOverride)))
        {
            if (i.Repeat == "不重复")
            {
                if (i.Start < to && (i.End > from || i.Start >= from)) yield return new(i, i.Start, i.End);
                continue;
            }
            int step = i.Repeat == "每天" ? 1 : 7;
            var duration = i.End - i.Start;
            int jump = Math.Max(0, (int)Math.Floor((from - duration - i.Start).TotalDays / step));
            for (var date = i.Start.AddDays(jump * step); date.Date <= i.Until.Date && date < to; date = date.AddDays(step))
                if (date + duration > from && !i.Excluded.Contains(date.ToString("yyyy-MM-dd")))
                    yield return new(i, date, date + duration);
        }
    }
    public static List<int> ParseReminders(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new();
        var parts = text.Replace('，', ',').Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length > 8) throw new InvalidDataException("最多设置 8 次提醒。");
        return parts.Select(p => int.TryParse(p, out var n) && n >= 0 && n <= 525600 ? n : throw new InvalidDataException("提醒填写提前分钟数，例如 1440,60。范围为 0～525600。")).Distinct().ToList();
    }
    public static List<int> Reminders(Item i, Settings s) => i.Reminders ?? (i.Kind == "课程" ? s.CourseReminder ? new() { s.CourseMinutes } : new() : i.Kind == "考试" ? ParseReminders(s.ExamMinutes) : new());
    public static List<(Occurrence Occ, int Minutes, string Key)> Due(AppData data, DateTime now)
    {
        var result = new List<(Occurrence, int, string)>();
        foreach (var o in Between(data.Items.Where(i => !i.Done), now.AddHours(-24), now.AddDays(367)))
            foreach (var min in Reminders(o.Item, data.Settings))
            {
                var due = o.Start.AddMinutes(-min);
                // Deliver overdue reminders only in a 24-hour catch-up window; no old reminder flood.
                var key = o.Key + ":" + min;
                if (due <= now && due > now.AddHours(-24) && !data.Fired.Contains(key)) result.Add((o, min, key));
            }
        return result;
    }
    public static void Validate(AppData data)
    {
        if (data.Version != 1) throw new InvalidDataException("不支持此数据版本。");
        if (data.Items == null || data.Settings == null || data.Fired == null || data.Alerts == null) throw new InvalidDataException("数据缺少必要字段。");
        if (data.CampusActivities == null || data.CampusSources == null || data.CampusActivities.Count > 1000 || data.CampusActivities.Any(a => !CampusFeeds.SafeUrl(a.Url) || string.IsNullOrWhiteSpace(a.Title))) throw new InvalidDataException("校园活动缓存不合法。");
        if (data.Items.Count > 100000) throw new InvalidDataException("记录数量过多。");
        if (data.Items.Select(i => i.Id).Distinct().Count() != data.Items.Count) throw new InvalidDataException("存在重复记录 ID。");
        ParseReminders(data.Settings.ExamMinutes);
        NotesModel.Validate(data);
        AssistantMerge.Validate(data);
        foreach (var i in data.Items)
        {
            if (string.IsNullOrWhiteSpace(i.Id) || string.IsNullOrWhiteSpace(i.Title) || i.Title.Length > 300 || i.End <= i.Start || i.Attachments == null || i.Excluded == null || i.Note == null || i.Location == null) throw new InvalidDataException("记录内容或起止时间不合法。");
            if (!new[] { "活动", "课程", "考试", "待办", "笔记" }.Contains(i.Kind)) throw new InvalidDataException("未知事项类型。");
            if (!new[] { "不重复", "每天", "每周" }.Contains(i.Repeat) || (i.Repeat != "不重复" && i.Until.Date < i.Start.Date)) throw new InvalidDataException("重复规则不合法。");
            if (i.Reminders != null) ParseReminders(string.Join(',', i.Reminders));
            foreach (var a in i.Attachments)
                if (string.IsNullOrWhiteSpace(a.File) || Path.GetFileName(a.File) != a.File || a.File.Contains(':') || a.File.Contains('/') || a.File.Contains('\\')) throw new InvalidDataException("附件路径不合法。");
        }
    }
    public static Item Detach(AppData data, Occurrence occurrence)
    {
        var parent = occurrence.Item;
        if (parent.Repeat == "不重复") return parent;
        parent.Excluded.Add(occurrence.Start.ToString("yyyy-MM-dd"));
        var copy = parent.Copy();
        copy.Id = Guid.NewGuid().ToString("N"); copy.Start = occurrence.Start; copy.End = occurrence.End;
        copy.Repeat = "不重复"; copy.Excluded.Clear(); copy.ParentId = parent.Id;
        data.Items.Add(copy);
        return copy;
    }
    public static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..24];
}

public sealed class Store
{
    public string Root { get; }
    public string Files => Path.Combine(Root, "attachments");
    public AppData Data { get; set; }
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    static AppData Decode(string text)
    {
        using var doc = JsonDocument.Parse(text);
        if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("Version", out _) || !doc.RootElement.TryGetProperty("Items", out _) || !doc.RootElement.TryGetProperty("Settings", out _))
            throw new InvalidDataException("数据缺少版本、事项或设置字段，已停止读取。");
        return JsonSerializer.Deserialize<AppData>(text) ?? throw new InvalidDataException("数据文件为空。");
    }
    public Store(string root)
    {
        Root = Path.GetFullPath(root); Directory.CreateDirectory(Files);
        var file = Path.Combine(Root, "data.json");
        Data = File.Exists(file) ? Decode(File.ReadAllText(file)) : new();
        Engine.Validate(Data);
    }
    public void Save()
    {
        Engine.Validate(Data);
        var file = Path.Combine(Root, "data.json"); var temp = file + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, Data, Json); stream.Flush(true);
        }
        if (File.Exists(file)) File.Replace(temp, file, file + ".bak"); else File.Move(temp, file);
    }
    public Attachment AddFile(string path)
    {
        var name = Guid.NewGuid().ToString("N") + Path.GetExtension(path);
        File.Copy(path, Path.Combine(Files, name));
        return new() { Name = Path.GetFileName(path), File = name };
    }
    public void Backup(string path)
    {
        Save();
        var stage = path + ".tmp";
        if (File.Exists(stage)) File.Delete(stage);
        using (var archive = ZipFile.Open(stage, ZipArchiveMode.Create))
        {
            archive.CreateEntryFromFile(Path.Combine(Root, "data.json"), "data.json");
            foreach (var file in Data.Items.SelectMany(i => i.Attachments).Select(a => a.File).Distinct())
                archive.CreateEntryFromFile(Path.Combine(Files, file), "attachments/" + file);
        }
        File.Move(stage, path, true);
    }
    public void Restore(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        if (zip.Entries.Sum(e => e.Length) > 2L * 1024 * 1024 * 1024) throw new InvalidDataException("备份解压后超过 2 GB。");
        if (zip.Entries.Select(e => e.FullName).Distinct().Count() != zip.Entries.Count) throw new InvalidDataException("备份内包含重复路径。");
        var entry = zip.GetEntry("data.json") ?? throw new InvalidDataException("不是拾课备份文件。");
        using var reader = new StreamReader(entry.Open());
        var restored = Decode(reader.ReadToEnd());
        Engine.Validate(restored);
        foreach (var file in restored.Items.SelectMany(i => i.Attachments).Select(a => a.File).Distinct())
            if (zip.GetEntry("attachments/" + file) == null) throw new InvalidDataException("备份缺少附件，已取消恢复。");
        // Keep a complete pre-restore backup. Give imported attachments new names to avoid
        // touching any current attachment until the new data has been committed.
        Backup(Path.Combine(Root, "恢复前备份-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".zip"));
        var mapping = new Dictionary<string, string>();
        foreach (var a in restored.Items.SelectMany(i => i.Attachments))
        {
            if (!mapping.TryGetValue(a.File, out var target))
            {
                target = Guid.NewGuid().ToString("N") + Path.GetExtension(a.File);
                zip.GetEntry("attachments/" + a.File)!.ExtractToFile(Path.Combine(Files, target));
                mapping[a.File] = target;
            }
            a.File = target;
        }
        var old = Data;
        try { Data = restored; Save(); } catch { Data = old; throw; }
    }
}

public sealed record MergeResult(int Added, int Updated, int Conflicts, int Removed);

public static class SchoolMerge
{
    public static MergeResult Apply(AppData data, string scope, List<SchoolRecord> incoming, bool authoritative)
    {
        if (incoming.Any(r => string.IsNullOrWhiteSpace(r.Key) || string.IsNullOrWhiteSpace(r.Title) || r.End <= r.Start) || incoming.Select(r => r.Key).Distinct().Count() != incoming.Count)
            throw new InvalidDataException("学校数据不完整或 ID 重复，未更新本地安排。");
        int added = 0, updated = 0, conflicts = 0, removed = 0;
        foreach (var source in incoming)
        {
            var item = data.Items.FirstOrDefault(i => i.Scope == scope && i.SourceKey == source.Key);
            if (item == null)
            {
                item = new Item { SourceKey = source.Key, Scope = scope, Original = source };
                Set(item, source); data.Items.Add(item); added++; continue;
            }
            item.PendingRemoval = false;
            if (item.SchoolRemoved && !item.LocalOverride) { Set(item, source); item.Original = source; item.Pending = null; updated++; continue; }
            if (source.Same(item.Original)) { item.Pending = null; continue; }
            if (item.LocalOverride && !source.Same(SchoolRecord.From(item))) { item.Pending = source; conflicts++; }
            else { Set(item, source); item.Original = source; item.Pending = null; updated++; }
        }
        if (authoritative)
        {
            var keys = incoming.Select(i => i.Key).ToHashSet();
            foreach (var item in data.Items.Where(i => i.Scope == scope && i.SourceKey != null && !keys.Contains(i.SourceKey)))
            {
                if (item.SchoolRemoved) continue;
                if (item.LocalOverride) { item.Pending = null; item.PendingRemoval = true; conflicts++; }
                else { item.Cancelled = true; item.SchoolRemoved = true; removed++; }
            }
        }
        return new(added, updated, conflicts, removed);
    }
    public static void Set(Item item, SchoolRecord source)
    {
        item.Title = source.Title; item.Kind = source.Kind; item.Start = source.Start; item.End = source.End; item.Location = source.Location;
        item.Teacher = source.Teacher; item.SchoolSuspended = source.Suspended; item.SchoolAdjustment = source.Adjustment;
        if (item.SchoolRemoved) item.Cancelled = false;
        item.SchoolRemoved = false;
    }
    public static void Resolve(Item item, bool useSchool)
    {
        if (item.PendingRemoval)
        {
            if (useSchool) item.Cancelled = true;
            item.SchoolRemoved = true;
        }
        if (item.Pending != null)
        {
            if (useSchool) { Set(item, item.Pending); item.Cancelled = false; }
            item.Original = item.Pending;
        }
        item.LocalOverride = !useSchool; item.Pending = null; item.PendingRemoval = false;
    }
}

