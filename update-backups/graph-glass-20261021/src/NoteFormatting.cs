using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using static Shike.Theme;

namespace Shike;
public static class NoteFormatting
{
    public static FrameworkElement TextTools(RichTextBox editor)
    {
        var panel = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        var installed = Fonts.SystemFontFamilies.Select(f => f.Source).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var families = new[] { "Microsoft YaHei UI", "Microsoft YaHei", "SimSun", "KaiTi", "SimHei", "FangSong", "DengXian", "Arial", "Calibri", "Cambria", "Georgia", "Times New Roman", "Consolas" }.Where(installed.Contains).ToArray();
        var family = Combo(families, families.FirstOrDefault() ?? "Microsoft YaHei UI"); family.Width = 170; family.ToolTip = "字体：选中文字后设置；未选择时用于接下来输入的文字";
        var sizes = Combo(new[] { "10", "12", "14", "16", "18", "20", "24", "28", "32", "36", "48", "60", "72" }, "16"); sizes.Width = 72; sizes.ToolTip = "字号";
        family.SelectionChanged += (_, _) => { editor.Selection.ApplyPropertyValue(TextElement.FontFamilyProperty, new FontFamily((string)family.SelectedItem)); editor.Focus(); };
        sizes.SelectionChanged += (_, _) => { editor.Selection.ApplyPropertyValue(TextElement.FontSizeProperty, double.Parse((string)sizes.SelectedItem)); editor.Focus(); };
        panel.Children.Add(family); panel.Children.Add(sizes);
        foreach (var (name, color) in NotePalette.Colors)
        {
            var b = Button("A", () => { editor.Selection.ApplyPropertyValue(TextElement.ForegroundProperty, new SolidColorBrush(color)); editor.Focus(); }); b.Focusable = false; b.Foreground = new SolidColorBrush(color); b.ToolTip = "文字颜色 · " + name; b.Padding = new Thickness(7); b.Margin = new Thickness(0, 0, 2, 0); panel.Children.Add(b);
        }
        foreach (var (name, align) in new[] { ("左对齐", TextAlignment.Left), ("居中", TextAlignment.Center), ("右对齐", TextAlignment.Right), ("两端对齐", TextAlignment.Justify) })
        { var b = Button(name, () => { editor.Selection.ApplyPropertyValue(Paragraph.TextAlignmentProperty, align); editor.Focus(); }); b.Focusable = false; b.Padding = new Thickness(7); panel.Children.Add(b); }
        return panel;
    }
    public static Paragraph? ParagraphOf(Image image) => (image.Parent as InlineUIContainer)?.ContentStart.Paragraph;
    public static void SetFormulaSize(Image image, double size)
    {
        if (image.Tag is not NoteEmbed tag || tag.Kind != "math") return;
        if (tag.NaturalWidth <= 0) tag.NaturalWidth = double.IsFinite(image.Width) ? image.Width : Math.Max(1, image.Source?.Width ?? 100);
        tag.FontSize = Math.Clamp(size, 10, 96); image.Width = tag.NaturalWidth * tag.FontSize / 24; image.Height = double.NaN; FloatingFormula.Update(image);
    }
    public static void InsertFormulaAtCaret(RichTextBox editor, Image image)
    {
        // Split at the caret so surrounding text is retained on either side of a centered formula paragraph.
        var pointer = editor.CaretPosition;
        if (pointer.Paragraph == null) { var p = new Paragraph(); editor.Document.Blocks.Add(p); pointer = p.ContentStart; }
        var middle = pointer.InsertParagraphBreak(); var paragraph = middle.Paragraph!;
        var after = middle.InsertParagraphBreak(); paragraph.TextAlignment = TextAlignment.Center; paragraph.LineHeight = double.NaN;
        paragraph.Inlines.Add(new InlineUIContainer(image)); editor.CaretPosition = after;
    }
    public static void MoveFormula(RichTextBox editor, Image image, bool up)
    {
        if (FloatingFormula.FigureOf(image) is Figure floating && image.Tag is NoteEmbed tag)
        {
            var anchor = floating.ElementStart.Paragraph; var target = up ? anchor?.PreviousBlock : anchor?.NextBlock;
            FloatingFormula.Wrap(image, tag.X, target is Paragraph ? 0 : Math.Max(0,tag.Y + (up ? -24 : 24)), target as Paragraph); return;
        }
        var paragraph = ParagraphOf(image); if (paragraph == null) return;
        var neighbor = up ? paragraph.PreviousBlock : paragraph.NextBlock; if (neighbor == null) return;
        BlockCollection? blocks = paragraph.Parent switch { FlowDocument doc => doc.Blocks, Section s => s.Blocks, ListItem li => li.Blocks, TableCell tc => tc.Blocks, _ => null };
        if (blocks == null) return;
        if (new TextRange(paragraph.ContentStart, paragraph.ContentEnd).Text.Trim().Length == 0 && RichNoteCodec.Images(paragraph).Count() == 1)
        {
            blocks.Remove(paragraph); if (up) blocks.InsertBefore(neighbor, paragraph); else blocks.InsertAfter(neighbor, paragraph);
        }
        else
        {
            var container = (InlineUIContainer)image.Parent; paragraph.Inlines.Remove(container); container.Child = null; var p = new Paragraph(new InlineUIContainer(image)) { TextAlignment = TextAlignment.Center };
            if (up) blocks.InsertBefore(neighbor, p); else blocks.InsertAfter(neighbor, p);
        }
        editor.Focus();
    }
    public static FrameworkElement FormulaTools(RichTextBox editor, Action changed)
    {
        Image? selected = null; bool filling = false;
        var panel = new WrapPanel { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 8) };
        panel.Children.Add(Text("公式排版", 12, bold: true));
        var size = Combo(new[] { "12", "16", "20", "24", "28", "32", "40", "48", "60", "72", "96" }, "24"); size.ToolTip = "公式字号"; size.Width = 80; panel.Children.Add(size);
        bool Present() => selected != null && RichNoteCodec.Images(editor.Document).Contains(selected);
        size.SelectionChanged += (_, _) => { if (!filling && Present() && size.SelectedItem != null) { SetFormulaSize(selected!, double.Parse((string)size.SelectedItem)); changed(); } };
        void Tool(string label, Action<Image> action) { var b = Button(label, () => { if (Present()) { action(selected!); changed(); } }); b.Focusable = false; b.Padding = new Thickness(8); panel.Children.Add(b); }
        foreach (var (name, align) in new[] { ("公式靠左", TextAlignment.Left), ("公式居中", TextAlignment.Center), ("公式靠右", TextAlignment.Right) }) Tool(name, image => {
            if (FloatingFormula.FigureOf(image) != null && image.Tag is NoteEmbed tag) { double room = Math.Max(0,editor.ActualWidth-editor.Padding.Left-editor.Padding.Right-image.Width-20); FloatingFormula.Wrap(image,align==TextAlignment.Left?0:align==TextAlignment.Center?room/2:room,tag.Y); }
            else if (ParagraphOf(image) is Paragraph p) p.TextAlignment = align;
        });
        Tool("上移一段", image => MoveFormula(editor, image, true)); Tool("下移一段", image => MoveFormula(editor, image, false));
        Tool("环绕·较宽侧", image => FloatingFormula.Wrap(image, 100));
        Tool("嵌入正文", FloatingFormula.Inline);

        var close = Button("收起", () => panel.Visibility = Visibility.Collapsed); close.Padding = new Thickness(8); panel.Children.Add(close);
        editor.PreviewMouseLeftButtonDown += (_, e) =>
        {
            DependencyObject? target = e.OriginalSource as DependencyObject;
            while (target != null && target is not Image) target = target is Visual ? VisualTreeHelper.GetParent(target) : null;
            if (target is Image image && image.Tag is NoteEmbed tag && tag.Kind == "math")
            { selected = image; filling = true; size.SelectedItem = tag.FontSize.ToString("0"); filling = false; panel.Visibility = Visibility.Visible; }
        };
        FloatingFormula.Attach(editor, changed);
        return panel;
    }
}





