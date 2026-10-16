using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using static Shike.Theme;

namespace Shike;

public sealed partial class MainWindow
{
    string? quickNoteKey;
    string? quickNoteLibrary;
    Action? updateQuickAppearance;
    double? quickGraphZoom;
    double quickGraphHorizontal, quickGraphVertical;
    void ExitQuickAssociation()
    {
        quickNoteKey = null; quickNoteLibrary = null; quickGraphZoom = null; updateQuickAppearance?.Invoke();
    }
    FrameworkElement BuildNoteGraph(NoteLibrary library, string query)
    {
        if (quickNoteLibrary != library.Id || !store.Data.Items.Any(i => i.Kind == "笔记" && NotesModel.Key(i) == quickNoteKey)) { quickNoteKey = null; quickNoteLibrary = null; }
        var graph = NotesModel.Graph(store.Data, library);
        var matches = graph.Nodes.Where(n => n.Label.Contains(query, StringComparison.OrdinalIgnoreCase) || n.Kind == "note" && store.Data.Items.Any(i => "note:" + i.Id == n.Key && i.Note.Contains(query, StringComparison.OrdinalIgnoreCase))).Select(n => n.Key).ToHashSet();
        var included = query.Length == 0 ? graph.Nodes.Select(n => n.Key).ToHashSet() : matches.Concat(graph.Edges.Where(e => matches.Contains(e.From) || matches.Contains(e.To)).SelectMany(e => new[] { e.From, e.To })).ToHashSet();
        int total = included.Count;
        var nodes = graph.Nodes.Where(n => included.Contains(n.Key)).Take(240).ToList(); var nodeKeys = nodes.Select(n => n.Key).ToHashSet();
        var edges = graph.Edges.Where(e => nodeKeys.Contains(e.From) && nodeKeys.Contains(e.To)).ToList();
        var dock = new DockPanel();
        var quickStatus = Text("右键笔记节点，开始快捷关联", 12, Green, true);
        var quickBanner = new Border { Background = Brush("#EDF3E9"), Padding = new Thickness(14, 10, 14, 10), Child = quickStatus };
        DockPanel.SetDock(quickBanner, Dock.Top); dock.Children.Add(quickBanner);
        var legend = Row(Text("● 笔记   ", 11, Brush("#8B79AF")), Text("● 课程   ", 11, Green), Text("● 其他事项   ", 11, Brush("#B38A4D")), Text("点击打开 · 拖动节点或空白处", 11, Muted)); legend.Margin = new Thickness(14, 10, 14, 10); DockPanel.SetDock(legend, Dock.Top); dock.Children.Add(legend);
        var bottom = new DockPanel { Margin = new Thickness(14, 8, 14, 12) }; DockPanel.SetDock(bottom, Dock.Bottom); dock.Children.Add(bottom);
        var hint = Text(total > 240 ? $"当前显示前 240 / {total} 个节点，请搜索缩小范围" : $"{nodes.Count} 个节点 · {edges.Count} 条连线", 11, Muted);
        var slider = new Slider { Minimum = .35, Maximum = 1.8, Value = .7, Width = 125, VerticalAlignment = VerticalAlignment.Center, ToolTip = "缩放图谱" };
        var zoom = Row(Text("缩放", 11, Muted), slider); DockPanel.SetDock(zoom, Dock.Right); bottom.Children.Add(zoom); bottom.Children.Add(hint);
        var scroll = new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, PanningMode = PanningMode.Both };
        var canvas = new Canvas { Width = 1150, Height = 820, Background = Brush("#F9FAF7"), ClipToBounds = false };
        var scale = new ScaleTransform(slider.Value, slider.Value); canvas.LayoutTransform = scale; scroll.Content = canvas; dock.Children.Add(scroll);
        if (nodes.Count == 0)
        {
            var empty = Stack(Text(query.Length > 0 ? "没有找到相关节点" : "让想法开始连接", 23, Green, true), Space(12), Text("新建笔记，通过“关联笔记”和“关联课程”建立连线。", 14, Muted), Space(20), Button("＋ 新建笔记", () => EditKnowledgeNote(null), true));
            Canvas.SetLeft(empty, 90); Canvas.SetTop(empty, 150); canvas.Children.Add(empty);
        }
        var positions = ForcePositions(nodes, edges);
        foreach (var n in nodes) if (library.Positions.TryGetValue(n.Key, out var saved)) positions[n.Key] = new Point(saved.X, saved.Y);
        if (positions.Count > 0) { canvas.Width = Math.Max(1150, positions.Values.Max(p => p.X) + 100); canvas.Height = Math.Max(820, positions.Values.Max(p => p.Y) + 100); }
        var lines = new Dictionary<NoteEdge, Line>();
        var dots = new Dictionary<string, Ellipse>();
        updateQuickAppearance = () =>
        {
            var source = nodes.FirstOrDefault(n => n.Key == quickNoteKey);
            quickStatus.Text = quickNoteKey == null ? "右键笔记节点，开始快捷关联" : $"快捷关联：{source?.Label ?? "已选笔记"} → 右键目标节点关联；左键任意位置或右键起点退出";
            foreach (var pair in dots) { pair.Value.Stroke = pair.Key == quickNoteKey ? Brush("#E1A34B") : Brushes.White; pair.Value.StrokeThickness = pair.Key == quickNoteKey ? 6 : 3; }
        };
        foreach (var edge in edges)
        {
            var line = new Line { Stroke = Brush("#CBD8D0"), StrokeThickness = 1.4, IsHitTestVisible = false }; lines[edge] = line; canvas.Children.Add(line);
        }
        void DrawLines()
        {
            foreach (var pair in lines) { var a = positions[pair.Key.From]; var b = positions[pair.Key.To]; pair.Value.X1 = a.X; pair.Value.Y1 = a.Y; pair.Value.X2 = b.X; pair.Value.Y2 = b.Y; }
        }
        void SavePositions()
        {
            var before = library.Positions.ToDictionary(p => p.Key, p => p.Value);
            try { foreach (var p in positions) library.Positions[p.Key] = new() { X = p.Value.X, Y = p.Value.Y }; store.Save(); hint.Text = "布局已保存到本地"; }
            catch (Exception ex) { library.Positions = before; MessageBox.Show(this, "布局未保存：" + ex.Message); }
        }
        foreach (var node in nodes)
        {
            int degree = edges.Count(e => e.From == node.Key || e.To == node.Key);
            double diameter = Math.Min(48, 27 + degree * 3);
            var color = node.Kind == "note" ? Brush("#8B79AF") : node.Kind == "course" ? Green : Brush("#B38A4D");
            var dot = new Ellipse { Width = diameter, Height = diameter, Fill = node.Missing ? Muted : color, Stroke = Brushes.White, StrokeThickness = 3 };
            dots[node.Key] = dot;
            var label = Text(node.Label, 17, Ink, degree > 1); label.TextAlignment = TextAlignment.Center; label.MaxHeight = 48;
            var element = new Border { Width = 150, Background = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = node.Label + "\n" + node.Detail, Child = Stack(new Border { Height = 54, Child = dot }, label), Opacity = query.Length > 0 && !matches.Contains(node.Key) ? .48 : 1 };
            void Position() { Canvas.SetLeft(element, positions[node.Key].X - 75); Canvas.SetTop(element, positions[node.Key].Y - 27); }
            Position(); canvas.Children.Add(element);
            element.MouseRightButtonDown += (_, e) =>
            {
                e.Handled = true;
                if (quickNoteKey == node.Key) { ExitQuickAssociation(); return; }
                if (quickNoteKey == null)
                {
                    if (node.Kind != "note" || node.Missing) { hint.Text = "请先右键一个笔记节点作为起点"; return; }
                    quickNoteKey = node.Key; quickNoteLibrary = library.Id; updateQuickAppearance?.Invoke(); return;
                }
                if (node.Missing) { hint.Text = "此节点的原事项已移除，无法新建关联"; return; }
                var sourceKey = quickNoteKey;
                quickGraphZoom = slider.Value; quickGraphHorizontal = scroll.HorizontalOffset; quickGraphVertical = scroll.VerticalOffset;
                if (Commit(() =>
                {
                    NotesModel.Connect(store.Data, library, sourceKey, node);
                    foreach (var p in positions) library.Positions[p.Key] = new() { X = p.Value.X, Y = p.Value.Y };
                })) updateQuickAppearance?.Invoke();
            };
            Point? origin = null; Point initial = default; bool moved = false;
            element.MouseLeftButtonDown += (_, e) => { origin = e.GetPosition(canvas); initial = positions[node.Key]; moved = false; element.CaptureMouse(); e.Handled = true; };
            element.MouseMove += (_, e) =>
            {
                if (origin is not Point start) return; var current = e.GetPosition(canvas); var delta = current - start;
                if (delta.Length > 4) moved = true;
                if (moved) { positions[node.Key] = new Point(Math.Clamp(initial.X + delta.X, 85, canvas.Width - 85), Math.Clamp(initial.Y + delta.Y, 45, canvas.Height - 80)); Position(); DrawLines(); }
            };
            element.MouseLeftButtonUp += (_, e) => { if (origin == null) return; origin = null; element.ReleaseMouseCapture(); e.Handled = true; if (moved) SavePositions(); else OpenNoteTarget(node); };
            element.LostMouseCapture += (_, _) => origin = null;
        }
        DrawLines(); updateQuickAppearance?.Invoke();
        slider.ValueChanged += (_, _) => { scale.ScaleX = scale.ScaleY = slider.Value; };
        scroll.PreviewMouseWheel += (_, e) => { slider.Value = Math.Clamp(slider.Value + (e.Delta > 0 ? .08 : -.08), slider.Minimum, slider.Maximum); e.Handled = true; };
        Point? pan = null; double horizontal = 0, vertical = 0;
        canvas.MouseLeftButtonDown += (_, e) => { if (e.OriginalSource != canvas) return; pan = e.GetPosition(scroll); horizontal = scroll.HorizontalOffset; vertical = scroll.VerticalOffset; canvas.CaptureMouse(); canvas.Cursor = Cursors.ScrollAll; };
        canvas.MouseMove += (_, e) => { if (pan is Point p) { var delta = e.GetPosition(scroll) - p; scroll.ScrollToHorizontalOffset(horizontal - delta.X); scroll.ScrollToVerticalOffset(vertical - delta.Y); } };
        canvas.MouseLeftButtonUp += (_, _) => { pan = null; canvas.ReleaseMouseCapture(); canvas.Cursor = Cursors.Arrow; };
        canvas.LostMouseCapture += (_, _) => { pan = null; canvas.Cursor = Cursors.Arrow; };
        var reset = Button("重排", () => { if (Commit(() => library.Positions.Clear())) Render(); }); reset.Padding = new Thickness(8, 3, 8, 3); DockPanel.SetDock(reset, Dock.Right); bottom.Children.Insert(0, reset);
        var save = Button("保存布局", SavePositions); save.Padding = new Thickness(8, 3, 8, 3); DockPanel.SetDock(save, Dock.Right); bottom.Children.Insert(0, save);
        scroll.Loaded += (_, _) => {
            slider.Value = quickNoteKey != null && quickGraphZoom.HasValue ? quickGraphZoom.Value : Math.Clamp(Math.Min(scroll.ViewportWidth / 1150, scroll.ViewportHeight / 820), .35, 1);
            if (quickNoteKey != null && quickGraphZoom.HasValue) { scroll.UpdateLayout(); scroll.ScrollToHorizontalOffset(quickGraphHorizontal); scroll.ScrollToVerticalOffset(quickGraphVertical); }
        };
        return new Border { Background = Brushes.White, BorderBrush = Theme.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14), Child = dock, ClipToBounds = true };
    }
    internal static Dictionary<string, Point> ForcePositions(List<NoteNode> nodes, List<NoteEdge> edges)
    {
        var p = new Dictionary<string, Point>();
        for (int i = 0; i < nodes.Count; i++) { double angle = i * 2.399963; double radius = 50 + 26 * Math.Sqrt(i); p[nodes[i].Key] = new Point(560 + radius * Math.Cos(angle), 385 + radius * Math.Sin(angle)); }
        for (int iteration = 0; iteration < 100; iteration++)
        {
            var force = nodes.ToDictionary(n => n.Key, n => new Vector((560 - p[n.Key].X) * .012, (385 - p[n.Key].Y) * .012));
            for (int i = 0; i < nodes.Count; i++) for (int j = i + 1; j < nodes.Count; j++)
            {
                var delta = p[nodes[i].Key] - p[nodes[j].Key]; double distance = Math.Max(5, delta.Length); if (distance > 350) continue;
                var push = delta / distance * (90000 / (distance * distance)); force[nodes[i].Key] += push; force[nodes[j].Key] -= push;
            }
            foreach (var edge in edges) { var delta = p[edge.To] - p[edge.From]; double length = Math.Max(1, delta.Length); var pull = delta / length * (length - 150) * .035; force[edge.From] += pull; force[edge.To] -= pull; }
            foreach (var node in nodes) { var next = p[node.Key] + force[node.Key]; p[node.Key] = new Point(Math.Clamp(next.X, 90, 1060), Math.Clamp(next.Y, 60, 720)); }
        }
        return p;
    }
}
