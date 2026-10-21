using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using static Shike.Theme;

namespace Shike;
public static class TaskCategories
{
    public static string Of(Item item) => item.Kind == "考试" ? "考试" :
        item.AssistantKey?.StartsWith("courses:") == true || item.AssistantKey?.StartsWith("pintia:") == true ? "作业" :
        item.TaskCategory == "作业" ? "作业" : "其他";
    public static IEnumerable<Item> Items(AppData data, string category) => data.Items
        .Where(i => !i.Cancelled && (category == "考试" ? i.Kind == "考试" : i.Kind == "待办" && Of(i) == category))
        .OrderBy(i => category != "考试" && i.Done).ThenBy(i => i.AssistantNoDue ? DateTime.MaxValue : i.Start);
}
public static class Motion
{
    public static bool Enabled => SystemParameters.ClientAreaAnimation;
    public static void Move(FrameworkElement element, double from, int ms = 520)
    {
        if (!Enabled) return;
        var translate = new TranslateTransform(); element.RenderTransform = translate;
        translate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(from, 0, TimeSpan.FromMilliseconds(ms)) {
            EasingFunction = new BackEase { Amplitude = .35, EasingMode = EasingMode.EaseOut } });
    }
    public static void Enter(FrameworkElement element)
    {
        if (!Enabled) return;
        Move(element, 18, 520);
        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(.12, 1, TimeSpan.FromMilliseconds(420)) { EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } });
    }
    public static void Press(Control control)
    {
        var scale = new ScaleTransform(1,1); control.RenderTransformOrigin = new Point(.5,.5); control.RenderTransform = scale;
        void Animate(double value) {
            if (!Enabled) return;
            foreach(var property in new[]{ScaleTransform.ScaleXProperty, ScaleTransform.ScaleYProperty})
                scale.BeginAnimation(property, new DoubleAnimation(value, TimeSpan.FromMilliseconds(value < 1 ? 100 : 380)) { EasingFunction = value < 1 ? new CubicEase { EasingMode=EasingMode.EaseOut } : new BackEase { Amplitude=.5, EasingMode=EasingMode.EaseOut } });
        }
        control.PreviewMouseLeftButtonDown += (_,_) => Animate(.96);
        control.PreviewMouseLeftButtonUp += (_,_) => Animate(1);
        control.MouseLeave += (_,_) => Animate(1);
    }
}
public sealed partial class MainWindow
{
    static Style CompletionStyle() => (Style)System.Windows.Markup.XamlReader.Parse("""
        <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="CheckBox">
          <Setter Property="Focusable" Value="True"/>
          <Setter Property="Template"><Setter.Value>
            <ControlTemplate TargetType="CheckBox">
              <Grid Width="32" Height="32" Background="Transparent">
                <Border x:Name="Square" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" Width="21" Height="21" CornerRadius="5" BorderThickness="1.5" BorderBrush="#698675" Background="#F8FAF5">
                  <TextBlock x:Name="Tick" Text="✓" Foreground="White" FontSize="16" HorizontalAlignment="Center" VerticalAlignment="Center" Visibility="Collapsed"/>
                </Border>
              </Grid>
              <ControlTemplate.Triggers>
                <Trigger Property="IsChecked" Value="True"><Setter TargetName="Square" Property="Background" Value="#28725B"/><Setter TargetName="Tick" Property="Visibility" Value="Visible"/></Trigger>
                <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="Square" Property="BorderBrush" Value="#C08030"/><Setter TargetName="Square" Property="BorderThickness" Value="2"/></Trigger>
                <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Square" Property="BorderBrush" Value="#28725B"/></Trigger>
              </ControlTemplate.Triggers>
            </ControlTemplate>
          </Setter.Value></Setter>
        </Style>
        """);
    string? expandedGroup;
    bool tasksExpanded, navigationAnimating;
    readonly HashSet<string> changingTasks = new();
    static string GroupOf(string page) => page is "今日概览" or "周课表" or "作业" or "考试" or "其他" ? "日常安排" :
        page == "笔记与附件" ? "学习记录" : page == "设置与备份" ? "" : "校园服务";
    async void ToggleNavigation(string group, bool task = false)
    {
        if (navigationAnimating) return;
        navigationAnimating = true;
        try {
            // Animate only the existing branch; the main page remains untouched.
            var branches = Visuals<StackPanel>(navigation).Where(p => Equals(p.Tag, task ? "task-branch" : "group-branch")).ToList();
            if (Motion.Enabled && branches.Count > 0) {
                foreach(var branch in branches) { branch.ClipToBounds=true; branch.BeginAnimation(HeightProperty,new DoubleAnimation(branch.ActualHeight,0,TimeSpan.FromMilliseconds(280)) { EasingFunction=new SineEase { EasingMode=EasingMode.EaseInOut } }); }
                await Task.Delay(290);
            }
            if (task) tasksExpanded = !tasksExpanded;
            else { expandedGroup = expandedGroup == group ? null : group; tasksExpanded = false; }
            RenderNavigation();
            if (Motion.Enabled) {
                navigation.UpdateLayout();
                foreach(var branch in Visuals<StackPanel>(navigation).Where(p=>Equals(p.Tag,task?"task-branch":"group-branch"))) {
                    double height=branch.ActualHeight; branch.ClipToBounds=true;
                    var animation=new DoubleAnimation(0,height,TimeSpan.FromMilliseconds(480)) { EasingFunction=new BackEase { Amplitude=.3,EasingMode=EasingMode.EaseOut } };
                    animation.Completed+=(_,_)=>branch.BeginAnimation(HeightProperty,null);
                    branch.BeginAnimation(HeightProperty,animation);
                }
            }
        } finally { navigationAnimating=false; }
    }
    void RenderNavigation()
    {
        navigation.Children.Clear();
        Button Nav(string label, Action action, bool selected=false, bool? expanded=null)
        {
            var button=Button(label,action); button.HorizontalContentAlignment=HorizontalAlignment.Left;
            button.Padding=new Thickness(13,11,8,11); button.Margin=new Thickness(0,2,0,2);
            button.Background=selected?Brush("#355D4B"):Brushes.Transparent;
            button.Foreground=selected?Brushes.White:Brush("#BDD1C5"); button.BorderThickness=new Thickness(selected?3:0,0,0,0); button.BorderBrush=Brush("#B8D8B0");
            if(expanded.HasValue) {
                var layout=new DockPanel(); var arrow=Text("›",17,Brush("#BDD1C5")); arrow.RenderTransformOrigin=new Point(.5,.5);
                arrow.RenderTransform=new RotateTransform(expanded.Value?90:0); DockPanel.SetDock(arrow,Dock.Right); layout.Children.Add(arrow); layout.Children.Add(Text(label,13,Brush("#DCE9E2")));
                button.Content=layout;button.HorizontalContentAlignment=HorizontalAlignment.Stretch;
                if(Motion.Enabled) ((RotateTransform)arrow.RenderTransform).BeginAnimation(RotateTransform.AngleProperty,new DoubleAnimation(expanded.Value?0:90,expanded.Value?90:0,TimeSpan.FromMilliseconds(420)) { EasingFunction=new SineEase { EasingMode=EasingMode.EaseInOut } });
                System.Windows.Automation.AutomationProperties.SetName(button,label+(expanded.Value?"，已展开":"，已折叠"));
            }
            button.MouseEnter+=(_,_)=>button.Background=Brush(selected?"#406A56":"#284D40");
            button.MouseLeave+=(_,_)=>button.Background=selected?Brush("#355D4B"):Brushes.Transparent;
            return button;
        }
        foreach(var group in new[]{"日常安排","学习记录","校园服务"}) {
            navigation.Children.Add(Nav(group,()=>ToggleNavigation(group),expanded:expandedGroup==group));
            if(expandedGroup!=group)continue;
            var branch=new StackPanel { Margin=new Thickness(14,3,0,12),Tag="group-branch" };
            string[] children=group=="日常安排"?new[]{"今日概览","周课表","待办清单"}:group=="学习记录"?new[]{"笔记与附件"}:new[]{"校园助手","校园活动","冲突与提醒"};
            foreach(var child in children) {
                string target=child;
                branch.Children.Add(Nav(child=="笔记与附件"?"知识笔记":child,()=>{if(target=="待办清单")ToggleNavigation(group,true);else SwitchView(target);},view==target,child=="待办清单"?tasksExpanded:null));
                if(child=="待办清单"&&tasksExpanded) {
                    var tasks=new StackPanel { Margin=new Thickness(13,0,0,4),Tag="task-branch" };
                    foreach(var category in new[]{"作业","考试","其他"}) {string selected=category;tasks.Children.Add(Nav(category,()=>SwitchView(selected),view==category));}
                    branch.Children.Add(tasks);
                }
            }
            navigation.Children.Add(branch);
        }
        navigation.Children.Add(Space(18));
        navigation.Children.Add(Nav("设置与备份",()=>SwitchView("设置与备份"),view=="设置与备份"));
    }
    async void CompleteTask(Item item, CheckBox box, FrameworkElement card, TextBlock title)
    {
        if (!changingTasks.Add(item.Id)) return;
        box.IsEnabled=false;
        bool before=item.Done, done=box.IsChecked==true;
        try {
            item.Done=done; item.Modified=DateTime.Now;
            try {store.Save();} catch(Exception ex) {item.Done=before;box.IsChecked=before;MessageBox.Show(this,"未保存："+ex.Message);return;}
            title.TextDecorations=done?TextDecorations.Strikethrough:null;
            if(Motion.Enabled) {card.BeginAnimation(OpacityProperty,new DoubleAnimation(done?.55:1,TimeSpan.FromMilliseconds(260)) { EasingFunction=new SineEase { EasingMode=EasingMode.EaseInOut } }); await Task.Delay(300);}
            if(exiting || !card.IsLoaded)return;
            var positions=Visuals<FrameworkElement>(body).Where(e=>e.Tag is Item).ToDictionary(e=>((Item)e.Tag).Id,e=>e.TranslatePoint(new Point(),body).Y);
            var scroll=body.Content as ScrollViewer;double offset=scroll?.VerticalOffset??0;
            Render();UpdateLayout();
            if(body.Content is ScrollViewer next){next.ScrollToVerticalOffset(offset);next.UpdateLayout();}
            foreach(var element in Visuals<FrameworkElement>(body).Where(e=>e.Tag is Item))
                if(positions.TryGetValue(((Item)element.Tag).Id,out double old)) Motion.Move(element,Math.Clamp(old-element.TranslatePoint(new Point(),body).Y,-700,700),600);
        } finally {box.IsEnabled=true;changingTasks.Remove(item.Id);}
    }
}
