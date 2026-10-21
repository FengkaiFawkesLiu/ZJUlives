using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace Shike;

internal static class GraphGlass
{
    public static FrameworkElement Panel(UIElement content)
    {
        var layers = new Grid();
        var surface = new Border { CornerRadius = new CornerRadius(20) };
        surface.SetResourceReference(Border.BackgroundProperty, "Glass.Surface"); layers.Children.Add(surface);
        layers.Children.Add(new Border { CornerRadius = new CornerRadius(20), IsHitTestVisible = false,
            Background = new LinearGradientBrush(new GradientStopCollection {
                new(Color.FromArgb(52,255,255,255),0), new(Color.FromArgb(8,255,255,255),.46), new(Color.FromArgb(28,220,247,232),1)},130) });
        layers.Children.Add(new Border { Padding = new Thickness(1), Child = content });
        layers.Children.Add(new Border { CornerRadius = new CornerRadius(20), BorderThickness = new Thickness(1.4), BorderBrush = GlassTheme.Edge, IsHitTestVisible = false });
        layers.Children.Add(new Border { CornerRadius = new CornerRadius(19), Margin = new Thickness(2), BorderThickness = new Thickness(1,1,0,0), BorderBrush = Theme.Brush("#45FFFFFF"), IsHitTestVisible = false });
        layers.SizeChanged += (_, _) => layers.Clip = new RectangleGeometry(new Rect(layers.RenderSize),20,20);
        return layers;
    }
    public static void Press(Ellipse dot, Brush color, bool pressed)
    {
        if (!pressed) { dot.Fill = color; dot.Stroke = Theme.Brush("#85FFFFFF"); dot.StrokeThickness = 1.5; dot.Effect = null; return; }
        var c = ((SolidColorBrush)color).Color;
        dot.Fill = new RadialGradientBrush(new GradientStopCollection {
            new(Color.FromArgb(175,240,249,245),0), new(Color.FromArgb(135,c.R,c.G,c.B),.28),
            new(Color.FromArgb(90,c.R,c.G,c.B),.55), new(Color.FromArgb(185,c.R,c.G,c.B),1) }) { GradientOrigin = new Point(.25,.2), Center = new Point(.4,.4), RadiusX = .7, RadiusY = .7 };
        dot.Stroke = GlassTheme.Edge; dot.StrokeThickness = 1.2;
        dot.Effect = new DropShadowEffect { Color = c, BlurRadius = 18, ShadowDepth = 0, Opacity = .12 };
    }
}
