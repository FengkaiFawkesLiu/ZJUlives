using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Shike.UI.Glass;
// One measured surface; resize and retarget from the current animated values.
public sealed class GlassSelectionLayer : Grid
{
    public Border Frame { get; }
    FrameworkElement? target;
    Rect previous = Rect.Empty;
    readonly TranslateTransform position = new();
    public GlassSelectionLayer(UIElement content, double radius = 11, double inset = 0)
    {
        Inset = inset;
        Frame = GlassTheme.Selection(); Frame.CornerRadius = new CornerRadius(radius);
        Frame.Visibility = Visibility.Visible; Frame.Opacity = 0; Frame.RenderTransform = position;
        Children.Add(Frame); Children.Add(content);
        LayoutUpdated += (_, _) => UpdateFrame();
    }
    public double Inset { get; }
    public void Select(FrameworkElement? element) => Select(element,false);
    public void Select(FrameworkElement? element, bool retainPosition)
    {
        target = element;
        if (element == null) { Animate(Frame, OpacityProperty, 0); if(!retainPosition)previous = Rect.Empty; }
        else { Animate(Frame, OpacityProperty, 1); UpdateFrame(); }
    }
    void UpdateFrame()
    {
        if (target == null || !IsLoaded || !target.IsVisible || target.ActualWidth <= 0) return;
        Point p;
        try { p = target.TranslatePoint(new Point(), this); } catch (InvalidOperationException) { return; }
        var next = new Rect(p.X-Inset,p.Y-Inset,target.ActualWidth+2*Inset,target.ActualHeight+2*Inset);
        if (!previous.IsEmpty && Math.Abs(next.X-previous.X)<.2 && Math.Abs(next.Y-previous.Y)<.2 && Math.Abs(next.Width-previous.Width)<.2 && Math.Abs(next.Height-previous.Height)<.2) return;
        if (previous.IsEmpty) { Frame.BeginAnimation(WidthProperty,null);Frame.BeginAnimation(HeightProperty,null);position.BeginAnimation(TranslateTransform.XProperty,null);position.BeginAnimation(TranslateTransform.YProperty,null);Frame.Width=next.Width;Frame.Height=next.Height;position.X=next.X;position.Y=next.Y; }
        else { Animate(Frame,WidthProperty,next.Width);Animate(Frame,HeightProperty,next.Height);Animate(position,TranslateTransform.XProperty,next.X);Animate(position,TranslateTransform.YProperty,next.Y); }
        previous=next;Animate(Frame,OpacityProperty,1);
    }
    public static void Animate(Animatable target, DependencyProperty property, double value)
    {
        double from=(double)target.GetValue(property); if(!double.IsFinite(from))from=value;
        target.SetValue(property,value);
        target.BeginAnimation(property,Motion.Enabled?new DoubleAnimation(from,value,TimeSpan.FromMilliseconds(500)){EasingFunction=new CubicEase{EasingMode=EasingMode.EaseOut}}:null);
    }
    public static void Animate(FrameworkElement target, DependencyProperty property, double value)
    {
        double from=(double)target.GetValue(property);if(!double.IsFinite(from))from=value;
        target.SetValue(property,value);
        target.BeginAnimation(property,Motion.Enabled?new DoubleAnimation(from,value,TimeSpan.FromMilliseconds(500)){EasingFunction=new CubicEase{EasingMode=EasingMode.EaseOut}}:null);
    }
}
