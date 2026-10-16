using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;

namespace Shike;
public sealed partial class MainWindow
{
    void RunNoteFreedomUiTests(string dir, Action<bool,string> check, Action<Action,Action<Window>> modal, Action<Window,string> click)
    {
        modal(() => EditKnowledgeNote(null), window =>
        {
            Visuals<TextBox>(window).First(t => t.MaxLength == 300).Text = "自由排版与纸面批注";
            var editor = Visuals<RichTextBox>(window).Single();
            editor.Document.Blocks.Clear(); var paragraph = new Paragraph(new Run("在原文上标注：保留文字与每一条笔迹。")); editor.Document.Blocks.Add(paragraph);
            editor.Selection.Select(paragraph.ContentStart, paragraph.ContentEnd);
            var font = Visuals<ComboBox>(window).First(c => c.ToolTip is string s && s.StartsWith("字体：")); font.SelectedItem = "Georgia";
            var size = Visuals<ComboBox>(window).First(c => (c.ToolTip as string) == "字号"); size.SelectedItem = "24";
            var red = Visuals<Button>(window).First(b => (b.ToolTip as string) == "文字颜色 · 红色"); red.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check(editor.Selection.GetPropertyValue(TextElement.FontSizeProperty) is double fs && fs == 24 && ((SolidColorBrush)editor.Selection.GetPropertyValue(TextElement.ForegroundProperty)).Color == Colors.Firebrick, "Text selection accepts font size and color");
            editor.CaretPosition = editor.Document.ContentEnd;
            InsertNoteImage(editor, Rasterize(MathFormula.Render(@"\int_0^1 x^2 dx=\frac{1}{3}")), new() { Kind = "math", Source = @"\int_0^1 x^2 dx=\frac{1}{3}" });
            var formula = RichNoteCodec.Images(editor.Document).Single();
            check(NoteFormatting.ParagraphOf(formula)?.TextAlignment == TextAlignment.Center && new RichNoteCodec().PlainText(editor.Document).Contains("保留文字"), "Inserted formula has its own centered line without losing text");
            window.UpdateLayout(); editor.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent, Source = formula }); window.UpdateLayout();
            var formulaSize = Visuals<ComboBox>(window).First(c => (c.ToolTip as string) == "公式字号"); double previous = formula.Width; formulaSize.SelectedItem = "40";
            check(formula.Width > previous && ((NoteEmbed)formula.Tag).FontSize == 40, "Clicking formula exposes working size controls");
            click(window,"公式靠右"); check(NoteFormatting.ParagraphOf(formula)?.TextAlignment == TextAlignment.Right, "Formula alignment can be changed"); click(window,"公式居中");
            var first = editor.Document.Blocks.FirstBlock; click(window,"上移一段"); check(editor.Document.Blocks.FirstBlock != first && NoteFormatting.ParagraphOf(formula) == editor.Document.Blocks.FirstBlock, "Formula can move above existing text"); click(window,"下移一段");
            click(window,"手写"); var paper = Visuals<NoteCanvas>(window).Single();
            check(paper.Ink.IsHitTestVisible && paper.Ink.EditingMode == InkCanvasEditingMode.Ink && paper.Ink.Parent == editor.Parent, "Pen draws directly on the same paper as text");
            var stroke = new Stroke(new StylusPointCollection(new[] { new StylusPoint(45,65), new StylusPoint(210,65), new StylusPoint(330,80) })) { DrawingAttributes = new DrawingAttributes { Color = Colors.RoyalBlue, Width = 4, Height = 4 } }; paper.Ink.Strokes.Add(stroke);
            paper.Ink.Strokes.Add(new Stroke(new StylusPointCollection(new[] { new StylusPoint(60,1150), new StylusPoint(220,1150) })));
            check(RichNoteCodec.Images(editor.Document).Count() == 1 && paper.Ink.Strokes.Count == 2, "Handwriting is editable strokes, not embedded images");
            click(window,"橡皮"); check(paper.Ink.EditingMode == InkCanvasEditingMode.EraseByPoint, "Point eraser affects vector ink only");
            paper.Ink.Strokes.RemoveAt(1); click(window,"撤销笔迹"); check(paper.Ink.Strokes.Count == 2, "Ink erase can be undone"); click(window,"重做笔迹"); check(paper.Ink.Strokes.Count == 1, "Ink erase can be redone");
            paper.Ink.Strokes.Add(new Stroke(new StylusPointCollection(new[] { new StylusPoint(70,1100), new StylusPoint(220,1100) })));
            window.UpdateLayout(); paper.Scroll.ScrollToVerticalOffset(300); window.UpdateLayout();
            check(paper.Scroll.VerticalOffset > 0 && paper.Paper.ActualHeight > 1100, "Text and handwriting share a scrollable expandable paper"); paper.Scroll.ScrollToTop();
            click(window,"文字编辑"); check(!paper.Ink.IsHitTestVisible && !editor.IsReadOnly, "Text editing resumes without deleting overlay strokes");
            window.UpdateLayout(); Theme.Screenshot((FrameworkElement)window.Content, Path.Combine(dir,"自由排版与覆盖手写.png")); click(window,"保存");
        });
        var saved = store.Data.Items.Single(i => i.Title == "自由排版与纸面批注");
        check(saved.NoteInk.Length > 0, "Vector ink is stored separately from the note body");
        modal(() => EditKnowledgeNote(saved), window =>
        {
            var paper = Visuals<NoteCanvas>(window).Single(); var editor = Visuals<RichTextBox>(window).Single(); var formula = RichNoteCodec.Images(editor.Document).Single();
            check(paper.Ink.Strokes.Count == 2 && paper.Ink.Strokes[0].DrawingAttributes.Color == Colors.RoyalBlue && Math.Abs(paper.Ink.Strokes[1].StylusPoints[0].Y - 1100) < 0.1, "Reopen preserves editable ink colors and paper coordinates");
            check(((NoteEmbed)formula.Tag).FontSize == 40 && Math.Abs(formula.Width - ((NoteEmbed)formula.Tag).NaturalWidth * 40 / 24) < 0.01 && NoteFormatting.ParagraphOf(formula)?.TextAlignment == TextAlignment.Center, "Formula size and centered alignment persist on reopen");
            var text = NoteTypography.Paragraphs(editor.Document).First(p => new TextRange(p.ContentStart,p.ContentEnd).Text.Contains("在原文")); var range = new TextRange(text.ContentStart,text.ContentEnd);
            check(range.GetPropertyValue(TextElement.FontSizeProperty) is double fs && fs == 24 && ((SolidColorBrush)range.GetPropertyValue(TextElement.ForegroundProperty)).Color == Colors.Firebrick, "Text color and font size persist on reopen");
            check(range.GetPropertyValue(TextElement.FontFamilyProperty) is FontFamily family && family.Source == "Georgia", "Selected font persists on reopen");
            click(window,"取消");
        });
        string backup = Path.Combine(dir,"vector-ink-backup.zip"); store.Backup(backup); store.Restore(backup);
        check(store.Data.Items.Single(i => i.Id == saved.Id).NoteInk == saved.NoteInk, "Backup restores vector ink exactly");
    }
}

public static class AcademicLogoutTests
{
    public static void Run(Action<string,Action> test)
    {
        test("Logout purge removes academics from active data and automatic history, retaining personal attachments", () =>
        {
            var root = Path.Combine(Path.GetTempPath(),"Shike-logout-"+Guid.NewGuid().ToString("N")); var store = new Store(root);
            string shared = "shared.txt", exclusive = "course.txt"; File.WriteAllText(Path.Combine(store.Files,shared),"shared"); File.WriteAllText(Path.Combine(store.Files,exclusive),"course");
            var course = new Item { Kind = "课程", Title = "DELETE_COURSE", Attachments = new() { new() { Name=shared, File=shared }, new() { Name=exclusive, File=exclusive } } };
            var exam = new Item { Kind="考试",Title="DELETE_EXAM", Cancelled=true };
            var note = new Item { Kind="笔记",Title="KEEP_NOTE", Note="keep text", NoteInk="kept opaque data", Attachments=new(){new(){Name=shared,File=shared}}, ParentId=course.Id, NoteLinks=new(){new(){Key=NotesModel.Key(course),Kind="course",Label=course.Title},new(){Key=NotesModel.Key(exam),Kind="item",Label=exam.Title}} };
            var task = new Item { Kind="待办", Title="KEEP_TASK", AssistantKey="courses:test:1" };
            store.Data.Items.AddRange(new[]{course,exam,note,task}); store.Data.Settings.LastSync=DateTime.Now; store.Data.Fired.Add(course.Id+":x"); store.Data.Fired.Add(task.Id+":y");
            store.Data.NoteLibraries[0].Positions[NotesModel.Key(course)] = new(); store.Data.Alerts.Add("10-13 09:00  ·  DELETE_COURSE  ·  安排于 10-14 10:00  room"); store.Data.Alerts.Add("keep alert");
            var archive=Path.Combine(root,"笔记系统升级前-test.zip"); store.Backup(archive); AcademicLogout.Purge(store);
            foreach (var path in new[]{Path.Combine(root,"data.json"),Path.Combine(root,"data.json.bak")}) { var raw=File.ReadAllText(path); var data=JsonSerializer.Deserialize<AppData>(raw)!; if(data.Items.Count!=2 || data.Items.Any(i=>i.Kind is "课程" or "考试") || data.Items.Single(i=>i.Kind=="笔记").NoteLinks.Count!=0 || data.Settings.LastSync!=null || data.Fired.Count!=1 || data.Alerts.Count!=1) throw new Exception("Academic data or references remain"); }
            using var zip=ZipFile.OpenRead(archive); using var reader=new StreamReader(zip.GetEntry("data.json")!.Open()); var archived=JsonSerializer.Deserialize<AppData>(reader.ReadToEnd())!;
            if(archived.Items.Count!=2 || zip.GetEntry("attachments/"+exclusive)!=null || zip.GetEntry("attachments/"+shared)==null || File.Exists(Path.Combine(store.Files,exclusive)) || !File.Exists(Path.Combine(store.Files,shared))) throw new Exception("Attachments or history purge incorrect");
            if(store.Data.Items.Single(i=>i.Kind=="笔记").NoteInk!=note.NoteInk || store.Data.Items.Single(i=>i.Kind=="笔记").Note!=note.Note) throw new Exception("Personal content changed");
        });
    }
}




