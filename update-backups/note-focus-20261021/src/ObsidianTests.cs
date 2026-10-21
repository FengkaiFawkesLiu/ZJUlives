using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Shike;
public static class ObsidianTests
{
    public static void Run(Action<string, Action> test, string temp)
    {
        void Assert(bool yes) { if (!yes) throw new Exception("Obsidian assertion failed"); }
        test("Obsidian unregistered and unreadable registration choose vault instead of invalid note URI", () => {
            Assert(ObsidianLaunch.FindVaultId("{}", @"E:\ZJUlives\data\ObsidianVault") == null);
            Assert(ObsidianLaunch.FindVaultId("broken", @"E:\ZJUlives\data\ObsidianVault") == null);
            Assert(ObsidianLaunch.UriFor(null) == "obsidian://choose-vault");
        });
        test("Obsidian registered vault resolves exact normalized path and uses encoded ID", () => {
            var json = JsonSerializer.Serialize(new { vaults = new System.Collections.Generic.Dictionary<string,object> {
                ["wrong"] = new { path = @"E:\ZJUlives\data\ObsidianVault-old" },
                ["correct id"] = new { path = "e:/ZJUlives/data/ObsidianVault/" }
            } });
            var id=ObsidianLaunch.FindVaultId(json,@"E:\ZJUlives\data\ObsidianVault");
            Assert(id=="correct id" && ObsidianLaunch.UriFor(id)=="obsidian://open?vault=correct%20id");
            Assert(ObsidianLaunch.FindVaultId(json,@"E:\another\ObsidianVault")==null);
        });
        test("Readable filenames disambiguate duplicates and migrate legacy links", () => {
            var store=new Store(Path.Combine(temp,"readable-names"));
            var a=new Item {Kind="笔记",Title="救援训练",Note="A"};var b=new Item {Kind="笔记",Title="救援训练",Note="B"};
            a.NoteLinks.Add(new(){Key=NotesModel.Key(b),Label=b.Title});b.NoteLinks.Add(new(){Key=NotesModel.Key(a),Label=a.Title});store.Data.Items.AddRange(new[]{a,b});
            var bridge=new ObsidianBridge();Assert(bridge.Export(store,"default").Written==2);
            string vault=ObsidianBridge.Vault(store), manifestPath=Path.Combine(store.Root,"plugins","obsidian","manifest.json");
            var files=Directory.GetFiles(vault,"*.md",SearchOption.AllDirectories);Assert(files.Any(f=>Path.GetFileName(f)=="救援训练.md") && files.Any(f=>Path.GetFileName(f)=="救援训练（2）.md"));
            var manifest=JsonSerializer.Deserialize<ExchangeManifest>(File.ReadAllText(manifestPath))!;
            string old=manifest.Notes[a.Id].RelativePath, legacy=old[..old.LastIndexOf('/')]+"/救援训练-"+ObsidianBridge.Hash(a.Id)[..16]+".md";
            File.Move(Path.Combine(vault,old),Path.Combine(vault,legacy));manifest.Notes[a.Id].RelativePath=legacy;File.WriteAllText(manifestPath,JsonSerializer.Serialize(manifest));
            Assert(bridge.Export(store,"default").Errors.Count==0 && !File.Exists(Path.Combine(vault,legacy)));
            Assert(File.ReadAllText(Path.Combine(vault,manifest.Notes[b.Id].RelativePath)).Contains("/救援训练|"));
            Assert(Directory.GetFiles(vault,"*.md",SearchOption.AllDirectories).Length==2);
        });
        test("Obsidian selected library, duplicate titles, reciprocal links and text-only files", () => {
            var store = new Store(Path.Combine(temp, "obsidian")); store.Data.NoteLibraries.Add(new() { Id="other", Name="其他" });
            var a = new Item { Kind="笔记", Title="同名", NoteFormat=MarkdownNotes.Format, NoteBody="公式 $x^2$\n正文" };
            var b = new Item { Kind="笔记", Title="同名", Note="同库" };
            a.NoteLinks.Add(new() { Key=NotesModel.Key(b), Label=b.Title }); b.NoteLinks.Add(new() { Key=NotesModel.Key(a), Label=a.Title });
            store.Data.Items.AddRange(new[]{a,b,new Item { Kind="笔记", LibraryId="other", Title="不导出" }});
            string attachment=Path.Combine(temp,"proof.txt"); File.WriteAllText(attachment,"attachment"); a.Attachments.Add(store.AddFile(attachment));
            File.WriteAllText(Path.Combine(store.Root,"school-credentials.dpapi"),"SECRET_DO_NOT_EXPORT");
            var bridge=new ObsidianBridge(); var result=bridge.Export(store,"default"); Assert(result.Written==2 && result.Errors.Count==0);
            string vault=ObsidianBridge.Vault(store); var files=Directory.GetFiles(vault,"*.md",SearchOption.AllDirectories); Assert(files.Length==2);
            Assert(files.Any(f=>File.ReadAllText(f).Contains("$x^2$")) && files.All(f=>File.ReadAllText(f).Contains("[[library-")));
            Assert(!Directory.GetFiles(vault,"*",SearchOption.AllDirectories).Any(f=>File.ReadAllText(f).Contains("SECRET_DO_NOT_EXPORT")));
            Assert(bridge.Export(store,"default").Unchanged==2);
            var manifest=JsonSerializer.Deserialize<ExchangeManifest>(File.ReadAllText(Path.Combine(store.Root,"plugins","obsidian","manifest.json")))!;
            string path=Path.Combine(vault,manifest.Notes[a.Id].RelativePath); File.AppendAllText(path,"Obsidian edit"); a.NoteBody="new from Shike";
            Assert(bridge.Export(store,"default").Conflicts.Count==1 && File.ReadAllText(path).EndsWith("Obsidian edit"));
            b.Title="重命名"; result=bridge.Export(store,"default"); Assert(result.Written==1 && Directory.GetFiles(vault,"*.md",SearchOption.AllDirectories).Length==2);
        });
        test("Obsidian rich text and LaTeX export without previews or extra metadata", () => {
            var store=new Store(Path.Combine(temp,"obsidian-rich"));
            var doc=new FlowDocument(new Paragraph(new Run("测试正文")));
            var bitmap=BitmapSource.Create(1,1,96,96,PixelFormats.Bgra32,null,new byte[]{0,0,0,255},4);
            var image=new Image { Source=bitmap, Width=30, Height=20, Tag=new NoteEmbed { Kind="math", Source=@"\frac{a}{b}", Index=0 } };
            doc.Blocks.Add(new Paragraph(new InlineUIContainer(image)));
            using var ink=new MemoryStream(); new StrokeCollection(new[]{ new Stroke(new StylusPointCollection(new[]{ new StylusPoint(10,10),new StylusPoint(120,30) })) }).Save(ink);
            var note=new Item { Kind="笔记", Title="富文本", NoteFormat="rtf-v2", NoteBody=new RichNoteCodec().Encode(doc), NoteInk=Convert.ToBase64String(ink.ToArray()) }; store.Data.Items.Add(note);
            var result=new ObsidianBridge().Export(store,"default"); if(result.Errors.Count>0) throw new Exception(string.Join(";",result.Errors)); Assert(result.Written==1);
            string vault=ObsidianBridge.Vault(store), text=File.ReadAllText(Directory.GetFiles(vault,"*.md",SearchOption.AllDirectories).Single());
            Assert(text.Contains("$$\n\\frac{a}{b}\n$$") && text.Contains("测试正文") && !text.Contains("预览") && !text.Contains("shike_") && !text.Contains("# 富文本"));
            Assert(Directory.GetFiles(vault,"*",SearchOption.AllDirectories).All(f => f.EndsWith(".md"))); Assert(note.NoteInk.Length > 0);
        });
        test("Legacy preview exports refresh even when source is unchanged", () => {
            var store=new Store(Path.Combine(temp,"obsidian-upgrade")); var note=new Item { Kind="笔记",Title="old",Note="正文" }; store.Data.Items.Add(note);
            var bridge=new ObsidianBridge(); bridge.Export(store,"default");
            string current=Path.Combine(store.Root,"plugins","obsidian","manifest.json"), vault=ObsidianBridge.Vault(store);
            var manifest=JsonSerializer.Deserialize<ExchangeManifest>(File.ReadAllText(current))!; var record=manifest.Notes[note.Id];
            string path=Path.Combine(vault,record.RelativePath); File.WriteAllText(path,"old preview content"); record.ExportHash=ObsidianBridge.Hash("old preview content"); record.SourceHash=ObsidianBridge.Hash(JsonSerializer.Serialize(note,new JsonSerializerOptions { WriteIndented=true }));
            Directory.CreateDirectory(Path.Combine(vault,".shike")); File.WriteAllText(Path.Combine(vault,".shike","manifest.json"),JsonSerializer.Serialize(manifest)); File.Delete(current);
            Assert(bridge.Export(store,"default").Written==1 && File.ReadAllText(path)=="正文\n");
            Assert(bridge.Export(store,"default").Unchanged==1);
        });
        test("Obsidian text-only export does not read or copy attachments", () => {
            var store=new Store(Path.Combine(temp,"obsidian-missing")); var note=new Item { Kind="笔记",Title="missing" }; note.Attachments.Add(new(){File="missing.pdf",Name="missing"});store.Data.Items.Add(note);
            var result=new ObsidianBridge().Export(store,"default"); Assert(result.Errors.Count==0 && result.Written==1 && Directory.GetFiles(ObsidianBridge.Vault(store),"*",SearchOption.AllDirectories).Length==1);
        });
    }
}
