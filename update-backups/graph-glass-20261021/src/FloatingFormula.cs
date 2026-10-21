using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace Shike;
public static class FloatingFormula
{
    public static Figure? FigureOf(Image image)
    {
        DependencyObject? p = image;
        while (p != null) { if (p is Figure f) return f; p = p is FrameworkContentElement c ? c.Parent : p is FrameworkElement e ? e.Parent : null; }
        return null;
    }
    static void Detach(Image image)
    {
        if (image.Parent is InlineUIContainer inline) { inline.Child = null; if (inline.Parent is Paragraph p) p.Inlines.Remove(inline); else if (inline.Parent is Span s) s.Inlines.Remove(inline); }
        else if (image.Parent is BlockUIContainer block) block.Child = null;
    }
    public static void Wrap(Image image, double x = 0, double y = 0, Paragraph? anchor = null)
    {
        if (image.Tag is not NoteEmbed tag) return;
        var figure = FigureOf(image);
        if (figure == null)
        {
            var inline = image.Parent as InlineUIContainer;
            var pointer = inline?.ElementStart;
            anchor ??= inline?.ContentStart.Paragraph;
            if (pointer == null && anchor == null) return;
            Detach(image);
            figure = new Figure(new BlockUIContainer(image)) { Padding = new Thickness(0), Margin = new Thickness(10), HorizontalAnchor = FigureHorizontalAnchor.ContentLeft, VerticalAnchor = FigureVerticalAnchor.ParagraphTop, WrapDirection = WrapDirection.Both, CanDelayPlacement = false };
            if (anchor != null) anchor.Inlines.InsertBefore(anchor.Inlines.FirstInline ?? AddEmpty(anchor), figure);
            else new Span(figure, pointer!);
        }
        else if (anchor != null && !IsInside(anchor, figure))
        {
            if (figure.Parent is Paragraph p) p.Inlines.Remove(figure); else if (figure.Parent is Span s) s.Inlines.Remove(figure);
            anchor.Inlines.InsertBefore(anchor.Inlines.FirstInline ?? AddEmpty(anchor), figure);
        }
        tag.Wrap = true; tag.X = Math.Max(0, Math.Min(1500, x)); tag.Y = Math.Max(0, Math.Min(10000, y));
        figure.HorizontalOffset = tag.X; figure.VerticalOffset = tag.Y; Update(image);
    }
    static Inline AddEmpty(Paragraph p) { var run = new Run(); p.Inlines.Add(run); return run; }
    static bool IsInside(DependencyObject element, Figure figure)
    {
        DependencyObject? p = element; while (p != null) { if (p == figure) return true; p = (p as FrameworkContentElement)?.Parent; } return false;
    }
    public static void Inline(Image image)
    {
        var figure = FigureOf(image); if (figure == null) return;
        var pointer = figure.ElementStart; Detach(image);
        if (figure.Parent is Paragraph p) p.Inlines.Remove(figure); else if (figure.Parent is Span s) s.Inlines.Remove(figure);
        new InlineUIContainer(image, pointer);
        if (image.Tag is NoteEmbed tag) { tag.Wrap = false; tag.X = tag.Y = 0; }
    }
    public static void Update(Image image)
    {
        if (FigureOf(image) is not Figure figure) return;
        double page = 580; DependencyObject? parent = figure;
        while (parent != null) { if (parent is FlowDocument doc) { page = doc.Resources["floating-editor"] is RichTextBox rich && rich.ActualWidth > 0 ? rich.ActualWidth - rich.Padding.Left - rich.Padding.Right : doc.PageWidth - doc.PagePadding.Left - doc.PagePadding.Right; break; } parent = (parent as FrameworkContentElement)?.Parent; }
        if (!double.IsFinite(page)) page = 580;
        if (image.Width > page - 20 && image.Tag is NoteEmbed sized && sized.NaturalWidth > 0) { image.Width = Math.Max(24,page-20); sized.FontSize = image.Width / sized.NaturalWidth * 24; }
        var tag = (NoteEmbed)image.Tag; double x = Math.Clamp(tag.X, 0, Math.Max(0,page-image.Width-20));
        bool left = x <= (page-image.Width)/2;
        figure.HorizontalAnchor = left ? FigureHorizontalAnchor.ContentLeft : FigureHorizontalAnchor.ContentRight;
        figure.Margin = left ? new Thickness(x+10,tag.Y+10,10,10) : new Thickness(10,tag.Y+10,Math.Max(10,page-x-image.Width-10),10);
        figure.WrapDirection = left ? WrapDirection.Right : WrapDirection.Left;
        figure.Width = new FigureLength(Math.Max(20, image.Width));
        figure.Height = new FigureLength(1, FigureUnitType.Auto);
    }
    public static void Place(RichTextBox editor, Image image, Point topLeft)
    {
        var hit = editor.GetPositionFromPoint(new Point(Math.Max(0, topLeft.X), Math.Max(0, topLeft.Y)), true);
        var paragraph = hit?.Paragraph;
        if (paragraph != null && FigureOf(image) is Figure current && IsInside(paragraph,current)) paragraph = current.ElementStart.Paragraph;
        paragraph ??= FigureOf(image)?.ElementStart.Paragraph ?? (image.Parent as InlineUIContainer)?.ContentStart.Paragraph ?? editor.Document.Blocks.OfType<Paragraph>().FirstOrDefault();
        if (paragraph == null) return;
        double anchorY = paragraph.ContentStart.GetCharacterRect(LogicalDirection.Forward).Top;
        double maxX = Math.Max(0, editor.ActualWidth - editor.Padding.Left - editor.Padding.Right - image.Width - 20);
        Wrap(image, Math.Clamp(topLeft.X - editor.Padding.Left - 10, 0, maxX), Math.Max(0, topLeft.Y - anchorY - 10), paragraph);
    }
    public static void Attach(RichTextBox editor, Action changed)
    {
        editor.Document.Resources["floating-editor"] = editor;
        editor.SizeChanged += (_, _) => { foreach (var image in RichNoteCodec.Images(editor.Document)) Update(image); };
        Image? selected = null; FormulaAdorner? adornment = null; AdornerLayer? layer = null;
        Point start = default, imageOrigin = default; bool dragging = false, moved = false;
        void Select(Image image)
        {
            if (adornment != null) layer?.Remove(adornment);
            selected = image; layer = AdornerLayer.GetAdornerLayer(image);
            adornment = new FormulaAdorner(image, editor, changed); layer?.Add(adornment);
        }
        editor.PreviewMouseLeftButtonDown += (_, e) =>
        {
            DependencyObject? p = e.OriginalSource as DependencyObject;
            while (p != null && p is not Image) p = p is Visual ? VisualTreeHelper.GetParent(p) : null;
            if (p is not Image image || image.Tag is not NoteEmbed tag || tag.Kind != "math") return;
            Select(image);
            if (e.ClickCount > 1) return;
            Window.GetWindow(editor)?.UpdateLayout(); start = e.GetPosition(Window.GetWindow(editor)); imageOrigin = image.TranslatePoint(new Point(), editor); dragging = editor.CaptureMouse(); moved = false; e.Handled = true;
        };
        editor.PreviewMouseMove += (_, e) =>
        {
            if (!dragging || selected == null || e.LeftButton != MouseButtonState.Pressed) return;
            var delta = e.GetPosition(Window.GetWindow(editor)) - start;
            if (delta.Length > 4) moved = true;
            if (moved && adornment != null) { adornment.RenderTransform = new TranslateTransform(delta.X, delta.Y); editor.Cursor = Cursors.SizeAll; }
            e.Handled = true;
        };
        editor.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (!dragging || selected == null) return;
            var delta = e.GetPosition(Window.GetWindow(editor)) - start; dragging = false; editor.ReleaseMouseCapture(); editor.Cursor = Cursors.IBeam;
            if (moved) { Place(editor, selected, imageOrigin + delta); changed(); editor.UpdateLayout(); Select(selected); }
            e.Handled = true;
        };
        editor.LostMouseCapture += (_, _) => { dragging = false; editor.Cursor = Cursors.IBeam; if (adornment != null) adornment.RenderTransform = Transform.Identity; };
        editor.Unloaded += (_, _) => { if (adornment != null) layer?.Remove(adornment); };
    }
}
public sealed class FormulaAdorner : Adorner
{
    readonly VisualCollection visuals;
    readonly Thumb handle;
    readonly Image image;
    public FormulaAdorner(Image image, RichTextBox editor, Action changed) : base(image)
    {
        this.image = image; visuals = new VisualCollection(this);
        handle = new Thumb { Width = 13, Height = 13, Background = Brushes.SeaGreen, Cursor = Cursors.SizeNWSE, ToolTip = "拖动方块等比例缩放公式" };
        visuals.Add(handle);
        handle.DragDelta += (_, e) =>
        {
            if (image.Tag is not NoteEmbed tag) return;
            if (tag.NaturalWidth <= 0) tag.NaturalWidth = image.Width * 24 / Math.Max(10, tag.FontSize);
            double vertical = e.VerticalChange * image.ActualWidth / Math.Max(1,image.ActualHeight);
            double delta = Math.Abs(e.HorizontalChange) >= Math.Abs(vertical) ? e.HorizontalChange : vertical;
            double width = Math.Clamp(image.Width + delta, 24, Math.Max(24, editor.Document.PageWidth - 68));
            NoteFormatting.SetFormulaSize(image, width / tag.NaturalWidth * 24); FloatingFormula.Update(image); changed(); InvalidateArrange();
        };
    }
    protected override int VisualChildrenCount => visuals.Count;
    protected override Visual GetVisualChild(int index) => visuals[index];
    protected override Size ArrangeOverride(Size size) { handle.Arrange(new Rect(Math.Max(0,image.ActualWidth - 6),Math.Max(0,image.ActualHeight - 6),13,13)); return size; }
    protected override void OnRender(DrawingContext dc) { dc.DrawRectangle(null,new Pen(Brushes.SeaGreen,1),new Rect(0,0,Math.Max(0,image.ActualWidth),Math.Max(0,image.ActualHeight))); }
}


