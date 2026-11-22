using System;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Shike.UI.Glass;

// A shared, non-interactive overlay for each action group; never reparents its buttons.
public static class GlassHover
{
    public static readonly DependencyProperty IsExcludedProperty = DependencyProperty.RegisterAttached(
        "IsExcluded",typeof(bool),typeof(GlassHover),new FrameworkPropertyMetadata(false,FrameworkPropertyMetadataOptions.Inherits));
    public static readonly DependencyProperty IsSubtleProperty = DependencyProperty.RegisterAttached(
        "IsSubtle",typeof(bool),typeof(GlassHover),new PropertyMetadata(false));
    static readonly DependencyProperty InstalledProperty = DependencyProperty.RegisterAttached("Installed",typeof(bool),typeof(GlassHover),new PropertyMetadata(false));
    static readonly DependencyProperty CurrentGroupProperty = DependencyProperty.RegisterAttached("CurrentGroup",typeof(FrameworkElement),typeof(GlassHover));
    static readonly ConditionalWeakTable<FrameworkElement,HoverAdorner> groups = new();
    public static bool GetIsExcluded(DependencyObject element)=>(bool)element.GetValue(IsExcludedProperty);
    public static void SetIsExcluded(DependencyObject element,bool value)=>element.SetValue(IsExcludedProperty,value);
    public static bool GetIsSubtle(DependencyObject element)=>(bool)element.GetValue(IsSubtleProperty);
    public static void SetIsSubtle(DependencyObject element,bool value)=>element.SetValue(IsSubtleProperty,value);

    public static void Install(Application app)
    {
        var style=new Style(typeof(Button),(Style)app.Resources[typeof(Button)]);
        style.Setters.Add(new EventSetter(FrameworkElement.LoadedEvent,new RoutedEventHandler((sender,_)=>Attach((Button)sender))));
        app.Resources[typeof(Button)]=style;
    }
    static bool Allowed(Button button)
    {
        if(GetIsExcluded(button)||!button.IsEnabled||!button.IsVisible)return false;
        // Calendar and selector internals keep their own selection treatment.
        for(DependencyObject? node=VisualTreeHelper.GetParent(button);node!=null;node=VisualTreeHelper.GetParent(node))
            if(node is Calendar || node is ComboBox || node is TabItem)return false;
        return true;
    }
    static FrameworkElement Group(Button button)
    {
        for(DependencyObject? node=VisualTreeHelper.GetParent(button);node!=null;node=VisualTreeHelper.GetParent(node))
            if(node is Panel panel)return panel;
        return button;
    }
    static void Attach(Button button)
    {
        if((bool)button.GetValue(InstalledProperty))return;
        button.SetValue(InstalledProperty,true);
        button.MouseEnter+=(_,_)=>Show(button);
        button.MouseLeave+=(_,_)=>Leave(button);
        button.IsEnabledChanged+=(_,_)=>{if(!button.IsEnabled)Leave(button,true);};
        button.Unloaded+=(_,_)=>Leave(button,true);
    }
    static void Show(Button button)
    {
        if(!Allowed(button))return;
        var owner=Group(button);
        if(!groups.TryGetValue(owner,out var overlay))
        {
            var host=AdornerLayer.GetAdornerLayer(owner);if(host==null)return;
            overlay=new HoverAdorner(owner);groups.Add(owner,overlay);host.Add(overlay);
            RoutedEventHandler? unload=null;
            unload=(_,_)=>{owner.Unloaded-=unload;overlay.Stop();host.Remove(overlay);groups.Remove(owner);};
            owner.Unloaded+=unload;
        }
        bool warning=button.Foreground is SolidColorBrush brush && brush.Color.R>brush.Color.G*1.15 && brush.Color.R>brush.Color.B*1.15;
        button.SetValue(CurrentGroupProperty,owner);
        overlay.Show(button,GetIsSubtle(button)||warning);
    }
    static void Leave(Button button,bool immediate=false)
    {
        var owner=button.GetValue(CurrentGroupProperty) as FrameworkElement ?? Group(button);
        if(groups.TryGetValue(owner,out var overlay))overlay.Leave(button,immediate);
    }
    internal static GlassSelectionLayer? Surface(Button button)=>groups.TryGetValue(Group(button),out var overlay)?overlay.Surface:null;

    sealed class HoverAdorner : Adorner
    {
        public GlassSelectionLayer Surface { get; }=new(new Grid(),8);
        readonly DispatcherTimer leaveTimer=new(){Interval=TimeSpan.FromMilliseconds(75)};
        Button? current;
        public HoverAdorner(FrameworkElement owner):base(owner)
        {
            IsHitTestVisible=false;AddVisualChild(Surface);
            Surface.Loaded+=(_,_)=>{if(current!=null)Surface.Select(current);};
            leaveTimer.Tick+=(_,_)=>{leaveTimer.Stop();current=null;Surface.Select(null,true);};
        }
        public void Show(Button button,bool subtle)
        {
            leaveTimer.Stop();current=button;
            GlassSelectionLayer.Animate(Surface,OpacityProperty,subtle ? .45 : 1);
            Surface.Select(button);InvalidateArrange();
        }
        public void Leave(Button button,bool immediate)
        {
            if(!ReferenceEquals(current,button))return;
            leaveTimer.Stop();
            if(immediate){current=null;Surface.Select(null,true);}else leaveTimer.Start();
        }
        public void Stop(){leaveTimer.Stop();current=null;Surface.Select(null);}
        protected override int VisualChildrenCount=>1;
        protected override Visual GetVisualChild(int index)=>index==0?Surface:throw new ArgumentOutOfRangeException(nameof(index));
        protected override Size MeasureOverride(Size available){Surface.Measure(AdornedElement.RenderSize);return AdornedElement.RenderSize;}
        protected override Size ArrangeOverride(Size finalSize)
        {
            Surface.Arrange(new Rect(finalSize));
            var visible=new Rect(finalSize);
            for(DependencyObject? node=VisualTreeHelper.GetParent(AdornedElement);node!=null;node=VisualTreeHelper.GetParent(node))
                if(node is UIElement element && element.ClipToBounds)
                    visible.Intersect(element.TransformToVisual(AdornedElement).TransformBounds(new Rect(element.RenderSize)));
            Clip=new RectangleGeometry(visible);return finalSize;
        }
    }
}
