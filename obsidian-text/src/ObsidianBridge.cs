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
    static string Slug(string text) => new string(text.Select(c => char.IsLetterOrDigit(c) || c == '-' ? c : '_').Take(35).ToArray());
    static string FileName(Item note) => Slug(note.Title) + "-" + Hash(note.Id)[..16] + ".md";
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
        var paths = notes.ToDictionary(n => n.Id, n => folder + "/" + FileName(n));
        var result = new ExchangeResult();
        foreach (var note in notes)
        {
            try
            {
                string relative = paths[note.Id]; string path = Path.Combine(vault, relative);
                manifest.Notes.TryGetValue(note.Id, out var prior);
                // Stable filenames are based on IDs: preserve a previously exported title after renaming.
                if (prior != null && prior.RelativePath.StartsWith(folder + "/", StringComparison.Ordinal) && !prior.RelativePath.Contains("..") && prior.RelativePath.Split('/').Length == 2 && !prior.RelativePath.Contains('\\'))
                    relative = prior.RelativePath;
                paths[note.Id] = relative;
            }
            catch (Exception ex) { result.Errors.Add(note.Title + "：" + ex.Message); }
        }
        foreach (var note in notes)
        {
            string relative = paths[note.Id], path = Path.Combine(vault, relative);
            try
            {
                manifest.Notes.TryGetValue(note.Id, out var prior);
                string snapshot = JsonSerializer.Serialize(note, Json), sourceHash = Hash("text-only-v2|" + snapshot);
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
