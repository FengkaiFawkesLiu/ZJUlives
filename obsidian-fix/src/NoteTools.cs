using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Ink;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using static Shike.Theme;

namespace Shike;
public sealed partial class MainWindow
{
    void EditTypography(Window owner, RichTextBox editor, Action changed)
    {
        var dialog = Dialog(owner, "笔记排版", 560, 630); var layout = NoteTypography.Get(editor.Document);
        var font = Combo(new[] { "12", "14", "16", "18", "20", "24" }, layout.FontSize.ToString("0"));
        var line = Combo(new[] { "1", "1.25", "1.5", "1.75", "2", "2.5" }, layout.LineSpacing.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var paragraph = Combo(new[] { "0", "6", "10", "16", "24", "32" }, layout.ParagraphSpacing.ToString("0"));
        var margin = Combo(new[] { "12", "24", "36", "48", "64" }, layout.PageMargin.ToString("0"));
        var width = Combo(new[] { "400", "580", "720", "900", "1200" }, layout.ColumnWidth.ToString("0"));
        double Value(ComboBox c) => double.Parse(c.SelectedItem!.ToString()!, System.Globalization.CultureInfo.InvariantCulture);
        dialog.Content = new Border { Padding = new Thickness(24), Child = Stack(Text("让阅读更舒适", 23, bold: true), Space(10), Text("应用于整篇笔记；局部标题与文字样式保留。正文宽度超出窗口时可横向滚动。", 12, Muted), Space(16), Field("默认字号（像素）", font), Field("行距倍数", line), Field("段落间距（像素）", paragraph), Field("四周留白（像素）", margin), Field("正文列宽（像素）", width), Row(Button("取消", () => dialog.Close()), Button("应用排版", () =>
        {
            NoteTypography.Apply(editor.Document, new() { FontSize = Value(font), LineSpacing = Value(line), ParagraphSpacing = Value(paragraph), PageMargin = Value(margin), ColumnWidth = Value(width) }, true); changed(); dialog.Close();
        }, true))) }; dialog.ShowDialog();
    }
    internal static BitmapSource Rasterize(FrameworkElement visual)
    {
        visual.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = visual.DesiredSize; if (size.Width < 1 || size.Height < 1 || size.Width > 6000 || size.Height > 3000) throw new InvalidOperationException("内容尺寸过大，请拆成多段插入。");
        visual.Arrange(new Rect(size)); visual.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(size.Width * 2), (int)Math.Ceiling(size.Height * 2), 192, 192, PixelFormats.Pbgra32); bitmap.Render(visual); bitmap.Freeze(); return bitmap;
    }
    internal static void InsertNoteImage(RichTextBox editor, BitmapSource bitmap, NoteEmbed data, Image? existing = null)
    {
        var image = existing ?? new Image();
        var previous = existing?.Tag as NoteEmbed;
        data.FontSize = previous?.FontSize ?? 24; data.Wrap = previous?.Wrap ?? false; data.X = previous?.X ?? 0; data.Y = previous?.Y ?? 0;
        data.NaturalWidth = Math.Min(540, bitmap.Width);
        image.Source = bitmap; image.Width = data.NaturalWidth; image.Stretch = Stretch.Uniform; image.Tag = data;
        image.ToolTip = data.Kind == "math" ? "单击调整公式字号与位置，双击编辑公式内容" : "双击编辑旧版手写";
        if (data.Kind == "math") NoteFormatting.SetFormulaSize(image, data.FontSize);
        if (existing == null) { if (data.Kind == "math") NoteFormatting.InsertFormulaAtCaret(editor, image); else new InlineUIContainer(image, editor.CaretPosition); }
        editor.RaiseEvent(new TextChangedEventArgs(TextBoxBaseTextChanged(), UndoAction.None));
        editor.Focus();
    }
    static RoutedEvent TextBoxBaseTextChanged() => System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent;
    void EditFormulaLegacy(Window owner, RichTextBox editor, Image? existing = null, string? sourceOverride = null)
    {
        var dialog = Dialog(owner, "数学公式", 820, 650); var source = new TextBox { Text = sourceOverride ?? (existing?.Tag as NoteEmbed)?.Source ?? @"\frac{a}{b}", AcceptsReturn = true, MinHeight = 90, TextWrapping = TextWrapping.Wrap, MaxLength = 1500 };
        var preview = new ContentControl { MinHeight = 110, HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center }; var error = Text("", 12, Brush("#AF653A"));
        var templates = new WrapPanel();
        foreach (var template in new[] { ("分数", @"\frac{a}{b}"), ("根号", @"\sqrt{x}"), ("上标", "x^{2}"), ("下标", "a_{n}"), ("求和", @"\sum_{i=1}^{n}i"), ("积分", @"\int_{a}^{b}f(x)\,dx"), ("极限", @"\lim_{x\to 0}\frac{\sin x}{x}"), ("矩阵", @"\begin{pmatrix}a & b \\ c & d\end{pmatrix}"), ("多行对齐", @"\begin{align}x+1 &= 2 \\ x &= 1\end{align}"), ("二次公式", @"x=\frac{-b\pm\sqrt{b^{2}-4ac}}{2a}") })
        { var b = Button(template.Item1, () => { source.SelectedText = template.Item2; source.Focus(); }); b.Padding = new Thickness(9, 6, 9, 6); b.Margin = new Thickness(0, 0, 6, 6); templates.Children.Add(b); }
        var symbols = new WrapPanel(); foreach (var symbol in new[] { "α", "β", "θ", "π", "∞", "≤", "≥", "≠", "≈", "±", "×", "∈", "∂", "∇", "→" }) { var b = Button(symbol, () => { source.SelectedText = symbol; source.Focus(); }); b.Padding = new Thickness(8, 5, 8, 5); symbols.Children.Add(b); }
        var insert = Button(existing == null ? "插入公式" : "更新公式", () =>
        {
            try { InsertNoteImage(editor, Rasterize(MathFormula.Render(source.Text)), new() { Kind = "math", Source = source.Text }, existing); dialog.Close(); } catch (Exception ex) { error.Text = ex.Message; }
        }, true);
        void Refresh() { try { preview.Content = MathFormula.Render(source.Text); error.Text = ""; insert.IsEnabled = true; } catch (Exception ex) { preview.Content = null; error.Text = ex.Message; insert.IsEnabled = false; } }
        source.TextChanged += (_, _) => Refresh(); Refresh();
        dialog.Content = new Border { Padding = new Thickness(24), Child = Stack(Text("数学公式", 24, bold: true), Space(8), Text("点击模板快速输入，替换花括号内的内容。使用 LaTeX 数学语法，本地离线排版；不支持文档导言区或额外宏包。", 12, Muted), Space(14), templates, symbols, Space(12), source, Space(12), new Border { Background = Brushes.White, CornerRadius = new CornerRadius(10), Padding = new Thickness(8), Child = new ScrollViewer { Content = preview, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 160 } }, error, Space(14), Row(Button("取消", () => dialog.Close()), insert)) };
        dialog.ShowDialog();
    }
    void EditHandwriting(Window owner, RichTextBox editor, Image? existing = null)
    {
        var dialog = Dialog(owner, "本地手写板", 1010, 730);
        var ink = new InkCanvas { Width = 900, Height = 480, Background = Brushes.White, DefaultDrawingAttributes = new DrawingAttributes { Color = Colors.DarkSlateGray, Width = 3, Height = 3, FitToCurve = true }, EditingMode = InkCanvasEditingMode.Ink };
        if (existing?.Tag is NoteEmbed previous) { try { using var stream = new MemoryStream(Convert.FromBase64String(previous.Source)); ink.Strokes.Add(new StrokeCollection(stream)); } catch { MessageBox.Show(owner, "手写原始笔迹无法读取，未更改原内容。"); return; } }
        var history = new Stack<StrokeCollection>(); var last = ink.Strokes.Clone(); bool replaying = false;
        ink.Strokes.StrokesChanged += (_, _) => { if (!replaying) history.Push(last); last = ink.Strokes.Clone(); };
        var penWidth = new Slider { Minimum = 1, Maximum = 12, Value = 3, Width = 100, VerticalAlignment = VerticalAlignment.Center, ToolTip = "画笔粗细" }; penWidth.ValueChanged += (_, _) => { ink.DefaultDrawingAttributes.Width = ink.DefaultDrawingAttributes.Height = penWidth.Value; };
        var tools = Row(Button("画笔", () => ink.EditingMode = InkCanvasEditingMode.Ink), Button("橡皮", () => ink.EditingMode = InkCanvasEditingMode.EraseByStroke), Button("撤销", () => { if (history.Count == 0) return; replaying = true; var old = history.Pop(); ink.Strokes.Clear(); ink.Strokes.Add(old); replaying = false; last = ink.Strokes.Clone(); }), Button("清空", () => ink.Strokes.Clear()), penWidth);
        foreach (var pair in new[] { ("墨色", Colors.DarkSlateGray), ("蓝色", Colors.RoyalBlue), ("绿色", Colors.SeaGreen), ("红色", Colors.IndianRed) }) tools.Children.Add(Button(pair.Item1, () => { ink.DefaultDrawingAttributes.Color = pair.Item2; ink.EditingMode = InkCanvasEditingMode.Ink; }));
        var status = Text("支持鼠标、触控笔；触控操作取决于设备驱动。手写以原始笔迹和图片保存在本地，不做文字识别。", 12, Muted);
        dialog.Content = new Border { Padding = new Thickness(22), Child = Stack(Text("随手画出你的思路", 23, bold: true), Space(10), tools, Space(12), new ScrollViewer { Content = ink, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 480 }, Space(10), status, Space(12), Row(Button("取消", () => dialog.Close()), Button(existing == null ? "插入手写" : "更新手写", () =>
        {
            if (ink.Strokes.Count == 0) { status.Text = "请先写下内容。"; return; }
            try { using var stream = new MemoryStream(); ink.Strokes.Save(stream); InsertNoteImage(editor, Rasterize(ink), new() { Kind = "ink", Source = Convert.ToBase64String(stream.ToArray()) }, existing); dialog.Close(); } catch (Exception ex) { status.Text = ex.Message; }
        }, true))) }; dialog.ShowDialog();
    }
}


