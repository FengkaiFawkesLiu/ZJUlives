using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
namespace Shike;
public sealed partial class MainWindow
{
    void RunMarkdownUiTests(string dir, Action<bool,string> check, Action<Action,Action<Window>> modal, Action<Window,string> click)
    {
        const string source = "# 线性代数\n\n**重点**与*推导*\n\n- [x] 阅读\n- [ ] 习题\n\n> 课堂记录\n\n| 概念 | 说明 |\n| --- | --- |\n| 矩阵 | 线性映射 |\n\n```python\nprint(1)\n```\n\n$$\n\\frac{a}{b}\n$$\n\n[官网](https://www.zju.edu.cn)\n";
        modal(() => EditKnowledgeNote(null, true), window =>
        {
            Visuals<TextBox>(window).First(t => t.MaxLength == 300).Text = "Markdown 集成测试";
            var control = Visuals<MarkdownEditor>(window).Single(); control.Source.Text = source; control.Refresh(); window.UpdateLayout();
            check(control.Preview.Document.Blocks.OfType<Table>().Any(), "Markdown table renders natively");
            check(control.Preview.Document.Blocks.OfType<BlockUIContainer>().Any(), "Markdown LaTeX renders offline");
            check(new TextRange(control.Preview.Document.ContentStart, control.Preview.Document.ContentEnd).Text.Contains("☑"), "Markdown checked tasks render");
            Theme.Screenshot((FrameworkElement)window.Content, Path.Combine(dir,"Markdown编辑预览.png")); click(window,"保存");
        });
        var note = store.Data.Items.Single(i => i.Title == "Markdown 集成测试");
        check(note.NoteFormat == MarkdownNotes.Format && note.NoteBody == source && note.Note.Contains("线性代数"), "Markdown source roundtrips exactly and plain search index is populated");
        var peer = store.Data.Items.First(i => i.Kind == "笔记" && i.LibraryId == note.LibraryId && i.Id != note.Id);
        note.NoteLinks.Add(new() { Key = NotesModel.Key(peer), Label = peer.Title, Kind = "note" }); NotesModel.SyncReciprocal(store.Data,note);
        var path = Path.Combine(dir,"markdown-attachment.txt"); File.WriteAllText(path,"local attachment"); note.Attachments.Add(store.AddFile(path)); store.Save();
        modal(() => EditKnowledgeNote(note), window =>
        {
            var control = Visuals<MarkdownEditor>(window).Single(); check(control.Source.Text == source, "Reopened Markdown selects source editor without conversion");
            control.Source.Select(2,4); click(window,"粗体"); check(control.Source.Text.StartsWith("# **线性代数**"), "Markdown toolbar wraps selected text"); click(window,"保存");
        });
        note = store.Data.Items.Single(i => i.Title == "Markdown 集成测试");
        check(note.Attachments.Count == 1 && note.NoteLinks.Any(l => l.Key == NotesModel.Key(peer)) && peer.NoteLinks.Any(l => l.Key == NotesModel.Key(note)), "Markdown edit preserves attachments and mutual graph links");
        var backup = Path.Combine(dir,"markdown-backup.zip"); store.Backup(backup); var expected = note.NoteBody; store.Restore(backup);
        check(store.Data.Items.Single(i => i.Id == note.Id).NoteBody == expected, "Backup restore accepts Markdown format without losing source");
        var unsafeDoc = MarkdownNotes.Render("<script>alert(1)</script>\n\n[bad](javascript:alert) ![remote](https://example.com/image.png)");
        check(!unsafeDoc.Blocks.OfType<Paragraph>().SelectMany(p => p.Inlines.Cast<Inline>()).OfType<Hyperlink>().Any(), "Markdown unsafe links and remote images cannot execute or fetch");
        var rich = store.Data.Items.First(i => i.Kind == "笔记" && i.NoteFormat == "rtf-v2"); var body = rich.NoteBody;
        modal(() => EditKnowledgeNote(rich), window => { check(Visuals<RichTextBox>(window).Any(), "Existing rich notes keep their original editor"); click(window,"取消"); });
        check(rich.NoteBody == body, "Opening rich note preserves original embedded data");
    }
}
