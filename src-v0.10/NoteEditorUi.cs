using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Shike;
internal static class NoteEditorUi
{
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Button,Action> closeDisclosures=new();
    static void ClosePeers(FrameworkElement current)
    {
        if(current.Parent is not Panel group)return;
        foreach(UIElement child in group.Children){if(ReferenceEquals(child,current))continue;if(child is Button b && closeDisclosures.TryGetValue(b,out var close))close();if(child is ComboBox combo)combo.IsDropDownOpen=false;}
    }
    public static readonly Brush Paper = Theme.Brush("#F7F8F2");
    public static FrameworkElement Icon(string key, Brush? color=null)
    {
        string path=key switch {
            "calendar"=>"M 4,5 L 20,5 20,21 4,21 Z M 4,10 L 20,10 M 8,2 L 8,7 M 16,2 L 16,7",
            "book"=>"M 12,5 Q 7,2 2,4 L 2,20 Q 7,18 12,21 Q 17,18 22,20 L 22,4 Q 17,2 12,5 L 12,21",
            "note"=>"M 6,3 L 20,3 20,21 6,21 Z M 3,7 L 8,7 M 3,12 L 8,12 M 3,17 L 8,17 M 11,8 L 16,8 M 11,12 L 16,12",
            "school"=>"M 3,10 L 12,3 21,10 M 5,9 L 5,21 19,21 19,9 M 10,21 L 10,15 14,15 14,21 M 11,9 L 13,9",
            "settings"=>"M 4,5 L 20,5 M 4,12 L 20,12 M 4,19 L 20,19 M 8,2 L 8,8 M 16,9 L 16,15 M 10,16 L 10,22",
            "list"=>"M 8,6 L 21,6 M 8,12 L 21,12 M 8,18 L 21,18 M 3,6 L 3.5,6 M 3,12 L 3.5,12 M 3,18 L 3.5,18",
            "undo"=>"M 8,4 L 3,9 8,14 M 3,9 L 14,9 C 23,9 23,20 14,20",
            "redo"=>"M 16,4 L 21,9 16,14 M 21,9 L 10,9 C 1,9 1,20 10,20",
            "plus"=>"M 12,4 L 12,20 M 4,12 L 20,12",
            _=>"M 8,4 L 16,4 M 4,20 L 12,20 M 14,4 L 6,20"
        };
        return new System.Windows.Shapes.Path { Data=Geometry.Parse(path),Stroke=color??Theme.Ink,StrokeThickness=1.7,StrokeStartLineCap=PenLineCap.Round,StrokeEndLineCap=PenLineCap.Round,StrokeLineJoin=PenLineJoin.Round,Width=18,Height=18,Stretch=Stretch.Uniform,VerticalAlignment=VerticalAlignment.Center };
    }
    public static Button Action(string label,Action action,string? icon=null)
    {
        var b=Theme.Button(label,action);b.Padding=new Thickness(10,7,10,7);b.Margin=new Thickness(0);b.Background=Brushes.Transparent;b.BorderThickness=new Thickness(0);b.FontSize=12;
        if(icon!=null){var row=new StackPanel { Orientation=Orientation.Horizontal };row.Children.Add(Icon(icon));var text=Theme.Text(label,12);text.Margin=new Thickness(6,0,0,0);row.Children.Add(text);b.Content=row;}
        System.Windows.Automation.AutomationProperties.SetName(b,label);return b;
    }
    public static Brush Toolbar => new LinearGradientBrush(Color.FromRgb(211,226,215),Color.FromRgb(233,237,222),0);
    public static void Selected(Button button,bool selected)
    {
        if(Equals(button.Tag,selected))return;button.Tag=selected;
        button.BorderBrush=GlassTheme.Edge;button.BorderThickness=new Thickness(1);
        if(button.Background is not SolidColorBrush brush || brush.IsFrozen){brush=new SolidColorBrush(Colors.Transparent);button.Background=brush;}
        var to=Color.FromArgb(selected?(byte)70:(byte)0,255,255,255);
        brush.BeginAnimation(SolidColorBrush.ColorProperty,Motion.Enabled?new ColorAnimation(brush.Color,to,TimeSpan.FromMilliseconds(500)):null);if(!Motion.Enabled)brush.Color=to;
    }
    public static Button Disclosure(string label, FrameworkElement panel, string? icon=null)
    {
        var rotation=new RotateTransform();
        var arrow=new System.Windows.Shapes.Path { Data=Geometry.Parse("M 0,0 L 4,4 0,8"),Stroke=Theme.Ink,StrokeThickness=1.4,Width=5,Height=9,Margin=new Thickness(9,0,2,0),VerticalAlignment=VerticalAlignment.Center,RenderTransformOrigin=new Point(.5,.5),RenderTransform=rotation };
        var b=Action(label,()=>{},icon);var content=b.Content;b.Content=null;var row=new StackPanel{Orientation=Orientation.Horizontal};
        row.Children.Add(content is UIElement element?element:Theme.Text(label,12));row.Children.Add(arrow);
        var selection=new Border{CornerRadius=new CornerRadius(10),BorderBrush=GlassTheme.Edge,BorderThickness=new Thickness(1),Background=Theme.Brush("#35FFFFFF"),Opacity=0,IsHitTestVisible=false};
        var holder=new Grid();holder.Children.Add(selection);holder.Children.Add(row);b.Content=holder;
        void VisualState(bool open){b.Tag=open;GlassSelectionLayer.Animate(rotation,RotateTransform.AngleProperty,open?90:0);GlassSelectionLayer.Animate(selection,UIElement.OpacityProperty,open?1:0);}
        closeDisclosures.Add(b,()=>{if(panel.Tag is true)Toggle(panel);VisualState(false);});
        b.Click+=(_,_)=>{if(panel.Tag is not true)ClosePeers(b);Toggle(panel);VisualState(panel.Tag is true);};
        return b;
    }
    public static GlassSelectionLayer ToolbarSelection(WrapPanel tools)
    {
        var layer=new GlassSelectionLayer(tools,11,2);
        foreach (UIElement child in tools.Children)
        {
            if (child is FrameworkElement element && child is Button or ComboBox)
            {
                element.FocusVisualStyle = null;
                element.MouseEnter += (_, _) => layer.Select(element);
            }
        }
        tools.MouseLeave += (_, _) => layer.Select(null);        return layer;
    }
    public static Border Setting(string label, FrameworkElement control)
    {
        control.Margin = new Thickness(5,0,0,0); control.VerticalAlignment=VerticalAlignment.Center;
        if(control is ComboBox combo) AnimateMenu(combo);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var caption=Theme.Text(label,12,Theme.Muted);caption.VerticalAlignment=VerticalAlignment.Center;row.Children.Add(caption); row.Children.Add(control);
        return new Border { CornerRadius=new CornerRadius(9), BorderBrush=GlassTheme.Edge, BorderThickness=new Thickness(1),
            Background=Theme.Brush("#35FFFFFF"), Padding=new Thickness(9,2,3,2), MinHeight=38, VerticalAlignment=VerticalAlignment.Center, Margin=new Thickness(0,0,8,0),Child=row };
    }
    public static void Toggle(FrameworkElement panel)
    {
        bool open = panel.Tag is not true;
        panel.Tag = open;
        double from = panel.Visibility == Visibility.Visible ? panel.ActualHeight : 0;
        panel.BeginAnimation(FrameworkElement.HeightProperty,null); panel.Height=double.NaN;
        if (!Motion.Enabled) { panel.Visibility=open?Visibility.Visible:Visibility.Collapsed; return; }
        panel.Visibility=Visibility.Visible;
        panel.Measure(new Size(Math.Max(1,(panel.Parent as FrameworkElement)?.ActualWidth ?? 700),double.PositiveInfinity));
        double to=open?Math.Max(0,panel.DesiredSize.Height-panel.Margin.Top-panel.Margin.Bottom):0;
        panel.ClipToBounds=true;
        // StackPanel can retain the children's arrange size while Height animates.
        // Animate the visible clip too, so the reveal is real on every WPF layout path.
        double clipFrom=panel.Clip is RectangleGeometry oldClip?oldClip.Rect.Height:from;
        double clipWidth=Math.Max(panel.DesiredSize.Width,Math.Max(panel.ActualWidth,1));
        var clip=new RectangleGeometry(new Rect(0,0,clipWidth,to));panel.Clip=clip;
        clip.BeginAnimation(RectangleGeometry.RectProperty,new RectAnimation(new Rect(0,0,clipWidth,clipFrom),new Rect(0,0,clipWidth,to),TimeSpan.FromMilliseconds(500)){EasingFunction=new CubicEase{EasingMode=EasingMode.EaseOut}});
        var animation=new DoubleAnimation(from,to,TimeSpan.FromMilliseconds(Motion.DurationMs)) { EasingFunction=new CubicEase { EasingMode=EasingMode.EaseOut } };
        animation.Completed+=(_,_)=>{if(!Equals(panel.Tag,open))return;panel.BeginAnimation(FrameworkElement.HeightProperty,null);panel.Height=double.NaN;panel.Visibility=open?Visibility.Visible:Visibility.Collapsed;panel.ClipToBounds=false;panel.Clip=null;};
        panel.BeginAnimation(FrameworkElement.HeightProperty,animation);
    }
    public static void Install(AppPanel panel)
    {
        // Compact toolbar geometry inherits the same materials and templates as every page.
        var compact = new Style(typeof(ComboBox), (Style)Application.Current.Resources[typeof(ComboBox)]);
        compact.Setters.Add(new Setter(Control.MinHeightProperty, 32d));
        compact.Setters.Add(new Setter(Control.FontSizeProperty, 12d));
        panel.Resources[typeof(ComboBox)] = compact;
    }
    public static void AnimateMenu(ComboBox combo) => GlassMenus.AnimateMenu(combo, ClosePeers);
}
