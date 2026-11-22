using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Shike;
public sealed partial class MainWindow
{
    void RunHoverUiTests(string directory,Action<bool,string> check)
    {
        void Pause(int milliseconds){var frame=new DispatcherFrame();var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(milliseconds)};timer.Tick+=(_,_)=>{timer.Stop();frame.Continue=false;};timer.Start();Dispatcher.PushFrame(frame);}
        void Enter(Button button)=>button.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice,Environment.TickCount){RoutedEvent=Mouse.MouseEnterEvent});
        void Leave(Button button)=>button.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice,Environment.TickCount){RoutedEvent=Mouse.MouseLeaveEvent});
        Button FindButton(string label)=>Visuals<Button>(this).First(b=>Equals(b.Content,label));
        Motion.Enabled=true;
        SwitchView("笔记与附件");UpdateLayout();Pause(600);
        var add=FindButton("＋ 笔记库");var settings=FindButton("库设置");
        var map=Visuals<Button>(this).First(b=>Equals(b.Content,"思维导图")||Equals(b.Content,"关系图谱"));
        double beforeWidth=add.ActualWidth;
        Enter(add);Pause(650);
        var layer=GlassHover.Surface(add);
        check(layer!=null && layer.IsLoaded && layer.Frame.Opacity>.99,"Action hover displays original glass component in real notes toolbar");
        check(Math.Abs(layer!.Frame.ActualWidth-add.ActualWidth)<.5 && Math.Abs(add.ActualWidth-beforeWidth)<.5,$"Hover frame fits button without changing layout (frame={layer.Frame.ActualWidth}, button={add.ActualWidth}, before={beforeWidth}, layer={layer.ActualWidth})");
        check(!((UIElement)VisualTreeHelper.GetParent(layer)).IsHitTestVisible,"Hover overlay never intercepts clicks");
        double start=((TranslateTransform)layer.Frame.RenderTransform).X;
        Leave(add);Enter(settings);Pause(170);
        double mid=((TranslateTransform)layer.Frame.RenderTransform).X;
        check(ReferenceEquals(layer,GlassHover.Surface(settings)) && mid>start && mid<settings.TranslatePoint(new Point(),layer).X,"Sibling hover shares one frame and interpolates position");
        Leave(settings);Enter(map);Pause(650);
        check(Math.Abs(((TranslateTransform)layer.Frame.RenderTransform).X-map.TranslatePoint(new Point(),layer).X)<.5,"Rapid hover retargets and settles on latest button");
        Theme.Screenshot(this,Path.Combine(directory,"hover-notes.png"));
        Leave(map);Pause(700);check(layer.Frame.Opacity<.01,"Leaving action fades glass out");
        Enter(map);Pause(650);check(layer.Frame.Opacity>.99,"Reentering same button restores glass after fade");
        map.IsEnabled=false;Pause(650);check(layer.Frame.Opacity<.01,"Disabling hovered button removes glass");
        Enter(map);Pause(100);check(layer.Frame.Opacity<.01,"Disabled action does not show hover glass");map.IsEnabled=true;
        var primary=FindButton("＋ 新建笔记");var originalBackground=primary.Background;
        Enter(primary);Pause(650);check(GlassHover.Surface(primary)!.Opacity<.5 && ReferenceEquals(originalBackground,primary.Background),$"Primary action uses lighter glass and keeps original color (opacity={GlassHover.Surface(primary)!.Opacity:F3}, subtle={GlassHover.GetIsSubtle(primary)}, sameBrush={ReferenceEquals(originalBackground,primary.Background)})");
        Leave(primary);
        foreach(var nav in navButtons.Values){Enter(nav);check(GlassHover.GetIsExcluded(nav)&&GlassHover.Surface(nav)==null,"Sidebar navigation excluded: "+System.Windows.Automation.AutomationProperties.GetName(nav));}
        var previousSurface=layer;
        assistantTab=1;SwitchView("校园助手");UpdateLayout();Pause(600);
        var tabs=Visuals<Button>(body).Where(b=>GlassHover.GetIsExcluded(b)).ToList();
        check(tabs.Count>=2,"Assistant tabs and both borrowing categories are explicitly excluded");
        foreach(var tab in tabs){Enter(tab);check(GlassHover.Surface(tab)==null,"Category hover leaves existing appearance: "+tab.Content);}
        var assistantTabs = Visuals<Button>(body).Where(b => new[] { "课程待办", "图书借阅", "课堂签到", "连接与设置" }.Contains(b.Content as string)).ToList();
        check(assistantTabs.Count == 4, "All four assistant tabs are present");
        foreach (var tab in assistantTabs) { Enter(tab); Pause(650); check(!GlassHover.GetIsExcluded(tab) && GlassHover.Surface(tab)?.Frame.Opacity > .99, "Assistant tab displays hover glass: " + tab.Content); Leave(tab); }        check(!previousSurface.IsLoaded,"Navigating away detaches previous hover overlay");
        // Isolated actions inside the real workspace cover subtle danger and animation-off behavior.
        var danger=Theme.Button("删除测试",()=>{});danger.Foreground=Theme.Brush("#A45248");
        int clicks=0;var ordinary=Theme.Button("测试操作",()=>clicks++);
        body.Content=Theme.Row(danger,ordinary);UpdateLayout();Pause(100);
        Enter(danger);Pause(650);check(GlassHover.Surface(danger)!.Opacity<.5,"Warning actions use lighter glass");
        Motion.Enabled=false;Leave(danger);Enter(ordinary);UpdateLayout();
        var immediate=GlassHover.Surface(ordinary)!;
        check(Math.Abs(((TranslateTransform)immediate.Frame.RenderTransform).X-ordinary.TranslatePoint(new Point(),immediate).X)<.5,"Animation preference off places hover immediately");
        ordinary.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));check(clicks==1,"Action click still executes once");
        Motion.Enabled=true;
    }
}
