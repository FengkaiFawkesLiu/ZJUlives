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
        SwitchView("笔记与附件");UpdateLayout();WaitForPanelLayout();
        var empty=Visuals<StackPanel>(body).Single(p=>p.Name=="GraphEmptyState");
        var emptyViewport=Visuals<GraphViewport>(body).Single();
        emptyViewport.ScrollToHorizontalOffset(3000);emptyViewport.ZoomAt(.05,new Point(100,100));UpdateLayout();
        var emptyOrigin=empty.TranslatePoint(new Point(),emptyViewport);
        check(emptyOrigin.X>=0&&emptyOrigin.Y>=0&&emptyOrigin.X+empty.ActualWidth<=emptyViewport.ActualWidth,"Empty state stays within viewport regardless of graph camera");
        var points = new Dictionary<string, Point> { ["a"] = new(350,350), ["b"] = new(550,300), ["c"] = new(550,500) };
        var engine = new GraphPhysics(points, new List<NoteEdge> { new("a","b"), new("a","c") },new[]{"a"});
        Dictionary<string,Point> Pair(double distance)=>new(){["root"]=new(0,0),["peer"]=new(distance,0)};
        var regular=Pair(100);var boosted=Pair(100);var twoRoots=Pair(100);
        new GraphPhysics(regular,new()).Step(1000,1000);
        new GraphPhysics(boosted,new(),new[]{"root"}).Step(1000,1000);
        new GraphPhysics(twoRoots,new(),new[]{"root","peer"}).Step(1000,1000);
        check(boosted["peer"].X>regular["peer"].X && boosted["peer"].X-regular["peer"].X<.14,"Root moderately increases repulsion at normal distance");
        check(boosted["peer"]==twoRoots["peer"],"Root-to-root repulsion does not compound");
        var closeNormal=Pair(1);var closeRoot=Pair(1);
        new GraphPhysics(closeNormal,new()).Step(1000,1000);new GraphPhysics(closeRoot,new(),new[]{"root"}).Step(1000,1000);
        check(closeNormal["peer"]==closeRoot["peer"],"Close-range force cap stays unchanged");
        var settledNormal=Pair(190);var settledRoot=Pair(190);var pairEdges=new List<NoteEdge>{new("root","peer")};
        var normalEngine=new GraphPhysics(settledNormal,pairEdges);var rootEngine=new GraphPhysics(settledRoot,pairEdges,new[]{"root"});double rootSpeed=0;
        for(int step=0;step<1200;step++){normalEngine.Step(1000,1000);rootSpeed=rootEngine.Step(1000,1000);}
        double normalGap=(settledNormal["peer"]-settledNormal["root"]).Length,rootGap=(settledRoot["peer"]-settledRoot["root"]).Length;
        check(rootSpeed<.035 && rootGap>normalGap && rootGap<normalGap*1.15,"Root spacing settles with bounded increase rather than scattering");
        engine.Held = "a"; engine.Move("a", new Point(180,200)); var before = points["b"];
        for (int i = 0; i < 90; i++) engine.Step(1150,820);
        check(points["a"] == new Point(180,200) && (points["b"] - before).Length > 20, "Dragged node stays held while connected nodes follow");
        engine.Held = null; double speed = 0;
        for (int i = 0; i < 1100; i++) speed = engine.Step(1150,820);
        check(speed < .035 && points.Values.All(p => double.IsFinite(p.X) && double.IsFinite(p.Y)), "Physics settles in unbounded world coordinates");
        points["a"] = points["b"]; for (int i=0;i<100;i++) engine.Step(1150,820);
        check(points.Values.All(p=>double.IsFinite(p.X) && double.IsFinite(p.Y)), "Overlapping nodes do not produce invalid coordinates");
        engine.Held="a";engine.Move("a",new Point(-18000,22000));engine.Step(1150,820);check(points["a"]==new Point(-18000,22000),"Dragging accepts negative positions and positions beyond former 10000 limit");
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
        var graphScroll = Visuals<GraphViewport>(body).Single();
        check(graphScroll.FocusVisualStyle == null, "No rectangular graph focus adorner");
        graphScroll.ScrollToHorizontalOffset(-20000);graphScroll.ScrollToVerticalOffset(22000);check(graphScroll.HorizontalOffset==-20000&&graphScroll.VerticalOffset==22000,"Camera pans beyond all viewport edges");graphScroll.Fit(new[]{new Point(-18000,-18000),new Point(18000,18000)});check(graphScroll.Zoom.ScaleX<.35,"Fit all supports large graphs below former minimum zoom");graphScroll.Fit(new[]{new Point(350,350),new Point(550,500)});
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
        library.Positions[NotesModel.Key(note)]=new(){X=-18000,Y=22000};store.Save();check(new Store(store.Root).Data.NoteLibraries.Single(l=>l.Id==library.Id).Positions[NotesModel.Key(note)].X==-18000,"Signed world coordinates survive local save and reload");
        library.Positions.Clear();
        var root=new Item { Kind="笔记",Title="窗口",LibraryId=library.Id };
        var hub=new Item { Kind="笔记",Title="窗口注册",LibraryId=library.Id,ParentId=root.Id };
        var children=new[]{"按钮创建","消息处理函数","窗口样式"}.Select(title=>new Item {Kind="笔记",Title=title,LibraryId=library.Id,ParentId=hub.Id}).ToArray();
        store.Data.Items.AddRange(new[]{root,hub}.Concat(children));
        root.NoteLinks.Add(new(){Key=NotesModel.Key(hub),Kind="note",Label=hub.Title});NotesModel.SyncReciprocal(store.Data,root);
        foreach(var child in children)hub.NoteLinks.Add(new(){Key=NotesModel.Key(child),Kind="note",Label=child.Title});
        NotesModel.SyncReciprocal(store.Data,hub);
        // The intermediate parent has more edges than the root, matching the reported case.
        SwitchView("笔记与附件");UpdateLayout();WaitForPanelLayout();
        Border Node(Item item)=>Visuals<Border>(body).First(b=>b.ToolTip is string text&&text.StartsWith(item.Title+"\n"));
        var rootDot=Visuals<Ellipse>(Node(root)).Single();
        check(rootDot.Width==42 && Visuals<TextBlock>(Node(root)).Single(t=>t.Text==root.Title).FontWeight==FontWeights.Bold,"Highest-level parent is larger and bold even with fewer edges");
        check(new[]{hub}.Concat(children).All(item=>Visuals<Ellipse>(Node(item)).Single().Width==30 && Visuals<TextBlock>(Node(item)).Single(t=>t.Text==item.Title).FontWeight==FontWeights.Normal),"Intermediate parent and leaves keep equal normal size and weight");
        check(Visuals<Ellipse>(Node(other)).Single().Width==30,"Unparented unrelated note keeps normal size");
        search.Text="按钮创建";UpdateLayout();WaitForPanelLayout();
        check(Visuals<Ellipse>(Node(hub)).Single().Width==30 && Visuals<TextBlock>(Node(hub)).Single(t=>t.Text==hub.Title).FontWeight==FontWeights.Normal,"Search hiding root does not promote intermediate parent");
        search.Text="";UpdateLayout();WaitForPanelLayout();
        Visuals<Button>(body).First(b=>Equals(b.Content,"显示全部")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));UpdateLayout();
        Theme.Screenshot((FrameworkElement)Content,Path.Combine(dir,"graph-hierarchy.png"));
        check(!Visuals<WrapPanel>(body).Any(p=>p.Name=="GraphHoverLegend") && !Visuals<TextBlock>(body).Any(t=>t.Text.Contains("点击打开 · 拖动节点或空白处") || t.Text.Contains("右键笔记节点，开始快捷关联")),"Removed graph legend and instructions are absent");
        quickNoteKey=NotesModel.Key(root);updateQuickAppearance?.Invoke();UpdateLayout();
        check(!Visuals<TextBlock>(body).Any(t=>t.Text.StartsWith("快捷关联：")),"Quick association works without instructional text");ExitQuickAssociation();

    }
}
