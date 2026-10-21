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
    readonly HashSet<string> mindCollapsed = new();
    string? mindRootKey;
    sealed class MindBranch
    {
        public NoteNode Node = null!;
        public List<MindBranch> Children = new();
        public double Height;
        public Point Position;
    }
    FrameworkElement BuildNoteMindMap(NoteLibrary library, string query)
    {
        var graph = NotesModel.Graph(store.Data, library);
        var match = graph.Nodes.Where(n => n.Label.Contains(query, StringComparison.OrdinalIgnoreCase) || n.Kind == "note" && store.Data.Items.Any(i => NotesModel.Key(i) == n.Key && i.Note.Contains(query, StringComparison.OrdinalIgnoreCase))).Select(n => n.Key).ToHashSet();
        var include = query.Length == 0 ? graph.Nodes.Select(n => n.Key).ToHashSet() : match.Concat(graph.Edges.Where(e => match.Contains(e.From) || match.Contains(e.To)).SelectMany(e => new[] { e.From, e.To })).ToHashSet();
        var nodes = graph.Nodes.Where(n => include.Contains(n.Key)).Take(240).ToDictionary(n => n.Key);
        var adjacency = nodes.Keys.ToDictionary(k => k, _ => new List<string>());
        foreach (var e in graph.Edges.Where(e => nodes.ContainsKey(e.From) && nodes.ContainsKey(e.To))) { adjacency[e.From].Add(e.To); adjacency[e.To].Add(e.From); }
        if (mindRootKey != null && !nodes.ContainsKey(mindRootKey)) mindRootKey = null;
        var visited = new HashSet<string>();
        MindBranch Branch(string key)
        {
            visited.Add(key); var branch = new MindBranch { Node = nodes[key] };
            foreach (var child in adjacency[key].OrderByDescending(k => adjacency[k].Count)) if (!visited.Contains(child)) branch.Children.Add(Branch(child));
            return branch;
        }
        MindBranch root;
        if (mindRootKey != null) root = Branch(mindRootKey);
        else
        {
            root = new() { Node = new("library:" + library.Id, library.Name, "library", "笔记库") };
            foreach (var node in nodes.Values.OrderBy(n => n.Kind == "course" ? 0 : 1).ThenByDescending(n => adjacency[n.Key].Count)) if (!visited.Contains(node.Key)) root.Children.Add(Branch(node.Key));
        }
        bool Collapsed(MindBranch b) => mindCollapsed.Contains(library.Id + ":" + b.Node.Key);
        double Measure(MindBranch b) { b.Height = Collapsed(b) || b.Children.Count == 0 ? 100 : Math.Max(100, b.Children.Sum(Measure)); return b.Height; }
        var visible = new List<MindBranch>(); int maxDepth = 0;
        void Position(MindBranch b, int depth, double top)
        {
            maxDepth = Math.Max(maxDepth, depth); b.Position = new Point(40 + depth * 270, top + b.Height / 2); visible.Add(b);
            if (Collapsed(b)) return; double offset = top;
            foreach (var child in b.Children) { Position(child, depth + 1, offset); offset += child.Height; }
        }
        Measure(root); Position(root, 0, 45);
        var dock = new DockPanel(); var status = Text("思维导图 · 实线为展开分支，虚线为交叉关联", 12, Muted);
        var roots = new[] { "整个笔记库" }.Concat(nodes.Values.Select(n => n.Label)).ToArray(); var center = Combo(roots, "整个笔记库"); center.Width = 170; center.SelectedIndex = mindRootKey == null ? 0 : nodes.Keys.ToList().IndexOf(mindRootKey) + 1;
        center.SelectionChanged += (_, _) => { mindRootKey = center.SelectedIndex <= 0 ? null : nodes.Keys.ElementAt(center.SelectedIndex - 1); Render(); };
        var topbar = Stack(Row(Text("中心", 12, Muted), center, Button("全部展开", () => { mindCollapsed.Clear(); Render(); })), Space(8), status); topbar.Margin = new Thickness(12); DockPanel.SetDock(topbar, Dock.Top); dock.Children.Add(topbar);
        var slider = new Slider { Minimum = .35, Maximum = 1.6, Value = .8, Width = 130, VerticalAlignment = VerticalAlignment.Center };
        var bottom = Row(Text("缩放  ", 11, Muted), slider, Text($"  {visible.Count - (root.Node.Kind == "library" ? 1 : 0)} 个可见节点 · 点击打开，＋/− 折叠", 11, Muted)); bottom.Margin = new Thickness(12); DockPanel.SetDock(bottom, Dock.Bottom); dock.Children.Add(bottom);
        var canvas = new Canvas { Width = Math.Max(900, maxDepth * 270 + 340), Height = Math.Max(580, root.Height + 90), Background = Brush("#F9FAF7") };
        var scale = new ScaleTransform(.8, .8); canvas.LayoutTransform = scale;
        var scroll = new ScrollViewer { Content = canvas, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, PanningMode = PanningMode.Both }; dock.Children.Add(scroll);
        scroll.Loaded += (_, _) => { slider.Value = Math.Clamp(Math.Min((scroll.ActualWidth - 25) / canvas.Width, (scroll.ActualHeight - 25) / canvas.Height), .35, 1); };
        var visibleMap = visible.ToDictionary(b => b.Node.Key);
        var treeEdges = new HashSet<string>();
        string EdgeKey(string a, string b) => string.CompareOrdinal(a, b) < 0 ? a + "|" + b : b + "|" + a;
        void Curve(MindBranch a, MindBranch b, bool cross)
        {
            var from = new Point(a.Position.X + 210, a.Position.Y); var to = new Point(b.Position.X, b.Position.Y);
            var geometry = new PathGeometry(new[] { new PathFigure(from, new PathSegment[] { new BezierSegment(new Point(from.X + 40, from.Y), new Point(to.X - 40, to.Y), to, true) }, false) });
            var path = new System.Windows.Shapes.Path { Data = geometry, Stroke = cross ? Brush("#BDB3CD") : Brush("#91B4A2"), StrokeThickness = cross ? 1 : 2, IsHitTestVisible = false };
            if (cross) path.StrokeDashArray = new DoubleCollection { 4, 4 }; canvas.Children.Add(path);
        }
        foreach (var branch in visible) if (!Collapsed(branch)) foreach (var child in branch.Children) if (visibleMap.ContainsKey(child.Node.Key)) { treeEdges.Add(EdgeKey(branch.Node.Key, child.Node.Key)); Curve(branch, child, false); }
        foreach (var edge in graph.Edges) if (visibleMap.TryGetValue(edge.From, out var a) && visibleMap.TryGetValue(edge.To, out var b) && !treeEdges.Contains(EdgeKey(edge.From, edge.To))) Curve(a, b, true);
        var buttons = new Dictionary<string, Button>();
        updateQuickAppearance = () => { status.Text = quickNoteKey == null ? "实线为展开分支，虚线为交叉关联 · 右键笔记可快捷关联" : "快捷关联中：右键目标；左键任意处或右键起点退出"; foreach (var pair in buttons) pair.Value.BorderBrush = pair.Key == quickNoteKey ? Brush("#E1A34B") : Theme.Line; };
        foreach (var branch in visible)
        {
            var node = branch.Node; var color = node.Kind == "course" || node.Kind == "library" ? Green : node.Kind == "note" ? Brush("#8B79AF") : Brush("#B38A4D");
            var button = Button("", () => { if (node.Kind != "library") OpenNoteTarget(node); }); button.Content = Stack(Text(node.Label, 15, color, true), Text(node.Kind == "library" ? "全部笔记" : node.Kind == "note" ? "笔记" : node.Kind == "course" ? "整门课程" : "关联事项", 10, Muted)); button.Width = 210; button.MinHeight = 60; button.MaxHeight = 100; button.HorizontalContentAlignment = HorizontalAlignment.Left; button.ToolTip = node.Label; buttons[node.Key] = button;
            Canvas.SetLeft(button, branch.Position.X); Canvas.SetTop(button, branch.Position.Y - 30); canvas.Children.Add(button);
            button.PreviewMouseRightButtonDown += (_, e) =>
            {
                e.Handled = true; if (node.Kind == "library") return;
                if (quickNoteKey == node.Key) { ExitQuickAssociation(); return; }
                if (quickNoteKey == null) { if (node.Kind != "note") return; quickNoteKey = node.Key; quickNoteLibrary = library.Id; updateQuickAppearance?.Invoke(); return; }
                if (!node.Missing) Commit(() => NotesModel.Connect(store.Data, library, quickNoteKey, node));
            };
            if (branch.Children.Count > 0)
            {
                var toggle = Button(Collapsed(branch) ? "+" : "−", () => { string key = library.Id + ":" + node.Key; if (!mindCollapsed.Add(key)) mindCollapsed.Remove(key); Render(); }); toggle.Padding = new Thickness(7, 3, 7, 3); toggle.ToolTip = "展开/折叠 " + node.Label;
                Canvas.SetLeft(toggle, branch.Position.X + 215); Canvas.SetTop(toggle, branch.Position.Y - 10); canvas.Children.Add(toggle);
            }
        }
        updateQuickAppearance?.Invoke(); slider.ValueChanged += (_, _) => { scale.ScaleX = scale.ScaleY = slider.Value; };
        slider.Value = Math.Clamp(Math.Min(Math.Max(300, ActualWidth - 515) / canvas.Width, Math.Max(250, ActualHeight - 330) / canvas.Height), .35, 1);
        scroll.PreviewMouseWheel += (_, e) => { if (Keyboard.Modifiers != ModifierKeys.Control) return; slider.Value = Math.Clamp(slider.Value + (e.Delta > 0 ? .1 : -.1), .35, 1.6); e.Handled = true; };
        if (nodes.Count == 0) { var empty = Text("新建笔记并建立关联，即可展开思维导图。", 15, Muted); Canvas.SetLeft(empty, 300); Canvas.SetTop(empty, 100); canvas.Children.Add(empty); }
        if (include.Count > 240) status.Text = "当前展示前 240 个节点，请搜索缩小范围。";
        return new Border { BorderBrush = Theme.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Background = Brushes.White, Child = dock, ClipToBounds = true };
    }
}
