using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;

namespace Shike;
public static class AcademicLogout
{
    // Preserve personal content, including homework todos; only course/exam records and their references are removed.
    public static HashSet<string> Clear(AppData data)
    {
        var removed = data.Items.Where(i => i.Kind is "课程" or "考试").ToList();
        var ids = removed.Select(i => i.Id).ToHashSet();
        var keys = removed.Select(NotesModel.Key).Concat(removed.Select(i => "item:" + i.Id)).ToHashSet();
        var files = removed.SelectMany(i => i.Attachments).Select(a => a.File).ToHashSet();
        data.Items.RemoveAll(i => ids.Contains(i.Id));
        foreach (var item in data.Items)
        {
            bool changed = item.NoteLinks.RemoveAll(l => l.Kind == "course" || keys.Contains(l.Key)) > 0;
            if (item.ParentId != null && ids.Contains(item.ParentId)) { item.ParentId = null; changed = true; }
            if (changed) item.Modified = DateTime.Now;
        }
        foreach (var library in data.NoteLibraries)
            foreach (var key in library.Positions.Keys.Where(k => k.StartsWith("course:") || keys.Contains(k)).ToList()) library.Positions.Remove(key);
        data.Fired.RemoveWhere(k => ids.Any(id => k.StartsWith(id + ":", StringComparison.Ordinal)));
        data.Alerts.RemoveAll(a => removed.Any(i => a.Contains("  ·  " + i.Title + "  ·  安排于 ", StringComparison.Ordinal)) || a.Contains(" · 学校更新：") || a.Contains(" · 本次未完整更新："));
        data.Settings.LastSync = null;
        data.Assistant.SignLog.Clear(); data.Assistant.SignAttempted.Clear();
        files.ExceptWith(data.Items.SelectMany(i => i.Attachments).Select(a => a.File));
        return files;
    }
    public static void Purge(Store store)
    {
        var previous = store.Data;
        store.Data = JsonSerializer.Deserialize<AppData>(JsonSerializer.Serialize(previous))!;
        string pending = Path.Combine(store.Root, "academic-cleanup.json");
        HashSet<string> files;
        try
        {
            files = Clear(store.Data);
            if (File.Exists(pending)) files.UnionWith(JsonSerializer.Deserialize<HashSet<string>>(File.ReadAllText(pending)) ?? new());
            File.WriteAllText(pending, JsonSerializer.Serialize(files));
            store.Save();
        }
        catch { store.Data = previous; throw; }
        // Save rotates the old data into .bak. Replace that snapshot with the already-sanitized data.
        File.Copy(Path.Combine(store.Root, "data.json"), Path.Combine(store.Root, "data.json.bak"), true);
        foreach (var path in Directory.EnumerateFiles(store.Root, "*.zip"))
        {
            // Only app-generated history; explicitly exported backups elsewhere remain under the user's control.
            var name = Path.GetFileName(path);
            if (!(name.StartsWith("恢复前备份-") || name.StartsWith("笔记系统升级前-") || name.StartsWith("节次时间修复前-") || name.StartsWith("校历"))) continue;
            files.UnionWith(SanitizeBackup(path));
            File.WriteAllText(pending, JsonSerializer.Serialize(files));
        }
        files.ExceptWith(store.Data.Items.SelectMany(i => i.Attachments).Select(a => a.File));
        foreach (var file in files)
        {
            if (string.IsNullOrEmpty(file) || Path.GetFileName(file) != file) continue;
            File.Delete(Path.Combine(store.Files, file));
        }
        File.Delete(pending);
    }
    static HashSet<string> SanitizeBackup(string path)
    {
        string temp = path + ".logout.tmp";
        HashSet<string> removedFiles;
        try
        {
            using (var input = ZipFile.OpenRead(path))
            {
                var entry = input.GetEntry("data.json"); if (entry == null) return new();
                using var reader = new StreamReader(entry.Open());
                var data = JsonSerializer.Deserialize<AppData>(reader.ReadToEnd()) ?? throw new InvalidDataException("历史备份不可读取。");
                removedFiles = Clear(data); Engine.Validate(data);
                using var output = ZipFile.Open(temp, ZipArchiveMode.Create);
                foreach (var e in input.Entries)
                {
                    if (e.FullName == "data.json" || removedFiles.Any(f => e.FullName == "attachments/" + f)) continue;
                    using var from = e.Open(); using var to = output.CreateEntry(e.FullName).Open(); from.CopyTo(to);
                }
                using var stream = output.CreateEntry("data.json").Open(); JsonSerializer.Serialize(stream, data);
            }
            File.Move(temp, path, true);
            return removedFiles;
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}

