using System;
using System.Linq;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;

namespace Shike.UI.Glass;

public static class GlassControls
{
    static readonly DependencyProperty CalendarInstalled = DependencyProperty.RegisterAttached("CalendarInstalled",typeof(bool),typeof(GlassControls),new PropertyMetadata(false));
    public static void Install(Application app)
    {
        app.Resources["Glass.Selection"] = GlassTheme.Selection().Background;
        var resources = new ResourceDictionary { Source=new Uri("/Shike.UI.Glass;component/GlassControls.xaml",UriKind.Relative) };
        ((Style)resources[typeof(ComboBox)]).Setters.Add(new EventSetter(FrameworkElement.LoadedEvent,new RoutedEventHandler((sender,_)=>GlassMenus.AnimateMenu((ComboBox)sender))));
        ((Style)resources[typeof(Calendar)]).Setters.Add(new EventSetter(FrameworkElement.LoadedEvent,new RoutedEventHandler((sender,_)=>((Calendar)sender).Dispatcher.BeginInvoke(new Action(()=>InstallCalendar((Calendar)sender)),DispatcherPriority.Loaded))));
        foreach(var key in resources.Keys)app.Resources[key]=resources[key];
        GlassHover.Install(app);
    }
    static IEnumerable<T> Children<T>(DependencyObject root) where T:DependencyObject
    {
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)
        {var child=VisualTreeHelper.GetChild(root,i);if(child is T found)yield return found;foreach(var nested in Children<T>(child))yield return nested;}
    }
    static void InstallCalendar(Calendar calendar)
    {
        if((bool)calendar.GetValue(CalendarInstalled))return;
        calendar.ApplyTemplate();
        var item=Children<CalendarItem>(calendar).FirstOrDefault();item?.ApplyTemplate();
        if(item?.Template.FindName("PART_MonthView",item) is not Grid month || VisualTreeHelper.GetParent(month) is not Grid parent)return;
        calendar.SetValue(CalendarInstalled,true);
        int index=parent.Children.IndexOf(month), row=Grid.GetRow(month), column=Grid.GetColumn(month);
        parent.Children.Remove(month);
        var layer=new GlassSelectionLayer(month,8);Grid.SetRow(layer,row);Grid.SetColumn(layer,column);parent.Children.Insert(index,layer);
        void Update()
        {
            layer.Visibility=calendar.DisplayMode==CalendarMode.Month?Visibility.Visible:Visibility.Hidden;
            if(item.Template.FindName("PART_YearView",item) is Grid year)
                year.Visibility=calendar.DisplayMode==CalendarMode.Month?Visibility.Hidden:Visibility.Visible;
            layer.Select(Children<CalendarDayButton>(month).FirstOrDefault(button=>button.IsSelected && !button.IsInactive));
        }
        void Queue()=>calendar.Dispatcher.BeginInvoke(new Action(Update),DispatcherPriority.Loaded);
        calendar.SelectedDatesChanged+=(_,_)=>Queue();calendar.DisplayDateChanged+=(_,_)=>Queue();calendar.DisplayModeChanged+=(_,_)=>Queue();
        Queue();
    }
}
