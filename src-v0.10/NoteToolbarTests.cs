using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
namespace Shike;
public sealed partial class MainWindow
{
    void RunNoteToolbarTests(string dir,Action<bool,string> check,Action<Action,Action<AppPanel>> modal)
    {
        void Pause(int ms){var f=new DispatcherFrame();var t=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(ms)};t.Tick+=(_,_)=>{t.Stop();f.Continue=false;};t.Start();Dispatcher.PushFrame(f);}
        Motion.Enabled=true;SwitchView("笔记与附件");
        modal(()=>EditKnowledgeNote(null),panel=>{
            var format=Visuals<StackPanel>(panel).Single(x=>x.Name=="NoteFormatTools");
            var button=Visuals<Button>(panel).Single(x=>System.Windows.Automation.AutomationProperties.GetName(x)=="排版");
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Pause(120);panel.UpdateLayout();double mid=((RectangleGeometry)format.Clip).Rect.Height;
            var arrow=Visuals<System.Windows.Shapes.Path>(button).First(x=>x.RenderTransform is RotateTransform);double angle=((RotateTransform)arrow.RenderTransform).Angle;
            Pause(500);File.WriteAllText(Path.Combine(dir,"motion-values.txt"),$"mid={mid}, end={format.ActualHeight}, angle={angle}, enabled={Motion.Enabled}, desired={format.DesiredSize}");check(mid>0&&mid<format.ActualHeight,"Formatting visible clip has intermediate animation frame");check(angle>0&&angle<90&&Math.Abs(((RotateTransform)arrow.RenderTransform).Angle-90)<.1,"First click rotates arrow progressively to 90 degrees");
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Pause(100);button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Pause(600);check(format.Visibility==Visibility.Visible&&double.IsNaN(format.Height),"Rapid close/reopen settles in expanded auto layout");
            var insert=Visuals<Button>(panel).Single(x=>System.Windows.Automation.AutomationProperties.GetName(x)=="插入");insert.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Pause(120);check(((RotateTransform)arrow.RenderTransform).Angle>0&&((RotateTransform)arrow.RenderTransform).Angle<90,"Opening sibling starts previous arrow reset animation");Pause(500);check(format.Visibility==Visibility.Collapsed&&Math.Abs(((RotateTransform)arrow.RenderTransform).Angle)<.1,"Opening Insert collapses Formatting and resets its arrow");button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Pause(600);check(Visuals<WrapPanel>(panel).Single(x=>x.Name=="NoteInsertTools").Visibility==Visibility.Collapsed,"Returning to Formatting collapses Insert");
            var combo=Visuals<ComboBox>(format).First();combo.IsDropDownOpen=true;Pause(150);var a=(FrameworkElement)combo.Template.FindName("arrow",combo);check(a.RenderTransform is RotateTransform rt&&rt.Angle>0&&rt.Angle<90,"Picker first click animates its arrow");Pause(450);check(combo.Template.FindName("menu",combo) is Border m&&m.Child is GlassSelectionLayer,"Dropdown uses shared measured glass layer");combo.IsDropDownOpen=false;
            var canvas=Visuals<NoteCanvas>(panel).Single();check(canvas.Paper.HorizontalAlignment==HorizontalAlignment.Left,"Paper is left aligned");
            Theme.Screenshot((FrameworkElement)Content,Path.Combine(dir,"toolbar-native.png"));panel.Tag="test-abort";panel.Close();
        });
        modal(()=>EditKnowledgeNote(null,true),panel=>{
            var md=Visuals<MarkdownEditor>(panel).Single();check(md.Source.IsVisible&&!md.Preview.IsVisible,"Markdown starts in edit-only mode");
            var more=Visuals<Button>(panel).Single(b=>System.Windows.Automation.AutomationProperties.GetName(b)=="更多");more.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Pause(600);
            check(Visuals<Button>(panel).Any(b=>Equals(b.Content,"数学公式")&&b.IsVisible),"Math remains available in More");
            md.Source.Text="# 保留原文\n\n**测试**";var title=Visuals<TextBox>(panel).First(x=>x.MaxLength==300);title.Text="工具栏保存测试";
            Theme.Screenshot((FrameworkElement)Content,Path.Combine(dir,"markdown-native.png"));Visuals<Button>(panel).First(b=>Equals(b.Content,"保存")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        });
        check(store.Data.Items.Any(i=>i.Title=="工具栏保存测试"&&i.NoteBody=="# 保留原文\n\n**测试**"),"Markdown saves original source");
    }
}
