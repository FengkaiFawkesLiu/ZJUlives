using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using static Shike.Theme;

namespace Shike;
public sealed partial class MainWindow
{
    void RunInlinePanelTests(string dir, Action<bool,string> check, Action<Action,Action<AppPanel>> modal, Action<FrameworkElement,string> click)
    {
        Width=1330; Height=870; SwitchView("今日概览"); UpdateLayout();
        var item=new Item { Kind="课程",Title="线性代数（甲）",Location="紫金港东2-203",Teacher="汪国军",
            SourceKey="inline-fixture",Start=DateTime.Today.AddHours(10),End=DateTime.Today.AddHours(12).AddMinutes(25) };
        store.Data.Items.Add(item); store.Save(); Render(); UpdateLayout();
        double sideWidth=shell.ColumnDefinitions[0].ActualWidth, initialWidth=ActualWidth, initialHeight=ActualHeight;
        var initialSide=navigation.PointToScreen(new Point());
        bool stable=true; var widths=new System.Collections.Generic.HashSet<int>();
        var monitor=new DispatcherTimer { Interval=TimeSpan.FromMilliseconds(35) };
        monitor.Tick+=(_,_)=>{ stable &= ActualWidth==initialWidth && ActualHeight==initialHeight && shell.ColumnDefinitions[0].ActualWidth==sideWidth; widths.Add((int)Workspace.ColumnDefinitions[0].ActualWidth); };
        monitor.Start();
        modal(()=>Edit(new(item,item.Start,item.End)), panel=>{
            check(OwnedWindows.Count==0 && Application.Current.Windows.Count==1,"Course details use no additional native window");
            check(Workspace.Panels.Count==1 && panel.IsVisible,"Course details occupy the right content column");
            check(shell.ColumnDefinitions[0].ActualWidth==sideWidth && navigation.PointToScreen(new Point())==initialSide,"Sidebar position and width are unchanged");
            check(ActualWidth==initialWidth && ActualHeight==initialHeight,"Outer window dimensions stay fixed");
            check(Visuals<System.Windows.Controls.Canvas>(body).Any(c=>c.Height==1440),"Original timeline remains in the main column");
            var note=Visuals<TextBox>(panel).Single(); note.Text="预习向量空间与线性相关。";
            item.Location="同步更新后的教室"; WaitForPanelLayout();
            check(new Store(store.Root).Data.Items.Single(i=>i.Id==item.Id).Note==note.Text,"Course remarks save automatically without a Save button");
            check(item.Location=="同步更新后的教室" && !item.LocalOverride,"Remark autosave preserves updated school fields");
            Theme.Screenshot((FrameworkElement)Content,Path.Combine(dir,"课程详情分列.png"));
            Dispatcher.BeginInvoke(new Action(()=>{
                var confirm=Workspace.Active!; WaitForPanelLayout();
                check(Workspace.Panels.Count==2 && OwnedWindows.Count==0,"Deletion confirmation opens a second embedded column");
                check(ActualWidth==initialWidth && ActualHeight==initialHeight && shell.ColumnDefinitions[0].ActualWidth==sideWidth,"Nested confirmation keeps window and sidebar fixed");
                Theme.Screenshot((FrameworkElement)Content,Path.Combine(dir,"删除确认分列.png"));
                click(confirm,"保留课程");
            }),DispatcherPriority.ApplicationIdle);
            click(panel,"删除课程");
            check(!item.Cancelled && Workspace.Active==panel,"Back from confirmation restores the same editable details");
            note.Text="关闭前最后一次修改";
            panel.Close();
        });
        monitor.Stop();
        check(stable && widths.Count>3,"Intermediate animation frames preserve outer bounds and animate internal widths");
        check(Workspace.Panels.Count==0 && new Store(store.Root).Data.Items.Single(i=>i.Id==item.Id).Note=="关闭前最后一次修改","Closing flushes pending debounce and restores main column");
        var todo=new Item { Kind="待办",Title="微积分作业",Start=DateTime.Today.AddDays(2),End=DateTime.Today.AddDays(2).AddMinutes(1) };
        store.Data.Items.Add(todo);store.Save();
        modal(()=>Edit(new(todo,todo.Start,todo.End)), panel=>{
            Visuals<TextBox>(panel).Single(t=>t.AcceptsReturn).Text="先完成第一题";
            panel.Close();
        });
        check(new Store(store.Root).Data.Items.Single(i=>i.Id==todo.Id).Note=="先完成第一题","Task remarks also autosave on close");
        modal(()=>Edit(new(todo,todo.Start,todo.End)),panel=>{
            var title=Visuals<TextBox>(panel).First(t=>t.MaxLength==300); title.Text="尚未保存的新标题";
            Dispatcher.BeginInvoke(new Action(()=>{var confirmation=Workspace.Active!;WaitForPanelLayout();click(confirmation,"否");}),DispatcherPriority.ApplicationIdle);
            panel.Close();
            check(!panel.IsClosed && Workspace.Active==panel && title.Text=="尚未保存的新标题","Rejecting discard keeps the same unsaved form");
            title.Text=todo.Title;panel.Close();
        });
        modal(()=>Edit(new(item,item.Start,item.End)),panel=>{
            string previous=item.Note;
            using(var fileLock=new FileStream(Path.Combine(store.Root,"data.json.tmp"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None))
            {
                Visuals<TextBox>(panel).Single().Text="写入失败后重试的备注"; WaitForPanelLayout();
                check(item.Note==previous && Visuals<TextBlock>(panel).Any(t=>t.Text.StartsWith("备注未保存：")),"Autosave failure restores memory and exposes an inline error");
                Dispatcher.BeginInvoke(new Action(()=>{var confirmation=Workspace.Active!;WaitForPanelLayout();click(confirmation,"否");}),DispatcherPriority.ApplicationIdle);
                panel.Close();check(!panel.IsClosed,"Failed remark save cannot silently discard text on close");
            }
            panel.Close();
        });
        check(new Store(store.Root).Data.Items.Single(i=>i.Id==item.Id).Note=="写入失败后重试的备注","Closing retries and saves after the storage lock is released");
        modal(()=>UiMessage.Show(this,"测试提示","界面内提示"),panel=>{
            check(OwnedWindows.Count==0 && Workspace.Active==panel,"Messages use the same embedded host");
            click(panel,"确定");
        });
        Width=1060;UpdateLayout();
        modal(()=>Edit(new(item,item.Start,item.End)),panel=>{
            check(Workspace.ActualWidth>0 && panel.ActualWidth>0,"Details fit at minimum application width");
            Theme.Screenshot((FrameworkElement)Content,Path.Combine(dir,"最小宽度分列.png"));panel.Close();
        });
        Motion.Enabled=false;
        modal(()=>Edit(new(item,item.Start,item.End)),panel=>panel.Close());
        check(Workspace.Panels.Count==0,"Opening and closing work with animations disabled");
        Motion.Enabled=true;
    }
}
