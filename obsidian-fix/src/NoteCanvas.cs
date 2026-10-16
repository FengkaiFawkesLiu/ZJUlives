using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Media;
using static Shike.Theme;

namespace Shike;
// Strokes are ISF vector data, anchored to the same fixed-width paper as the document.
public sealed class NoteCanvas : DockPanel
{
    public InkCanvas Ink { get; } = new() { Background = Brushes.Transparent, IsHitTestVisible = false, EditingMode = InkCanvasEditingMode.None, DefaultDrawingAttributes = new() { Color = Colors.DarkSlateGray, Width = 3, Height = 3, FitToCurve = true } };
    public ScrollViewer Scroll { get; }
    public Grid Paper { get; } = new() { Background = Brushes.White, HorizontalAlignment = HorizontalAlignment.Left, MinHeight = 780 };
    readonly RichTextBox editor;
    readonly Action changed;
    readonly WrapPanel controls = new() { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 8) };
    readonly TextBlock hint = Text("", 11, Muted);
    readonly Stack<StrokeCollection> history = new(), future = new();
    StrokeCollection snapshot;
    bool replay;
    public NoteCanvas(RichTextBox editor, string source, Action changed)
    {
        this.editor = editor; this.changed = changed;
        if (!string.IsNullOrEmpty(source)) { using var stream = new MemoryStream(Convert.FromBase64String(source)); Ink.Strokes = new StrokeCollection(stream); }
        snapshot = Ink.Strokes.Clone(); Observe();
        Ink.Strokes.StrokesChanged += (_, _) => { Observe(); Record(); };
        Ink.SelectionMoved += (_, _) => Record(); Ink.SelectionResized += (_, _) => Record();
        Ink.StrokeCollected += (_, _) => Grow();
        editor.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled; editor.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        editor.VerticalAlignment = VerticalAlignment.Top; editor.BorderThickness = new Thickness(0); editor.MinHeight = 600;
        Paper.RowDefinitions.Add(new() { Height = GridLength.Auto }); Paper.RowDefinitions.Add(new() { Height = new GridLength(280) });
        Paper.Children.Add(editor); Grid.SetRowSpan(Ink, 2); Paper.Children.Add(Ink); UpdatePaper();
        editor.TextChanged += (_, _) => UpdatePaper(); editor.SizeChanged += (_, _) => UpdatePaper();
        Scroll = new ScrollViewer { Content = Paper, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, Background = Brush("#EBEFEB") };
        SetDock(controls, Dock.Top); Children.Add(controls); SetDock(hint, Dock.Bottom); Children.Add(hint); Children.Add(Scroll);
        void Tool(string label, Action action) { var b = Button(label, action); b.Padding = new Thickness(8, 6, 8, 6); controls.Children.Add(b); }
        Tool("文字编辑", () => SetMode(InkCanvasEditingMode.None)); Tool("画笔", () => SetMode(InkCanvasEditingMode.Ink)); Tool("橡皮", () => SetMode(InkCanvasEditingMode.EraseByPoint)); Tool("整笔擦除", () => SetMode(InkCanvasEditingMode.EraseByStroke)); Tool("选择笔迹", () => SetMode(InkCanvasEditingMode.Select)); Tool("撤销笔迹", Undo); Tool("重做笔迹", Redo);
        foreach (var (name, color) in NotePalette.Colors)
        {
            var b = Button("●", () => { Ink.DefaultDrawingAttributes.Color = color; foreach (var stroke in Ink.GetSelectedStrokes()) stroke.DrawingAttributes.Color = color; if (Ink.GetSelectedStrokes().Count > 0) Record(); });
            b.Foreground = new SolidColorBrush(color); b.ToolTip = name; b.Padding = new Thickness(6); b.Margin = new Thickness(0, 0, 2, 0); controls.Children.Add(b);
        }
        var width = Combo(new[] { "1", "2", "3", "5", "8", "12" }, "3"); width.ToolTip = "笔宽";
        width.SelectionChanged += (_, _) => { double v = double.Parse((string)width.SelectedItem); Ink.DefaultDrawingAttributes.Width = Ink.DefaultDrawingAttributes.Height = v; foreach (var stroke in Ink.GetSelectedStrokes()) { stroke.DrawingAttributes.Width = stroke.DrawingAttributes.Height = v; } if (Ink.GetSelectedStrokes().Count > 0) Record(); }; controls.Children.Add(width);
        Tool("收起画笔", () => { SetMode(InkCanvasEditingMode.None); controls.Visibility = Visibility.Collapsed; });
        Tool("向下加纸", () => Paper.MinHeight = Math.Min(50000, Math.Max(Paper.ActualHeight, Paper.MinHeight) + 500));
    }
    void Observe() { foreach (var stroke in Ink.Strokes) { stroke.StylusPointsChanged -= StrokeChanged; stroke.StylusPointsChanged += StrokeChanged; stroke.DrawingAttributesChanged -= AttributesChanged; stroke.DrawingAttributesChanged += AttributesChanged; } }
    void StrokeChanged(object? sender, EventArgs e) => Record();
    void AttributesChanged(object? sender, PropertyDataChangedEventArgs e) => Record();
    void Record()
    {
        if (replay) return;
        history.Push(snapshot); if (history.Count > 40) { var keep = history.Take(40).Reverse().ToArray(); history.Clear(); foreach (var s in keep) history.Push(s); }
        future.Clear(); snapshot = Ink.Strokes.Clone(); changed(); Grow();
    }
    void Restore(StrokeCollection strokes) { replay = true; Ink.Strokes.Clear(); Ink.Strokes.Add(strokes.Clone()); snapshot = Ink.Strokes.Clone(); replay = false; changed(); Grow(); }
    public void Undo() { if (history.Count == 0) return; future.Push(snapshot); Restore(history.Pop()); }
    public void Redo() { if (future.Count == 0) return; history.Push(snapshot); Restore(future.Pop()); }
    void Grow()
    {
        if (Ink.Strokes.Count == 0) return;
        Paper.MinHeight = Math.Max(Paper.MinHeight, Math.Min(50000, Ink.Strokes.GetBounds().Bottom + 180));
        Paper.Width = Math.Max(double.IsFinite(Paper.Width) ? Paper.Width : 628, Math.Min(50000, Ink.Strokes.GetBounds().Right + 24));
    }
    public void UpdatePaper() { double width = editor.Document.PageWidth; editor.Width = double.IsFinite(width) ? width : 628; Paper.Width = editor.Width; Grow(); }
    public void SetMode(InkCanvasEditingMode mode)
    {
        controls.Visibility = Visibility.Visible; Ink.EditingMode = mode; Ink.IsHitTestVisible = mode != InkCanvasEditingMode.None;
        editor.IsReadOnly = mode != InkCanvasEditingMode.None;
        hint.Text = mode == InkCanvasEditingMode.None ? "文字编辑 · 笔迹覆盖在正文上，修改排版后可用选择工具移动笔迹" : "纸面批注 · 可选中移动、缩放或改色 · 橡皮只擦除笔迹";
    }
    public string Encode()
    {
        if (Ink.Strokes.Count == 0) return "";
        using var stream = new MemoryStream(); Ink.Strokes.Save(stream);
        if (stream.Length > 6 * 1024 * 1024) throw new InvalidDataException("手写笔迹过多，请拆成多篇笔记。");
        return Convert.ToBase64String(stream.ToArray());
    }
}
public static class NotePalette
{
    public static readonly (string Name, Color Color)[] Colors = { ("墨色", System.Windows.Media.Colors.DarkSlateGray), ("黑色", System.Windows.Media.Colors.Black), ("蓝色", System.Windows.Media.Colors.RoyalBlue), ("红色", System.Windows.Media.Colors.Firebrick), ("绿色", System.Windows.Media.Colors.SeaGreen), ("紫色", System.Windows.Media.Colors.DarkOrchid), ("橙色", System.Windows.Media.Colors.DarkOrange), ("灰色", System.Windows.Media.Colors.Gray) };
}

