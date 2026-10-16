using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Shike;

public sealed class NoteLibrary
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "我的笔记";
    public bool AllowCourses { get; set; } = true;
    public bool AllowItems { get; set; } = true;
    public Dictionary<string, NotePosition> Positions { get; set; } = new();
}
public sealed class NotePosition { public double X { get; set; } public double Y { get; set; } }
public sealed class NoteLink
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public string Kind { get; set; } = "note";
}
public sealed record NoteNode(string Key, string Label, string Kind, string Detail, bool Missing = false);
public sealed record NoteEdge(string From, string To);
public sealed record NoteGraph(List<NoteNode> Nodes, List<NoteEdge> Edges);

public static class NotesModel
{
    // Note-to-note edges are materialized on both endpoints; external targets expose the same edge by reverse lookup.
    public static void SyncReciprocal(AppData data, Item note)
    {
        note.NoteLinks = note.NoteLinks.Where(l => l.Key != Key(note)).DistinctBy(l => l.Key).ToList();
        foreach (var peer in data.Items.Where(i => i.Kind == "笔记" && i.LibraryId == note.LibraryId && i.Id != note.Id))
        {
            bool wanted = note.NoteLinks.Any(l => l.Kind == "note" && l.Key == Key(peer));
            var existing = peer.NoteLinks.Where(l => l.Key == Key(note)).ToList();
            if (wanted && (existing.Count != 1 || existing[0].Label != note.Title))
            {
                peer.NoteLinks.RemoveAll(l => l.Key == Key(note));
                peer.NoteLinks.Add(new() { Kind = "note", Key = Key(note), Label = note.Title }); peer.Modified = DateTime.Now;
            }
            else if (!wanted && existing.Count > 0) { peer.NoteLinks.RemoveAll(l => l.Key == Key(note)); peer.Modified = DateTime.Now; }
        }
    }
    public static bool Connect(AppData data, NoteLibrary library, string sourceKey, NoteNode target)
    {
        var note = data.Items.Single(i => i.Kind == "笔记" && i.LibraryId == library.Id && Key(i) == sourceKey);
        if (sourceKey == target.Key || target.Missing) return false;
        if (!Targets(data, library, note.Id).Any(t => t.Key == target.Key)) throw new InvalidOperationException("此节点不可关联。");
        bool added = !note.NoteLinks.Any(l => l.Key == target.Key);
        if (added) { note.NoteLinks.Add(new() { Key = target.Key, Kind = target.Kind, Label = target.Label }); note.Modified = DateTime.Now; }
        SyncReciprocal(data, note); return added;
    }
    public static string CourseKey(Item i) => "course:" + Engine.Hash((i.Scope ?? "个人课程") + "|" + i.Title.Trim() + "|" + i.Teacher.Trim());
    public static string Key(Item i) => i.Kind == "课程" ? CourseKey(i) : (i.Kind == "笔记" ? "note:" : "item:") + i.Id;
    public static List<NoteNode> Targets(AppData data, NoteLibrary library, string? exclude = null)
    {
        var result = data.Items.Where(i => i.Kind == "笔记" && !i.Cancelled && i.LibraryId == library.Id && i.Id != exclude)
            .Select(i => new NoteNode(Key(i), i.Title, "note", "笔记")).ToList();
        if (library.AllowCourses)
            result.AddRange(data.Items.Where(i => i.Kind == "课程" && !i.SchoolRemoved).GroupBy(CourseKey).Select(g => new NoteNode(g.Key, g.First().Title, "course", "整门课程 · " + g.First().Teacher + " · " + g.Count() + " 次安排")));
        if (library.AllowItems)
        {
            result.AddRange(data.Items.Where(i => i.Kind is not ("课程" or "笔记") && !i.Cancelled).Select(i => new NoteNode(Key(i), i.Title, "item", i.Kind + " · " + i.Start.ToString("MM-dd HH:mm"))));
            result.AddRange(data.CampusActivities.Select(a => new NoteNode("campus:" + a.Url, a.Title, "campus", "校园活动 · " + a.Source)));
        }
        return result.OrderBy(n => n.Kind).ThenBy(n => n.Label).ToList();
    }
    public static bool Visible(NoteLink link, NoteLibrary library) => link.Kind == "note" || link.Kind == "course" && library.AllowCourses || link.Kind is "item" or "campus" && library.AllowItems;
    public static NoteGraph Graph(AppData data, NoteLibrary library)
    {
        var notes = data.Items.Where(i => i.Kind == "笔记" && !i.Cancelled && i.LibraryId == library.Id).ToList();
        var targets = Targets(data, library).ToDictionary(n => n.Key);
        var nodes = notes.Select(i => targets[Key(i)]).ToDictionary(n => n.Key);
        var edges = new List<NoteEdge>(); var seen = new HashSet<string>();
        foreach (var note in notes)
            foreach (var link in note.NoteLinks.Where(l => Visible(l, library)))
            {
                if (link.Key == Key(note)) continue;
                if (link.Kind == "note" && !targets.ContainsKey(link.Key)) continue;
                if (!nodes.ContainsKey(link.Key)) nodes[link.Key] = targets.TryGetValue(link.Key, out var found) ? found : new(link.Key, link.Label, link.Kind, "原事项已移除 · 保留关联记录", true);
                var edgeKey = string.CompareOrdinal(Key(note), link.Key) < 0 ? Key(note) + "|" + link.Key : link.Key + "|" + Key(note);
                if (seen.Add(edgeKey)) edges.Add(new(Key(note), link.Key));
            }
        return new(nodes.Values.ToList(), edges);
    }
    public static void Validate(AppData data)
    {
        if (data.NoteLibraries == null || data.NoteLibraries.Count == 0 || data.NoteLibraries.Select(l => l.Id).Distinct().Count() != data.NoteLibraries.Count) throw new InvalidDataException("笔记库数据不合法。");
        foreach (var library in data.NoteLibraries)
        {
            if (string.IsNullOrWhiteSpace(library.Id) || string.IsNullOrWhiteSpace(library.Name) || library.Name.Length > 80 || library.Positions == null || library.Positions.Values.Any(p => !double.IsFinite(p.X) || !double.IsFinite(p.Y) || p.X < 0 || p.Y < 0 || p.X > 10000 || p.Y > 10000)) throw new InvalidDataException("笔记库名称或图谱位置不合法。");
        }
        foreach (var note in data.Items.Where(i => i.Kind == "笔记"))
        {
            if (!data.NoteLibraries.Any(l => l.Id == note.LibraryId) || note.NoteLinks == null || note.NoteInk == null || note.NoteInk.Length > 8 * 1024 * 1024 || note.NoteBody == null || note.NoteBody.Length > 8 * 1024 * 1024 || note.NoteFormat is not ("plain-v1" or "rtf-v1" or "rtf-v2" or "markdown-v1")) throw new InvalidDataException("笔记格式或所属笔记库不合法。");
            foreach (var link in note.NoteLinks)
            {
                if (link.Kind is not ("note" or "course" or "item" or "campus") || !link.Key.StartsWith(link.Kind + ":") || link.Label == null) throw new InvalidDataException("笔记关联格式不合法。");
                if (link.Kind == "note")
                {
                    var target = data.Items.FirstOrDefault(i => "note:" + i.Id == link.Key && i.Kind == "笔记");
                    if (target != null && target.LibraryId != note.LibraryId) throw new InvalidDataException("第一版不支持跨笔记库关联。");
                }
            }
        }
    }
    public static void Migrate(Store store)
    {
        if (store.Data.NoteSchemaVersion >= 2) return;
        var before = JsonSerializer.Serialize(store.Data);
        if (File.Exists(Path.Combine(store.Root, "data.json"))) store.Backup(Path.Combine(store.Root, "笔记系统升级前-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".zip"));
        try
        {
            foreach (var note in store.Data.Items.Where(i => i.Kind == "笔记" && store.Data.NoteSchemaVersion < 1))
            {
                var parent = store.Data.Items.FirstOrDefault(i => i.Id == note.ParentId);
                if (parent != null && parent.Id != note.Id && !note.NoteLinks.Any(l => l.Key == Key(parent))) note.NoteLinks.Add(new() { Key = Key(parent), Kind = parent.Kind == "课程" ? "course" : parent.Kind == "笔记" ? "note" : "item", Label = parent.Title });
            }
            // Union existing directed links before normalization, so no legacy edge is lost.
            foreach (var note in store.Data.Items.Where(i => i.Kind == "笔记").ToList())
                foreach (var link in note.NoteLinks.Where(l => l.Kind == "note").ToList())
                {
                    var peer = store.Data.Items.FirstOrDefault(i => i.Kind == "笔记" && i.LibraryId == note.LibraryId && Key(i) == link.Key);
                    if (peer != null && peer.Id != note.Id && !peer.NoteLinks.Any(l => l.Key == Key(note))) peer.NoteLinks.Add(new() { Kind = "note", Key = Key(note), Label = note.Title });
                }
            foreach (var note in store.Data.Items.Where(i => i.Kind == "笔记")) SyncReciprocal(store.Data, note);
            store.Data.NoteSchemaVersion = 2; store.Save();
        }
        catch { store.Data = JsonSerializer.Deserialize<AppData>(before)!; throw; }
    }
}


