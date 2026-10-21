using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Path = System.IO.Path;

namespace Shike;
public sealed partial class MainWindow
{
    void RunGraphGlassTests(string dir, Action<bool, string> check)
    {
        var points = new Dictionary<string, Point> { ["a"] = new(350,350), ["b"] = new(550,300), ["c"] = new(550,500) };
        var engine = new GraphPhysics(points, new List<NoteEdge> { new("a","b"), new("a","c") });
        engine.Held = "a"; engine.Move("a", new Point(180,200)); var before = points["b"];
        for (int i = 0; i < 90; i++) engine.Step(1150,820);
        check(points["a"] == new Point(180,200) && (points["b"] - before).Length > 20, "Dragged node stays held while connected nodes follow");
        engine.Held = null; double speed = 0;
        for (int i = 0; i < 1100; i++) speed = engine.Step(1150,820);
        check(speed < .035 && points.Values.All(p => double.IsFinite(p.X) && double.IsFinite(p.Y) && p.X >= 85 && p.X <= 1065 && p.Y >= 45 && p.Y <= 740), "Physics settles within canvas bounds");
        points["a"] = points["b"]; for (int i=0;i<100;i++) engine.Step(1150,820);
        check(points.Values.All(p=>double.IsFinite(p.X) && double.IsFinite(p.Y)), "Overlapping nodes do not produce invalid coordinates");
        var library = CurrentLibrary;
        var note = new Item { Kind="笔记", Title="救援训练", LibraryId=library.Id };
        var other = new Item { Kind="笔记", Title="测试链接", LibraryId=library.Id };
        var course = new Item { Kind="课程", Title="皮划艇（初级）" };
        note.NoteLinks.Add(new() { Key=NotesModel.Key(other), Kind="note", Label=other.Title });
        note.NoteLinks.Add(new() { Key=NotesModel.Key(course), Kind="course", Label=course.Title });
        store.Data.Items.AddRange(new[] {note,other,course}); store.Save();
        SwitchView("笔记与附件"); UpdateLayout(); WaitForPanelLayout(); UpdateLayout();
        var node = Visuals<Border>(body).First(b=>b.ToolTip is string t && t.StartsWith(note.Title+"\n"));
        var dot = Visuals<Ellipse>(node).Single(); var original = dot.Fill;
        check(original is SolidColorBrush && dot.Effect == null, "Nodes start as solid colors without default glow");
        var canvas = Visuals<Canvas>(body).First(c=>c.Children.OfType<Line>().Any());
        check(canvas.Background == Brushes.Transparent, "Graph canvas is transparent");
        check(!Visuals<TextBlock>(body).Any(t=>t.Text.Contains("布局已保存")), "No lower-left layout status text");
        var graphScroll = Visuals<ScrollViewer>(body).First(s=>s.Content==canvas);
        check(graphScroll.FocusVisualStyle == null, "No rectangular graph focus adorner");
        Theme.Screenshot((FrameworkElement)Content,Path.Combine(dir,"graph-glass.png"));
        node.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice,0,MouseButton.Left) { RoutedEvent=UIElement.MouseLeftButtonDownEvent });
        check(dot.Fill is RadialGradientBrush, "Press shows colored transparent glass node");
        dot.InvalidateVisual(); UpdateLayout();
        Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Render);
        Theme.Screenshot((FrameworkElement)Content,Path.Combine(dir,"graph-glass-pressed.png"));
        node.ReleaseMouseCapture();
        check(ReferenceEquals(dot.Fill,original) && dot.Effect == null, "Capture release restores original node color and clears glow");
        var pressed = new Ellipse(); GraphGlass.Press(pressed,original,true); GraphGlass.Press(pressed,original,false);
        check(ReferenceEquals(pressed.Fill, original), "Glass state is reversible");
        var slider = Visuals<Slider>(body).First(); slider.Value = .8; slider.UpdateLayout();
        check(slider.Template.FindName("PART_Track",slider) != null, "Shared glass slider retains its functional track");
        var save = Visuals<Button>(body).First(b=>(b.Content as string)=="保存布局"); save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var reloaded = new Store(store.Root);
        check(reloaded.Data.NoteLibraries.Single(l=>l.Id==library.Id).Positions.Count==3,"Graph positions survive save and reload");
    }
}
