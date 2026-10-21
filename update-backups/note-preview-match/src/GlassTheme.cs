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
        var controls = (ResourceDictionary)System.Windows.Markup.XamlReader.Parse("""
        <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
          <Style TargetType="Slider">
            <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
            <Setter Property="MinHeight" Value="28"/>
            <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="Slider">
              <Grid Background="Transparent" Margin="8,0">
                <Border x:Name="rail" Height="4" VerticalAlignment="Center" CornerRadius="2" Background="#3047725C"/>
                <Track x:Name="PART_Track" Minimum="{TemplateBinding Minimum}" Maximum="{TemplateBinding Maximum}" Value="{TemplateBinding Value}" Orientation="{TemplateBinding Orientation}" IsDirectionReversed="{TemplateBinding IsDirectionReversed}">
                  <Track.DecreaseRepeatButton><RepeatButton Command="Slider.DecreaseLarge" Focusable="False"><RepeatButton.Template><ControlTemplate TargetType="RepeatButton"><Border Background="Transparent"/></ControlTemplate></RepeatButton.Template></RepeatButton></Track.DecreaseRepeatButton>
                  <Track.Thumb><Thumb Width="18" Height="18"><Thumb.Template><ControlTemplate TargetType="Thumb"><Border x:Name="knob" CornerRadius="9" BorderBrush="#B0FFFFFF" BorderThickness="1.2"><Border.Background><RadialGradientBrush GradientOrigin="0.25,0.2"><GradientStop Color="#D1EDF8EC" Offset="0"/><GradientStop Color="#A1518E72" Offset="1"/></RadialGradientBrush></Border.Background></Border><ControlTemplate.Triggers><Trigger Property="IsMouseOver" Value="True"><Setter TargetName="knob" Property="BorderBrush" Value="#28725B"/></Trigger><Trigger Property="IsDragging" Value="True"><Setter TargetName="knob" Property="BorderBrush" Value="#28725B"/></Trigger></ControlTemplate.Triggers></ControlTemplate></Thumb.Template></Thumb></Track.Thumb>
                  <Track.IncreaseRepeatButton><RepeatButton Command="Slider.IncreaseLarge" Focusable="False"><RepeatButton.Template><ControlTemplate TargetType="RepeatButton"><Border Background="Transparent"/></ControlTemplate></RepeatButton.Template></RepeatButton></Track.IncreaseRepeatButton>
                </Track>
                
              </Grid>
              <ControlTemplate.Triggers><Trigger Property="IsKeyboardFocusWithin" Value="True"><Setter TargetName="rail" Property="Background" Value="#9028725B"/></Trigger><Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.4"/></Trigger></ControlTemplate.Triggers>
            </ControlTemplate></Setter.Value></Setter>
          </Style>
        </ResourceDictionary>
        """);
        foreach (var key in controls.Keys) app.Resources[key] = controls[key];
        app.Resources["Glass.Surface"]=Surface;
        app.Resources["Glass.Edge"]=Edge;
        app.Resources["Glass.Reading"]=Theme.Brush("#CCFAFCF8");
        app.Resources["Glass.Input"]=Theme.Brush("#65FFFFFF");
        app.Resources["Glass.Backdrop"]=Backdrop;
        app.Resources["Glass.Radius"]=new CornerRadius(14);
    }
}

