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
    public const int DurationMs = 500;
    public static bool Enabled { get; set; } = true;
    public static void Move(FrameworkElement element, double from, int ms = DurationMs)
    {
        if (!Enabled) return;
        var translate = new TranslateTransform(); element.RenderTransform = translate;
        translate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(from, 0, TimeSpan.FromMilliseconds(ms)) {
            EasingFunction = new BackEase { Amplitude = .35, EasingMode = EasingMode.EaseOut } });
    }
    public static void Enter(FrameworkElement element)
    {
        if (!Enabled) return;
        Move(element, 24, DurationMs);
        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(.12, 1, TimeSpan.FromMilliseconds(Motion.DurationMs)) { EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } });
    }
    public static void Press(Control control)
    {
        var scale = new ScaleTransform(1,1); control.RenderTransformOrigin = new Point(.5,.5); control.RenderTransform = scale;
        void Animate(double value) {
            if (!Enabled) return;
            foreach(var property in new[]{ScaleTransform.ScaleXProperty, ScaleTransform.ScaleYProperty})
                scale.BeginAnimation(property, new DoubleAnimation(value, TimeSpan.FromMilliseconds(Motion.DurationMs)) { EasingFunction = value < 1 ? new CubicEase { EasingMode=EasingMode.EaseOut } : new BackEase { Amplitude=.5, EasingMode=EasingMode.EaseOut } });
        }
        control.PreviewMouseLeftButtonDown += (_,_) => { if (!Enabled) return; scale.BeginAnimation(ScaleTransform.ScaleXProperty,null); scale.BeginAnimation(ScaleTransform.ScaleYProperty,null); scale.ScaleX=scale.ScaleY=.94; };
        control.PreviewMouseLeftButtonUp += (_,_) => Animate(1);
        control.MouseLeave += (_,_) => Animate(1);
    }
}
public sealed partial class MainWindow
{
    readonly Grid glassNavigation = new();
    readonly Border navGlass = GlassTheme.Selection();
    readonly TranslateTransform navGlassPosition = new();
    readonly Dictionary<string,Button> navButtons = new();
    string? selectedNav;
    Rect lastGlassBounds = Rect.Empty;
    FrameworkElement BuildGlassNavigation()
    {
        navGlass.RenderTransform=navGlassPosition;
        glassNavigation.Children.Add(navGlass); glassNavigation.Children.Add(navigation);
        navigation.LayoutUpdated += (_,_) => PositionNavGlass();
        return glassNavigation;
    }
    void PositionNavGlass()
    {
        if (!navButtons.TryGetValue(selectedNav ?? view,out var button) || !button.IsVisible || button.ActualHeight==0) {navGlass.Visibility=Visibility.Hidden;return;}
        var point=button.TranslatePoint(new Point(),glassNavigation);
        var bounds=new Rect(point,new Size(button.ActualWidth,button.ActualHeight));
        if(bounds==lastGlassBounds)return;
        bool animate=Motion.Enabled && navGlass.Visibility==Visibility.Visible;
        navGlass.Visibility=Visibility.Visible; lastGlassBounds=bounds;
        void Set(Animatable target,DependencyProperty property,double value) {
            if(animate)target.BeginAnimation(property,new DoubleAnimation(value,TimeSpan.FromMilliseconds(Motion.DurationMs)){EasingFunction=new CubicEase{EasingMode=EasingMode.EaseOut}});
            else {target.BeginAnimation(property,null);target.SetValue(property,value);}
        }
        Set(navGlassPosition,TranslateTransform.XProperty,point.X); Set(navGlassPosition,TranslateTransform.YProperty,point.Y);
        navGlass.Width=bounds.Width;navGlass.Height=bounds.Height;
    }
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
    readonly Dictionary<string, StackPanel> navBranches = new();
    readonly Dictionary<string, RotateTransform> navArrows = new();
    bool navigationNeedsRefresh;
    async Task AnimateBranch(string key, bool open)
    {
        if (!navBranches.TryGetValue(key, out var branch)) return;
        double from = branch.Visibility == Visibility.Visible ? branch.ActualHeight : 0;
        branch.Visibility=Visibility.Visible;
        branch.BeginAnimation(HeightProperty, null);
        branch.Height = double.NaN;
        branch.Measure(new Size(Math.Max(1, navigation.ActualWidth - branch.Margin.Left), double.PositiveInfinity));
        double target = open ? branch.DesiredSize.Height : 0;
        branch.Height = target;
        if (!Motion.Enabled) { branch.Height=open?double.NaN:0; branch.Opacity=open?1:0; branch.Visibility=open?Visibility.Visible:Visibility.Collapsed; if(navArrows.TryGetValue(key,out var instantArrow))instantArrow.Angle=open?90:0; return; }
        if(navArrows.TryGetValue(key,out var arrow))
            arrow.BeginAnimation(RotateTransform.AngleProperty,new DoubleAnimation(open ? 90 : 0,TimeSpan.FromMilliseconds(500)) { EasingFunction=new SineEase { EasingMode=EasingMode.EaseInOut } });
        var completed = new TaskCompletionSource<bool>();
        var animation = new DoubleAnimation(from,target,TimeSpan.FromMilliseconds(500)) { EasingFunction=new SineEase { EasingMode=EasingMode.EaseInOut } };
        animation.Completed += (_,_) => { branch.BeginAnimation(HeightProperty,null); branch.Height=open?double.NaN:0; branch.Visibility=open?Visibility.Visible:Visibility.Collapsed; completed.TrySetResult(true); };
        branch.BeginAnimation(HeightProperty,animation);
        branch.BeginAnimation(OpacityProperty,new DoubleAnimation(open?1:0,TimeSpan.FromMilliseconds(500)) { EasingFunction=new SineEase { EasingMode=EasingMode.EaseInOut } });
        await completed.Task;
    }
    async void ToggleNavigation(string group, bool task = false)
    {
        if (navigationAnimating) return;
        navigationAnimating=true; selectedNav=task?"待办清单":group; PositionNavGlass();
        try {
            if(task) {tasksExpanded=!tasksExpanded; await AnimateBranch("待办清单",tasksExpanded);}
            else {
                string? previous=expandedGroup;
                expandedGroup=previous==group?null:group;
                tasksExpanded=false;
                var transitions=new List<Task>();
                if(previous!=null)transitions.Add(AnimateBranch(previous,false));
                if(expandedGroup!=null)transitions.Add(AnimateBranch(expandedGroup,true));
                await Task.WhenAll(transitions);
                if(navBranches.TryGetValue("待办清单",out var nested)){nested.BeginAnimation(HeightProperty,null);nested.Height=0;nested.BeginAnimation(OpacityProperty,null);nested.Opacity=0;nested.Visibility=Visibility.Collapsed;}
                if(navArrows.TryGetValue("待办清单",out var arrow)){arrow.BeginAnimation(RotateTransform.AngleProperty,null);arrow.Angle=0;}
            }
        } finally {
            navigationAnimating=false;
            if(navigationNeedsRefresh){navigationNeedsRefresh=false;RenderNavigation();}
        }
    }
    void RenderNavigation()
    {
        if(navigationAnimating){navigationNeedsRefresh=true;return;}
        navigation.Children.Clear(); navBranches.Clear(); navArrows.Clear(); navButtons.Clear();
        Button Nav(string label, Action action, bool selected=false, bool? expanded=null)
        {
            var button=Button(label,action); navButtons[label=="知识笔记"?"笔记与附件":label]=button; button.HorizontalContentAlignment=HorizontalAlignment.Left;
            button.Padding=new Thickness(13,11,8,11); button.Margin=new Thickness(0,2,0,2);
            button.Background=Brushes.Transparent;
            button.Foreground=Brushes.White; button.BorderThickness=new Thickness(0);
            var caption=new StackPanel { Orientation=Orientation.Horizontal };
            string? icon=label switch { "日常安排"=>"calendar","学习记录"=>"book","知识笔记"=>"note","校园服务"=>"school","设置与备份"=>"settings",_=>null };
            if(icon!=null){var glyph=NoteEditorUi.Icon(icon,Brush("#E3EFE6"));glyph.Margin=new Thickness(0,0,10,0);caption.Children.Add(glyph);}
            caption.Children.Add(Text(label,13,Brush("#DCE9E2")));button.Content=caption;
            System.Windows.Automation.AutomationProperties.SetName(button,label);
            if(expanded.HasValue) {
                button.Content=null;
                var layout=new DockPanel(); var arrow=Text("›",17,Brush("#BDD1C5")); arrow.RenderTransformOrigin=new Point(.5,.5);
                arrow.RenderTransform=new RotateTransform(expanded.Value?90:0); DockPanel.SetDock(arrow,Dock.Right); layout.Children.Add(arrow); layout.Children.Add(caption);
                button.Content=layout;button.HorizontalContentAlignment=HorizontalAlignment.Stretch;
                navArrows[label]=(RotateTransform)arrow.RenderTransform;
                System.Windows.Automation.AutomationProperties.SetName(button,label+(expanded.Value?"，已展开":"，已折叠"));
            }
            button.MouseEnter+=(_,_)=>button.Background=Brush("#12FFFFFF");
            button.MouseLeave+=(_,_)=>button.Background=Brushes.Transparent;
            return button;
        }
        foreach(var group in new[]{"日常安排","学习记录","校园服务"}) {
            navigation.Children.Add(Nav(group,()=>ToggleNavigation(group),expanded:expandedGroup==group));

            var branch=new StackPanel { Margin=new Thickness(14,0,0,0),Tag="group-branch", Visibility=expandedGroup==group?Visibility.Visible:Visibility.Collapsed, ClipToBounds=true, Height=expandedGroup==group?double.NaN:0, Opacity=expandedGroup==group?1:0 }; navBranches[group]=branch;
            string[] children=group=="日常安排"?new[]{"今日概览","周课表","待办清单"}:group=="学习记录"?new[]{"笔记与附件"}:new[]{"校园助手","校园活动","冲突与提醒"};
            foreach(var child in children) {
                string target=child;
                branch.Children.Add(Nav(child=="笔记与附件"?"知识笔记":child,()=>{if(target=="待办清单")ToggleNavigation(group,true);else SwitchView(target);},view==target,child=="待办清单"?tasksExpanded:null));
                if(child=="待办清单") {
                    var tasks=new StackPanel { Margin=new Thickness(13,0,0,0),Tag="task-branch", Visibility=tasksExpanded?Visibility.Visible:Visibility.Collapsed, ClipToBounds=true, Height=tasksExpanded?double.NaN:0, Opacity=tasksExpanded?1:0 }; navBranches["待办清单"]=tasks;
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
            try {store.Save();} catch(Exception ex) {item.Done=before;box.IsChecked=before;UiMessage.Show(this,"未保存："+ex.Message);return;}
            title.TextDecorations=done?TextDecorations.Strikethrough:null;
            if(Motion.Enabled) {card.BeginAnimation(OpacityProperty,new DoubleAnimation(done?.55:1,TimeSpan.FromMilliseconds(Motion.DurationMs)) { EasingFunction=new SineEase { EasingMode=EasingMode.EaseInOut } }); await Task.Delay(Motion.DurationMs);}
            if(exiting || !card.IsLoaded)return;
            var positions=Visuals<FrameworkElement>(body).Where(e=>e.Tag is Item).ToDictionary(e=>((Item)e.Tag).Id,e=>e.TranslatePoint(new Point(),body).Y);
            var scroll=body.Content as ScrollViewer;double offset=scroll?.VerticalOffset??0;
            Render();UpdateLayout();
            if(body.Content is ScrollViewer next){next.ScrollToVerticalOffset(offset);next.UpdateLayout();}
            foreach(var element in Visuals<FrameworkElement>(body).Where(e=>e.Tag is Item))
                if(positions.TryGetValue(((Item)element.Tag).Id,out double old)) Motion.Move(element,Math.Clamp(old-element.TranslatePoint(new Point(),body).Y,-700,700),Motion.DurationMs);
        } finally {box.IsEnabled=true;changingTasks.Remove(item.Id);}
    }
}
