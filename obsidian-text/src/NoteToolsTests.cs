using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Shike;
public sealed partial class MainWindow
{
    void RunNoteToolsUiTests(string dir, Action<bool, string> check, Action<Action, Action<Window>> modal, Action<Window, string> click)
    {
        modal(() => EditKnowledgeNote(null), window =>
        {
            Visuals<TextBox>(window).First(t => t.MaxLength == 300).Text = "课堂推导与手写图解";
            var rich = Visuals<RichTextBox>(window).Single(); rich.Document.Blocks.Clear(); rich.Document.Blocks.Add(new Paragraph(new Run("用公式表达规律，用手写记录推导。"))); rich.CaretPosition = rich.Document.ContentEnd;
            void Nested(Action invoke, Action<Window> operate)
            {
                Exception? failure = null;
                Dispatcher.BeginInvoke(new Action(() => { var tool = window.OwnedWindows.Cast<Window>().Single(); try { tool.UpdateLayout(); operate(tool); } catch (Exception ex) { failure = ex; tool.Close(); } }), DispatcherPriority.ApplicationIdle);
                invoke(); if (failure != null) throw failure;
            }
            Nested(() => click(window, "排版"), tool => { var boxes = Visuals<ComboBox>(tool).ToList(); boxes[0].SelectedItem = "18"; boxes[1].SelectedItem = "1.75"; boxes[2].SelectedItem = "16"; boxes[3].SelectedItem = "36"; boxes[4].SelectedItem = "580"; click(tool, "应用排版"); });
            check(NoteTypography.Get(rich.Document).LineSpacing == 1.75 && rich.Document.PagePadding.Left == 36, "Typography controls apply line spacing and page margins");
            Nested(() => EditFormulaLegacy(window, rich), tool => { Visuals<TextBox>(tool).Single().Text = @"x=\frac{-b\pm\sqrt{b^{2}-4ac}}{2a}"; tool.UpdateLayout(); Theme.Screenshot((FrameworkElement)tool.Content, Path.Combine(dir, "公式输入.png")); Visuals<TextBox>(tool).Single().Text = @"\begin{pmatrix}a & b \\ c & d\end{pmatrix}"; tool.UpdateLayout(); Theme.Screenshot((FrameworkElement)tool.Content, Path.Combine(dir, "LaTeX矩阵.png")); click(tool, "插入公式"); });
            check(RichNoteCodec.Images(rich.Document).Any(i => i.Tag is NoteEmbed e && e.Kind == "math"), "Formula inserted with editable local source");
            rich.Document.Blocks.Add(new Paragraph(new Run("函数变化趋势与几何解释："))); rich.CaretPosition = rich.Document.ContentEnd;
            Nested(() => EditHandwriting(window, rich), tool =>
            {
                var ink = Visuals<InkCanvas>(tool).Single();
                ink.Strokes.Add(new Stroke(new StylusPointCollection(new[] { new StylusPoint(100, 390), new StylusPoint(100, 80) })));
                ink.Strokes.Add(new Stroke(new StylusPointCollection(new[] { new StylusPoint(100, 390), new StylusPoint(760, 390) })));
                ink.Strokes.Add(new Stroke(new StylusPointCollection(Enumerable.Range(0, 80).Select(n => new StylusPoint(120 + n * 7, 350 - Math.Pow(n / 7.0, 2) * 1.8)))));
                tool.UpdateLayout(); Theme.Screenshot((FrameworkElement)tool.Content, Path.Combine(dir, "手写板.png")); click(tool, "插入手写");
            });
            check(RichNoteCodec.Images(rich.Document).Count() == 2, "Formula and handwriting coexist in note document");
            window.UpdateLayout(); Theme.Screenshot((FrameworkElement)window.Content, Path.Combine(dir, "公式与手写笔记.png")); click(window, "保存");
        });
        var note = store.Data.Items.Single(i => i.Title == "课堂推导与手写图解");
        modal(() => EditKnowledgeNote(note), window =>
        {
            var rich = Visuals<RichTextBox>(window).Single(); var images = RichNoteCodec.Images(rich.Document).ToList();
            check(images.Count == 2 && images.All(i => i.Tag is NoteEmbed), "Both image sources restored after reopening saved note");
            var inkSource = images.Select(i => (NoteEmbed)i.Tag).Single(e => e.Kind == "ink"); using var stream = new MemoryStream(Convert.FromBase64String(inkSource.Source)); check(new StrokeCollection(stream).Count == 3, "Original handwriting strokes persisted, not only bitmap");
            check(NoteTypography.Get(rich.Document).ParagraphSpacing == 16 && NoteTypography.Get(rich.Document).ColumnWidth == 580, "Paragraph spacing and column width persist across reopening");
            var formula = images.Single(i => ((NoteEmbed)i.Tag).Kind == "math");
            Dispatcher.BeginInvoke(new Action(() => { var tool = window.OwnedWindows.Cast<Window>().Single(); tool.UpdateLayout(); Visuals<TextBox>(tool).Single().Text = @"\int_{0}^{1}x^{2}dx=\frac{1}{3}"; click(tool, "更新公式"); }), DispatcherPriority.ApplicationIdle);
            EditFormulaLegacy(window, rich, formula); check(RichNoteCodec.Images(rich.Document).Count() == 2 && ((NoteEmbed)formula.Tag).Source.Contains("int"), "Updating formula replaces image without adding a duplicate"); click(window, "保存");
        });
        noteLibraryId = store.Data.NoteLibraries.Single(l => l.Name == "学习与探索").Id; noteGraphView = false; mindRootKey = null; SwitchView("笔记与附件"); UpdateLayout();
        check(Visuals<System.Windows.Shapes.Path>(body).Any(), "Mind map renders curved branches from existing associations");
        Theme.Screenshot((FrameworkElement)Content, Path.Combine(dir, "思维导图.png"));
        var toggle = Visuals<Button>(body).First(b => b.ToolTip is string s && s.StartsWith("展开/折叠")); toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); UpdateLayout();
        check(mindCollapsed.Count > 0, "Mind map branch can be collapsed");
        click(this, "全部展开"); check(mindCollapsed.Count == 0, "Mind map branches can be expanded again");
        noteGraphView = true; noteLibraryId = "default";
    }
}
public static class NoteToolsTests
{
    public static void Run(Action<string, Action> test)
    {
        test("Formula renderer handles nested fractions, scripts and radicals locally", () => { var bitmap = MainWindow.Rasterize(MathFormula.Render(@"x=\frac{-b\pm\sqrt{b^{2}-4ac}}{2a}")); if (bitmap.PixelWidth < 50 || bitmap.PixelHeight < 30) throw new Exception("Formula not rendered"); });
        test("Unsupported or malformed formula syntax rejected", () => { foreach (var source in new[] { @"\unknown{x}", @"\frac{a}", "x^", "x}" }) { try { MathFormula.Render(source); throw new Exception("Invalid formula accepted"); } catch (FormatException) { } } });
        test("LaTeX matrices and aligned equations render with delimiters", () => {
            foreach (string source in new[] { @"\begin{pmatrix}a & b \\ c & d\end{pmatrix}", @"\begin{align}x+1 &= 2 \\ x &= 1\end{align}", @"$$\left(\frac{a}{b}\right)^2$$", @"\[\sum_{i=1}^{n}i=\frac{n(n+1)}{2}\]" }) {
                var bitmap = MainWindow.Rasterize(MathFormula.Render(source)); if (bitmap.PixelWidth < 20) throw new Exception("Missing LaTeX output");
            }
        });
        test("Legacy rich text still opens under versioned codec", () => {
            var doc = new FlowDocument(new Paragraph(new Run("旧版正文"))); using var stream = new MemoryStream(); new TextRange(doc.ContentStart, doc.ContentEnd).Save(stream, DataFormats.Rtf); var codec = new RichNoteCodec(); if (!codec.PlainText(codec.Decode(Convert.ToBase64String(stream.ToArray()), "")).Contains("旧版正文")) throw new Exception("Legacy note lost");
        });
        test("Formula metadata survives full codec roundtrip", () => {
            var editor = new RichTextBox(); MainWindow.InsertNoteImage(editor, MainWindow.Rasterize(MathFormula.Render(@"\frac{a}{b}")), new() { Kind = "math", Source = @"\frac{a}{b}" }); var codec = new RichNoteCodec(); var restored = codec.Decode(codec.Encode(editor.Document), ""); if (RichNoteCodec.Images(restored).Single().Tag is not NoteEmbed e || e.Source != @"\frac{a}{b}") throw new Exception("Formula source lost");
        });
    }
}

