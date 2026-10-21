using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Shike;

// Versioned boundary for future import adapters. Export never modifies the source Store.
public interface INoteExchange { ExchangeResult Export(Store store, string libraryId); }
public sealed class ExchangeRecord
{
    public string NoteId { get; set; } = "";
    public string LibraryId { get; set; } = "";
    public string RelativePath { get; set; } = "";
    public string ExportHash { get; set; } = "";
    public string SourceHash { get; set; } = "";
    public DateTime ExportedUtc { get; set; }
}
public sealed class ExchangeManifest
{
    public int Version { get; set; } = 1;
    public Dictionary<string, ExchangeRecord> Notes { get; set; } = new();
}
public sealed class ExchangeResult
{
    public int Written, Unchanged;
    public List<string> Conflicts { get; } = new();
    public List<string> Errors { get; } = new();
}
public sealed class ObsidianBridge : INoteExchange
{
    public static string Vault(Store store) => Path.Combine(store.Root, "ObsidianVault");
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    static string TitleName(string title)
    {
        string value = new string(title.Select(c => Path.GetInvalidFileNameChars().Contains(c) || "#[]^|".Contains(c) ? '_' : c).Take(100).ToArray()).Trim().TrimEnd('.');
        if (value.Length == 0) value = "未命名笔记";
        string stem = value.Split('.')[0].ToUpperInvariant();
        if (new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" }.Contains(stem)) value = "_" + value;
        return value;
    }
    static void Atomic(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(tmp, text, new UTF8Encoding(false)); File.Move(tmp, path, true); }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
    }
    static string Escape(string value) => value.Replace("\\", "\\\\").Replace("[", "\\[").Replace("]", "\\]").Replace("*", "\\*").Replace("_", "\\_").Replace("#", "\\#").Replace("$", "\\$");
    public ExchangeResult Export(Store store, string libraryId)
    {
        var library = store.Data.NoteLibraries.Single(l => l.Id == libraryId);
        string vault = Vault(store); Directory.CreateDirectory(vault);
        string manifestPath = Path.Combine(store.Root, "plugins", "obsidian", "manifest.json");
        string readManifest = File.Exists(manifestPath) ? manifestPath : Path.Combine(vault, ".shike", "manifest.json");
        var manifest = File.Exists(readManifest) ? JsonSerializer.Deserialize<ExchangeManifest>(File.ReadAllText(readManifest)) ?? throw new InvalidDataException("导出记录损坏") : new();
        if (manifest.Version != 1) throw new InvalidDataException("不支持的导出记录版本");
        var notes = store.Data.Items.Where(n => n.Kind == "笔记" && !n.Cancelled && n.LibraryId == libraryId).ToList();
        string folder = "library-" + Hash(libraryId)[..16];
        var paths = new Dictionary<string, string>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new ExchangeResult();
        bool Safe(string path) => path.StartsWith(folder + "/", StringComparison.Ordinal) && path.Split('/').Length == 2 && !path.Contains('\\') && !path.Contains("..") && Path.GetFileName(path).IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
        foreach (var record in manifest.Notes.Values.Where(r => Safe(r.RelativePath))) used.Add(record.RelativePath);
        string NewPath(Item note) {
            string title = TitleName(note.Title), candidate = folder + "/" + title + ".md"; int suffix = 2;
            while (used.Contains(candidate) || File.Exists(Path.Combine(vault,candidate))) candidate = folder + "/" + title + "（" + suffix++ + "）.md";
            used.Add(candidate); return candidate;
        }
        foreach (var note in notes)
        {
            manifest.Notes.TryGetValue(note.Id, out var prior);
            if (prior == null || !Safe(prior.RelativePath)) { paths[note.Id] = NewPath(note); continue; }
            paths[note.Id] = prior.RelativePath;
            // Only migrate known legacy generated names, never rename user-edited exports.
            string legacySuffix = "-" + Hash(note.Id)[..16] + ".md";
            if (!prior.RelativePath.EndsWith(legacySuffix, StringComparison.Ordinal)) continue;
            string oldPath = Path.Combine(vault, prior.RelativePath);
            if (File.Exists(oldPath) && Hash(File.ReadAllText(oldPath)) != prior.ExportHash) continue;
            try {
                string next = NewPath(note), destination = Path.Combine(vault,next);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                if (File.Exists(oldPath)) File.Move(oldPath,destination);
                prior.RelativePath = next; prior.SourceHash = ""; paths[note.Id] = next;
                Atomic(manifestPath, JsonSerializer.Serialize(manifest, Json));
            } catch (Exception ex) { result.Errors.Add(note.Title + "：重命名失败：" + ex.Message); }
        }
        string linkRevision = JsonSerializer.Serialize(paths);
        foreach (var note in notes)
        {
            string relative = paths[note.Id], path = Path.Combine(vault, relative);
            try
            {
                manifest.Notes.TryGetValue(note.Id, out var prior);
                string snapshot = JsonSerializer.Serialize(note, Json), sourceHash = Hash("title-names-v3|" + snapshot + linkRevision);
                string? existing = File.Exists(path) ? File.ReadAllText(path) : null;
                if (existing != null && (prior == null || Hash(existing) != prior.ExportHash)) { result.Conflicts.Add(note.Title); continue; }
                if (existing != null && prior?.SourceHash == sourceHash) { result.Unchanged++; continue; }
                string body;
                if (note.NoteFormat == MarkdownNotes.Format) body = note.NoteBody;
                else if (note.NoteFormat.StartsWith("rtf-"))
                {
                    var doc = new RichNoteCodec().Decode(note.NoteBody, note.Note);
                    body = Blocks(doc.Blocks, vault);

                }
                else body = Escape(note.Note);
                var text = new StringBuilder(body);
                foreach (var link in note.NoteLinks)
                {
                    var target = notes.FirstOrDefault(n => NotesModel.Key(n) == link.Key);
                    text.Append(target != null ? "\n\n[[" + paths[target.Id][..^3] + "|" + target.Title.Replace("|", " ").Replace("]", " ").Replace("\n", " ") + "]]" : "\n\n" + Escape(link.Label));
                }
                string output = text.ToString() + "\n";
                // Recheck immediately before replacement, including newly-created files.
                if ((File.Exists(path) ? File.ReadAllText(path) : null) != existing) { result.Conflicts.Add(note.Title); continue; }
                Atomic(path, output);
                manifest.Notes[note.Id] = new() { NoteId = note.Id, LibraryId = libraryId, RelativePath = relative, ExportHash = Hash(output), SourceHash = sourceHash, ExportedUtc = DateTime.UtcNow };
                Atomic(manifestPath, JsonSerializer.Serialize(manifest, Json)); result.Written++;
            }
            catch (Exception ex) { result.Errors.Add(note.Title + "：" + ex.Message); }
        }
        return result;
    }
    static string Blocks(BlockCollection blocks, string vault) => string.Join("\n\n", blocks.Cast<Block>().Select(b => b switch {
        Paragraph p => Inlines(p.Inlines, vault), Section s => Blocks(s.Blocks, vault),
        System.Windows.Documents.List list => string.Join("\n", list.ListItems.Cast<ListItem>().Select(i => "- " + Blocks(i.Blocks, vault))),
        BlockUIContainer c => Element(c.Child, vault),
        _ => Escape(new TextRange(b.ContentStart, b.ContentEnd).Text)
    }));
    static string Inlines(InlineCollection list, string vault) => string.Concat(list.Cast<Inline>().Select(i => i switch {
        Run r => Escape(r.Text), LineBreak => "  \n", Bold b => "**" + Inlines(b.Inlines, vault) + "**", Italic e => "*" + Inlines(e.Inlines, vault) + "*",
        Span s => Inlines(s.Inlines, vault), InlineUIContainer c => Element(c.Child, vault), Figure f => Blocks(f.Blocks, vault), Floater f => Blocks(f.Blocks, vault), _ => ""
    }));
    static string Element(UIElement element, string vault)
    {
        if (element is Image image)
        {
            if (image.Tag is NoteEmbed embed && embed.Kind == "math") return "\n\n$$\n" + embed.Source + "\n$$\n\n";

        }
        return "";
    }
}
