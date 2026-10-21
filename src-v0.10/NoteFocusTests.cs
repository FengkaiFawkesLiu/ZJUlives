using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;

namespace Shike;
public sealed partial class MainWindow
{
    void RunNoteFocusTests(string dir,Action<bool,string> check,Action<Action,Action<AppPanel>> modal,Action<FrameworkElement,string> click)
    {
        Width=1330;Height=870;SwitchView("笔记与附件");UpdateLayout();
        WaitForPanelLayout();double sideWidth=shell.ColumnDefinitions[0].ActualWidth,windowWidth=ActualWidth;
        check(Visuals<System.Windows.Shapes.Path>(navigation).Count(p=>p.IsVisible)>=5,"Sidebar displays navigation line icons");
        var note=new Item { Kind="笔记",Title="救援训练",LibraryId=CurrentLibrary.Id,Note="把课堂上的方法，整理成自己能够复述的步骤。\n\n这次想记录的内容\n先梳理训练的过程，再补充观察与问题。" };
        store.Data.Items.Add(note);store.Save();
        modal(()=>EditKnowledgeNote(note),panel=>
        {
            check(panel.FocusWorkspace && panel.ActualWidth>Workspace.ActualWidth-55,"Note editor fills workspace beside sidebar");
            check(Math.Abs(shell.ColumnDefinitions[0].ActualWidth-sideWidth)<1 && Math.Abs(ActualWidth-windowWidth)<1,"Editor preserves sidebar and window dimensions");
            var rich=Visuals<RichTextBox>(panel).Single();var canvas=Visuals<NoteCanvas>(panel).Single();
            check(canvas.Scroll.Content is StackPanel document && document.Children.Count==2,"Title and body share one scrolling paper");
            check(Visuals<Button>(panel).Any(b=>Equals(b.Content,"笔记列表")) && Visuals<Button>(panel).Count(b=>new[]{"项目符号","撤销","重做"}.Contains(System.Windows.Automation.AutomationProperties.GetName(b)) && b.Content is System.Windows.Shapes.Path)==3,"Header actions and icon toolbar match preview structure");
            check(rich.FocusVisualStyle==null && rich.BorderThickness==new Thickness(0),"Writing surface has no rectangular focus outline");
            check(canvas.Scroll.HorizontalScrollBarVisibility==ScrollBarVisibility.Disabled,"Paper fits viewport without horizontal scrollbar");
            var format=Visuals<StackPanel>(panel).Single(p=>p.Name=="NoteFormatTools");var related=Visuals<Border>(panel).Single(p=>p.Name=="NoteRelations");
            check(format.Visibility==Visibility.Collapsed && related.Visibility==Visibility.Collapsed,"Formatting and relations start collapsed");
            Theme.Screenshot((FrameworkElement)Content,Path.Combine(dir,"note-focus.png"));
            Visuals<Button>(panel).Single(b=>System.Windows.Automation.AutomationProperties.GetName(b)=="排版").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));WaitForPanelLayout();check(format.Visibility==Visibility.Visible && !double.IsFinite(format.Height),"Formatting expands and releases animated height");
            var font=Visuals<ComboBox>(format).First();check(font.Parent is StackPanel row && row.Parent is Border,"Setting name and value share a framed group");
            font.IsDropDownOpen=true;WaitForPanelLayout();
            check(font.Template.FindName("menu",font) is Border menu && menu.CornerRadius.TopLeft==11,"Font dropdown uses rounded themed menu");
            Theme.Screenshot((FrameworkElement)Content,Path.Combine(dir,"note-format.png"));font.IsDropDownOpen=false;
            var width=Visuals<ComboBox>(format).Single(c=>c.Items.Contains("铺满"));double initialWidth=canvas.Paper.Width;width.SelectedIndex=1;WaitForPanelLayout();check(canvas.Paper.Width>initialWidth,"Wide layout expands the writing column");width.SelectedIndex=0;WaitForPanelLayout();
            Visuals<Button>(panel).Single(b=>System.Windows.Automation.AutomationProperties.GetName(b)=="排版").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));WaitForPanelLayout();check(format.Visibility==Visibility.Collapsed,"Formatting closes after transition");
            Visuals<Button>(panel).Single(b=>System.Windows.Automation.AutomationProperties.GetName(b).StartsWith("关联 ·")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));WaitForPanelLayout();check(related.Visibility==Visibility.Visible && rich.ActualWidth>500,"Relations overlay does not shrink writing surface");
            Theme.Screenshot((FrameworkElement)Content,Path.Combine(dir,"note-relations.png"));Visuals<Button>(panel).Single(b=>System.Windows.Automation.AutomationProperties.GetName(b).StartsWith("关联 ·")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));WaitForPanelLayout();
            Dispatcher.BeginInvoke(new Action(()=>{var child=Workspace.Active!;WaitForPanelLayout();check(Workspace.Panels.Count==2 && panel.IsVisible,"Nested tools retain focus editor underneath");click(child,"确定");}),DispatcherPriority.ApplicationIdle);
            UiMessage.Show(panel,"测试嵌套工具");check(Workspace.Active==panel && panel.ActualWidth>Workspace.ActualWidth-55,"Closing tool restores full-width editor");
            rich.Document.Blocks.Add(new Paragraph(new Run("保存测试内容")));click(panel,"保存");
        });
        check(Workspace.Panels.Count==0 && body.IsVisible,"Closing editor restores notes page");
        var saved=new Store(store.Root).Data.Items.Single(i=>i.Id==note.Id);check(saved.Note.Contains("保存测试内容"),"Rich note saves and reloads after focus editing");
        modal(()=>EditKnowledgeNote(saved),panel=>
        {
            var title=Visuals<TextBox>(panel).First(t=>t.MaxLength==300);title.Text="不应保存的标题";
            Dispatcher.BeginInvoke(new Action(()=>{var confirm=Workspace.Active!;WaitForPanelLayout();click(confirm,"否");}),DispatcherPriority.ApplicationIdle);
            panel.Close();check(!panel.IsClosed && Workspace.Active==panel,"Rejecting discard preserves editor and draft");
            panel.Tag="test-abort";panel.Close();
        });
        Width=1060;UpdateLayout();modal(()=>EditKnowledgeNote(saved),panel=>{check(panel.ActualWidth>750,"Focus editor remains usable at minimum window width");Theme.Screenshot((FrameworkElement)Content,Path.Combine(dir,"note-focus-narrow.png"));panel.Close();});
        Motion.Enabled=false;modal(()=>EditKnowledgeNote(saved),panel=>{Visuals<Button>(panel).Single(b=>System.Windows.Automation.AutomationProperties.GetName(b)=="排版").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));check(Visuals<StackPanel>(panel).Single(p=>p.Name=="NoteFormatTools").Visibility==Visibility.Visible,"Formatting works with animations disabled");panel.Close();});Motion.Enabled=true;
    }
}

