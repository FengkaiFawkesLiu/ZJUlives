using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Shike;
// Shared materials: new modules should use Theme.Card and these resources, not local white fills.
public static class GlassTheme
{
    public static SolidColorBrush Surface = new(Color.FromArgb(15,255,255,255));
    public static Brush Backdrop => new LinearGradientBrush(new GradientStopCollection {
        new(Color.FromRgb(166,194,181),0), new(Color.FromRgb(222,225,206),.48), new(Color.FromRgb(177,204,190),1) }, 32);
    public static Brush Tint(string hex) { var c=(Color)ColorConverter.ConvertFromString(hex); c.A=60; return new SolidColorBrush(c); }
    public static Brush Sidebar => new LinearGradientBrush(Color.FromArgb(186,27,70,56),Color.FromArgb(162,43,88,69),65);
    public static Brush Edge => new LinearGradientBrush(new GradientStopCollection {
        new(Color.FromArgb(130,255,255,255),0), new(Color.FromArgb(90,255,223,160),.25),
        new(Color.FromArgb(95,233,178,220),.48), new(Color.FromArgb(115,151,209,255),.73), new(Color.FromArgb(110,171,239,197),1)},38);
    public static Border Selection() => new() { IsHitTestVisible=false, CornerRadius=new CornerRadius(12), BorderThickness=new Thickness(1.5), BorderBrush=Edge,
        Background=new LinearGradientBrush(Color.FromArgb(40,255,255,255),Color.FromArgb(8,255,255,255),90), HorizontalAlignment=HorizontalAlignment.Left,VerticalAlignment=VerticalAlignment.Top,Visibility=Visibility.Hidden };
    public static void Apply(double opacity) { Surface=new SolidColorBrush(Color.FromArgb((byte)Math.Round(Math.Clamp(opacity, .02, .45)*255),255,255,255)); if(Application.Current!=null) Application.Current.Resources["Glass.Surface"]=Surface; }
    public static void Install(Application app)
    {
        app.Resources["Glass.Surface"]=Surface;
        app.Resources["Glass.Edge"]=Edge;
        app.Resources["Glass.Reading"]=Theme.Brush("#CCFAFCF8");
        app.Resources["Glass.Input"]=Theme.Brush("#65FFFFFF");
        app.Resources["Glass.Backdrop"]=Backdrop;
        app.Resources["Glass.Radius"]=new CornerRadius(14);
    }
}
