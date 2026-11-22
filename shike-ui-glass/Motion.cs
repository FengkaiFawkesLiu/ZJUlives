using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Shike.UI.Glass;
public static class Motion
{
    public const int DurationMs = 500;
    public static bool Enabled { get; set; } = true;
    public static void Move(FrameworkElement element, double from, int ms = DurationMs)
    {
        if (!Enabled) return;
        var translate = new TranslateTransform(); element.RenderTransform = translate;
        translate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(from, 0, TimeSpan.FromMilliseconds(ms)) {
            EasingFunction = new BackEase { Amplitude = .35, EasingMode = EasingMode.EaseOut } });
    }
    public static void Enter(FrameworkElement element)
    {
        if (!Enabled) return;
        Move(element, 24, DurationMs);
        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(.12, 1, TimeSpan.FromMilliseconds(Motion.DurationMs)) { EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } });
    }
    public static void Press(Control control)
    {
        var scale = new ScaleTransform(1,1); control.RenderTransformOrigin = new Point(.5,.5); control.RenderTransform = scale;
        void Animate(double value) {
            if (!Enabled) return;
            foreach(var property in new[]{ScaleTransform.ScaleXProperty, ScaleTransform.ScaleYProperty})
                scale.BeginAnimation(property, new DoubleAnimation(value, TimeSpan.FromMilliseconds(Motion.DurationMs)) { EasingFunction = value < 1 ? new CubicEase { EasingMode=EasingMode.EaseOut } : new BackEase { Amplitude=.5, EasingMode=EasingMode.EaseOut } });
        }
        control.PreviewMouseLeftButtonDown += (_,_) => { if (!Enabled) return; scale.BeginAnimation(ScaleTransform.ScaleXProperty,null); scale.BeginAnimation(ScaleTransform.ScaleYProperty,null); scale.ScaleX=scale.ScaleY=.94; };
        control.PreviewMouseLeftButtonUp += (_,_) => Animate(1);
        control.MouseLeave += (_,_) => Animate(1);
    }
}
