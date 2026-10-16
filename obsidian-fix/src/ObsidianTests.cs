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
        test("Obsidian selected library, duplicate titles, links, attachments and no credentials", () => {
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
            var manifest=JsonSerializer.Deserialize<ExchangeManifest>(File.ReadAllText(Path.Combine(vault,".shike","manifest.json")))!;
            string path=Path.Combine(vault,manifest.Notes[a.Id].RelativePath); File.AppendAllText(path,"Obsidian edit"); a.NoteBody="new from Shike";
            Assert(bridge.Export(store,"default").Conflicts.Count==1 && File.ReadAllText(path).EndsWith("Obsidian edit"));
            b.Title="重命名"; result=bridge.Export(store,"default"); Assert(result.Written==1 && Directory.GetFiles(vault,"*.md",SearchOption.AllDirectories).Length==2);
        });
        test("Obsidian rich formula and overlay ink keep original source and PNG preview", () => {
            var store=new Store(Path.Combine(temp,"obsidian-rich"));
            var doc=new FlowDocument(new Paragraph(new Run("测试正文")));
            var bitmap=BitmapSource.Create(1,1,96,96,PixelFormats.Bgra32,null,new byte[]{0,0,0,255},4);
            var image=new Image { Source=bitmap, Width=30, Height=20, Tag=new NoteEmbed { Kind="math", Source=@"\frac{a}{b}", Index=0 } };
            doc.Blocks.Add(new Paragraph(new InlineUIContainer(image)));
            using var ink=new MemoryStream(); new StrokeCollection(new[]{ new Stroke(new StylusPointCollection(new[]{ new StylusPoint(10,10),new StylusPoint(120,30) })) }).Save(ink);
            var note=new Item { Kind="笔记", Title="富文本", NoteFormat="rtf-v2", NoteBody=new RichNoteCodec().Encode(doc), NoteInk=Convert.ToBase64String(ink.ToArray()) }; store.Data.Items.Add(note);
            var result=new ObsidianBridge().Export(store,"default"); if(result.Errors.Count>0) throw new Exception(string.Join(";",result.Errors)); Assert(result.Written==1);
            string vault=ObsidianBridge.Vault(store), text=File.ReadAllText(Directory.GetFiles(vault,"*.md",SearchOption.AllDirectories).Single());
            Assert(text.Contains("$$\n\\frac{a}{b}\n$$") && text.Contains("原始排版与手写预览")); Assert(Directory.GetFiles(Path.Combine(vault,"assets"),"*.png").Length>0);
            Assert(File.ReadAllText(Directory.GetFiles(Path.Combine(vault,".shike","originals")).Single()).Contains(note.NoteInk));
        });
        test("Obsidian missing attachment reports failure without exporting partial note", () => {
            var store=new Store(Path.Combine(temp,"obsidian-missing")); var note=new Item { Kind="笔记",Title="missing" }; note.Attachments.Add(new(){File="missing.pdf",Name="missing"});store.Data.Items.Add(note);
            var result=new ObsidianBridge().Export(store,"default"); Assert(result.Errors.Count==1 && result.Written==0 && Directory.GetFiles(ObsidianBridge.Vault(store),"*.md",SearchOption.AllDirectories).Length==0);
        });
    }
}
