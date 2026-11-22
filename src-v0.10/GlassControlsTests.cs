using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Shike;
public static class GlassControlsTests
{
    static IEnumerable<T> Find<T>(DependencyObject root) where T:DependencyObject
    {
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)
        {var child=VisualTreeHelper.GetChild(root,i);if(child is T found)yield return found;foreach(var nested in Find<T>(child))yield return nested;}
    }
    public static int Run(Application app,string directory)
    {
        Directory.CreateDirectory(directory);var results=new List<string>();int exit=0;
        void Check(bool ok,string message){results.Add((ok?"PASS ":"FAIL ")+message);if(!ok)throw new Exception(message);}
        var date=new DatePicker{SelectedDate=new DateTime(2026,10,25),Width=235};
        var combo=Theme.Combo(new[]{"课程","考试","待办","个人活动"},"待办");combo.Width=240;
        var pass=new PasswordBox{Password="preview-only",Width=240};
        var check=new CheckBox{Content="在本机记住账号",IsChecked=true};
        var calendar=new Calendar{SelectedDate=new DateTime(2026,10,25),DisplayDate=new DateTime(2026,10,25)};
        var content=new StackPanel{Margin=new Thickness(28)};
        content.Children.Add(Theme.Text("拾课 · 统一主题验证",24,bold:true));content.Children.Add(Theme.Space(18));
        content.Children.Add(Theme.Card(Theme.Stack(Theme.Field("日期",date),Theme.Field("类型",combo),Theme.Field("密码",pass),check)));
        content.Children.Add(calendar);content.Children.Add(new TimeWheel(new DateTime(2026,10,25,14,30,0)));
        var window=new Window{Title="拾课主题验证",Width=700,Height=920,Content=new Border{Background=GlassTheme.Backdrop,Child=content}};
        window.Loaded+=async(_,_)=>
        {
            try
            {
                await Task.Delay(700);
                Check(check.Template.FindName("box",check) is Border,"Checkbox uses rounded theme template");
                Check(pass.Template.FindName("PART_ContentHost",pass) is ScrollViewer,"Password editing host preserved");
                var text=(DatePickerTextBox)date.Template.FindName("PART_TextBox",date);
                Check(text.Template.FindName("PART_ContentHost",text) is ScrollViewer,"Date textbox uses custom editing template");
                date.Text="2026/10/26";date.IsDropDownOpen=true;await Task.Delay(650);
                Check(date.SelectedDate==new DateTime(2026,10,26),"Typed date commits when calendar opens");
                var popup=(Popup)date.Template.FindName("PART_Popup",date);
                Check(popup.IsOpen && popup.Child!=null,"Date popup opens");
                var popupCalendar=popup.Child as Calendar ?? Find<Calendar>(popup.Child!).First();
                Check(Find<GlassSelectionLayer>(popupCalendar).Any(),"Date popup reuses original glass selection component");
                Theme.Screenshot(popupCalendar,Path.Combine(directory,"date-popup.png"));
                popupCalendar.SelectedDate=new DateTime(2026,10,28);await Task.Delay(100);
                Check(date.SelectedDate==new DateTime(2026,10,28),"Calendar selection updates date input");
                date.IsDropDownOpen=false;
                var layer=Find<GlassSelectionLayer>(calendar).Single();
                Check(Find<TextBlock>(calendar).Count(b=>b.Text.Length==1 && "一二三四五六日".Contains(b.Text))>=7,"Calendar weekday headings remain visible");
                var initial=((TranslateTransform)layer.Frame.RenderTransform).X;
                calendar.SelectedDate=new DateTime(2026,10,19);await Task.Delay(170);
                var mid=((TranslateTransform)layer.Frame.RenderTransform).X;
                Check(mid>0 && mid<initial,"Calendar glass interpolates during 500 ms movement");
                calendar.SelectedDate=new DateTime(2026,10,23);await Task.Delay(650);
                var selected=Find<CalendarDayButton>(calendar).Single(b=>b.IsSelected&&!b.IsInactive);
                Check(Math.Abs(((TranslateTransform)layer.Frame.RenderTransform).X-selected.TranslatePoint(new Point(),layer).X)<.5,"Calendar glass retargets to latest date");
                calendar.DisplayMode=CalendarMode.Year;await Task.Delay(100);
                Check(layer.Visibility!=Visibility.Visible,"Month selection hides in year mode");
                Check(Find<CalendarButton>(calendar).Count(b=>b.IsVisible)>=12,"Year mode exposes month choices");
                calendar.DisplayMode=CalendarMode.Month;await Task.Delay(650);
                var calendarItem=Find<CalendarItem>(calendar).Single();
                ((Button)calendarItem.Template.FindName("PART_NextButton",calendarItem)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Task.Delay(100);
                Check(calendar.DisplayDate.Month==11,"Calendar next-month button retains navigation behavior");
                ((Button)calendarItem.Template.FindName("PART_PreviousButton",calendarItem)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Task.Delay(650);
                combo.IsDropDownOpen=true;await Task.Delay(650);
                var menu=(Border)combo.Template.FindName("menu",combo);
                Check(menu.Child is GlassSelectionLayer,"Dropdown uses original glass selection component");
                var comboLayer=(GlassSelectionLayer)menu.Child;
                double before=((TranslateTransform)comboLayer.Frame.RenderTransform).Y;
                combo.SelectedIndex=0;await Task.Delay(170);
                double moving=((TranslateTransform)comboLayer.Frame.RenderTransform).Y;
                Check(moving<before && moving>0,"Dropdown selection moves through intermediate frame");
                await Task.Delay(500);Theme.Screenshot(menu,Path.Combine(directory,"dropdown.png"));combo.IsDropDownOpen=false;
                Motion.Enabled=false;calendar.SelectedDate=new DateTime(2026,10,20);await Task.Delay(100);
                selected=Find<CalendarDayButton>(calendar).Single(b=>b.IsSelected&&!b.IsInactive);
                Check(Math.Abs(((TranslateTransform)layer.Frame.RenderTransform).X-selected.TranslatePoint(new Point(),layer).X)<.5,"Animation off moves selection immediately");
                Motion.Enabled=true;
                Theme.Screenshot((FrameworkElement)window.Content,Path.Combine(directory,"theme-controls.png"));
            }
            catch(Exception ex){exit=1;results.Add(ex.ToString());}
            finally{File.WriteAllLines(Path.Combine(directory,"results.txt"),results);window.Close();app.Shutdown(exit);}
        };
        app.Run(window);return exit;
    }
}
