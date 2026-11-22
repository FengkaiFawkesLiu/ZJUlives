using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Shike.UI.Glass;
public static class GlassMenus
{
    static readonly DependencyProperty InstalledProperty = DependencyProperty.RegisterAttached("Installed",typeof(bool),typeof(GlassMenus),new PropertyMetadata(false));
    static readonly DependencyProperty BeforeOpenProperty = DependencyProperty.RegisterAttached("BeforeOpen",typeof(Action<FrameworkElement>),typeof(GlassMenus));
    public static void AnimateMenu(ComboBox combo) => AnimateMenu(combo, null);
    public static void AnimateMenu(ComboBox combo, Action<FrameworkElement>? beforeOpen)
    {
        if(beforeOpen != null) combo.SetValue(BeforeOpenProperty, beforeOpen);
        if((bool)combo.GetValue(InstalledProperty))return;combo.SetValue(InstalledProperty,true);
        GlassSelectionLayer? layer=null;
        void Selection(){if(layer!=null && combo.ItemContainerGenerator.ContainerFromIndex(combo.SelectedIndex) is FrameworkElement item)layer.Select(item);}
        combo.SelectionChanged+=(_,_)=>Selection();
        void Arrow(bool open){combo.ApplyTemplate();if(combo.Template.FindName("arrow",combo) is FrameworkElement a){a.RenderTransformOrigin=new Point(.5,.5);if(a.RenderTransform is not RotateTransform)a.RenderTransform=new RotateTransform();GlassSelectionLayer.Animate((RotateTransform)a.RenderTransform,RotateTransform.AngleProperty,open?90:0);}}
        combo.DropDownClosed+=(_,_)=>Arrow(false);
        combo.DropDownOpened+=(_,_)=>
        {
            if(combo.GetValue(BeforeOpenProperty) is Action<FrameworkElement> callback) callback(combo);
            else if(combo.Parent is Panel group) foreach(UIElement sibling in group.Children) if(sibling is ComboBox other && !ReferenceEquals(other,combo)) other.IsDropDownOpen=false;
            Arrow(true);
            combo.Dispatcher.BeginInvoke(new Action(Selection));
            if(combo.Template.FindName("menu",combo) is Border frame && frame.Child is UIElement child && child is not GlassSelectionLayer){frame.Child=null;layer=new GlassSelectionLayer(child,8);frame.Child=layer;layer.PreviewMouseMove+=(_,e)=>{DependencyObject? node=e.OriginalSource as DependencyObject;while(node!=null && node is not ComboBoxItem)node=node is Visual?VisualTreeHelper.GetParent(node):null;if(node is ComboBoxItem item)layer.Select(item);};layer.MouseLeave+=(_,_)=>Selection();combo.Dispatcher.BeginInvoke(new Action(Selection));}
            if(!Motion.Enabled)return;
            if(combo.Template.FindName("menu",combo) is FrameworkElement menu)
            {
                var shift=new TranslateTransform(0,-6); menu.RenderTransform=shift;
                shift.BeginAnimation(TranslateTransform.YProperty,new DoubleAnimation(0,TimeSpan.FromMilliseconds(Motion.DurationMs)) { EasingFunction=new CubicEase { EasingMode=EasingMode.EaseOut } });
                menu.BeginAnimation(UIElement.OpacityProperty,new DoubleAnimation(.35,1,TimeSpan.FromMilliseconds(Motion.DurationMs)));
            }
        };
    }
}
