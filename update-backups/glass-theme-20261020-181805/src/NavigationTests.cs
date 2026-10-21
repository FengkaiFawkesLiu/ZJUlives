using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using static Shike.Theme;
namespace Shike;
public sealed partial class MainWindow
{
    void RunNavigationTests(string dir, Action<bool,string> check)
    {
        void Wait(int ms) {
            var frame=new DispatcherFrame();var timer=new DispatcherTimer { Interval=TimeSpan.FromMilliseconds(ms) };
            timer.Tick+=(_,_)=>{timer.Stop();frame.Continue=false;};timer.Start();Dispatcher.PushFrame(frame);UpdateLayout();
        }
        check(expandedGroup==null && navigation.Children.OfType<Button>().Count()==4,"Startup shows only four top-level navigation entries");
        var original=body.Content;ToggleNavigation("日常安排");Wait(150);
        double middle=navBranches["日常安排"].Height; check(navigationAnimating && middle>0,"Group visibly animates at 150ms"); Wait(600); check(navBranches["日常安排"].ActualHeight>middle,$"Group expands through full half-second transition ({middle} -> {navBranches["日常安排"].ActualHeight})");
        check(expandedGroup=="日常安排" && ReferenceEquals(original,body.Content),"Opening group keeps current page");
        ToggleNavigation("日常安排",true);Wait(150); check(navigationAnimating && navBranches["待办清单"].ActualHeight>0,"Nested task directory visibly animates"); Wait(600);
        check(tasksExpanded && ReferenceEquals(original,body.Content),"Task parent expands three categories without opening page");
        ToggleNavigation("学习记录");Wait(150); check(navBranches["日常安排"].ActualHeight>0 && navBranches["学习记录"].ActualHeight>0,"Old branch collapses while new branch expands"); Wait(600);
        check(expandedGroup=="学习记录" && !tasksExpanded && ReferenceEquals(original,body.Content),"Only one first-level branch remains expanded");
        var manual=new Item {Kind="待办",Title="手动分类作业",TaskCategory="作业",Start=DateTime.Today.AddHours(10),End=DateTime.Today.AddHours(10).AddMinutes(1)};
        var remote=new Item {Kind="待办",Title="学校作业",AssistantKey="courses:fixture:navigation",Start=DateTime.Today.AddHours(12),End=DateTime.Today.AddHours(12).AddMinutes(1)};
        var other=new Item {Kind="待办",Title="原有待办"};
        var exam=new Item {Kind="考试",Title="线性代数考试",Start=DateTime.Today.AddDays(2),End=DateTime.Today.AddDays(2).AddHours(2)};
        store.Data.Items.AddRange(new[]{manual,remote,other,exam});store.Save();
        check(TaskCategories.Of(remote)=="作业" && TaskCategories.Of(other)=="其他" && TaskCategories.Of(new Item {AssistantKey="pintia:x"})=="作业","Remote homework classified automatically and old manual tasks remain Other");
        SwitchView("作业");Wait(2200);
        check(TaskCategories.Items(store.Data,"作业").Count()==2 && TaskCategories.Items(store.Data,"考试").Single().Id==exam.Id,"Homework and exams have independent category lists");
        var card=Visuals<FrameworkElement>(body).Single(e=>ReferenceEquals(e.Tag,manual));
        var box=Visuals<CheckBox>(card).Single();box.IsChecked=true;box.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));Wait(4300);
        check(manual.Done && new Store(store.Root).Data.Items.Single(i=>i.Id==manual.Id).Done,"Completion persists before animation");
        check(TaskCategories.Items(store.Data,"作业").Last().Id==manual.Id,"Completed homework sinks below unfinished homework");
        Theme.Screenshot((FrameworkElement)Content,Path.Combine(dir,"作业完成下沉.png"));
        card=Visuals<FrameworkElement>(body).Single(e=>ReferenceEquals(e.Tag,manual));box=Visuals<CheckBox>(card).Single();
        box.IsChecked=false;box.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));Wait(4300);
        check(!manual.Done && TaskCategories.Items(store.Data,"作业").First().Id==manual.Id,"Unchecking restores homework deadline order");
        SwitchView("考试");Wait(2200);check(!Visuals<CheckBox>(body).Any(),"Exam cards do not expose task completion checkbox");
        Theme.Screenshot((FrameworkElement)Content,Path.Combine(dir,"考试子目录.png"));
        SwitchView("今日概览");Wait(2200);
        check(Visuals<Canvas>(body).Any(c=>c.Height==1440),"Overview preserves original hourly timeline canvas");
        Theme.Screenshot((FrameworkElement)Content,Path.Combine(dir,"折叠导航与原时间轴.png"));
        Width=1060;UpdateLayout();Wait(2200);Theme.Screenshot((FrameworkElement)Content,Path.Combine(dir,"最小宽度.png"));
        check(OwnedWindows.Count==0,"Navigation and completion open no additional windows");
    }
}
