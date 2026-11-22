using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Shike.UI.Glass;
// Shared materials: new modules should use Theme.Card and these resources, not local white fills.
public static class GlassTheme
{
    static Brush GlassBrush(string hex) => new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
    public static SolidColorBrush Surface = new(Color.FromArgb(15,255,255,255));
    // Approved bright Tiffany-inspired palette; background is 28% accent over white.
    public static Brush Accent => GlassBrush("#87E2DA");
    public static Brush Backdrop => GlassBrush("#DDF7F5");
    public static Brush Tint(string hex) { var c=(Color)ColorConverter.ConvertFromString(hex); c.A=60; return new SolidColorBrush(c); }
    public static Brush Sidebar => Accent;
    public static Brush Edge => new LinearGradientBrush(new GradientStopCollection {
        new(Color.FromArgb(179,255,255,255),0), new(Color.FromArgb(135,190,244,235),.25),
        new(Color.FromArgb(138,191,209,235),.48), new(Color.FromArgb(171,135,226,218),.73), new(Color.FromArgb(158,177,239,225),1)},38);
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
                <Border x:Name="rail" Height="4" VerticalAlignment="Center" CornerRadius="2" Background="#3047726F"/>
                <Track x:Name="PART_Track" Minimum="{TemplateBinding Minimum}" Maximum="{TemplateBinding Maximum}" Value="{TemplateBinding Value}" Orientation="{TemplateBinding Orientation}" IsDirectionReversed="{TemplateBinding IsDirectionReversed}">
                  <Track.DecreaseRepeatButton><RepeatButton Command="Slider.DecreaseLarge" Focusable="False"><RepeatButton.Template><ControlTemplate TargetType="RepeatButton"><Border Background="Transparent"/></ControlTemplate></RepeatButton.Template></RepeatButton></Track.DecreaseRepeatButton>
                  <Track.Thumb><Thumb Width="18" Height="18"><Thumb.Template><ControlTemplate TargetType="Thumb"><Border x:Name="knob" CornerRadius="9" BorderBrush="#B0FFFFFF" BorderThickness="1.2"><Border.Background><RadialGradientBrush GradientOrigin="0.25,0.2"><GradientStop Color="#D1F0FFFD" Offset="0"/><GradientStop Color="#D187E2DA" Offset="1"/></RadialGradientBrush></Border.Background></Border><ControlTemplate.Triggers><Trigger Property="IsMouseOver" Value="True"><Setter TargetName="knob" Property="BorderBrush" Value="#216F6B"/></Trigger><Trigger Property="IsDragging" Value="True"><Setter TargetName="knob" Property="BorderBrush" Value="#216F6B"/></Trigger></ControlTemplate.Triggers></ControlTemplate></Thumb.Template></Thumb></Track.Thumb>
                  <Track.IncreaseRepeatButton><RepeatButton Command="Slider.IncreaseLarge" Focusable="False"><RepeatButton.Template><ControlTemplate TargetType="RepeatButton"><Border Background="Transparent"/></ControlTemplate></RepeatButton.Template></RepeatButton></Track.IncreaseRepeatButton>
                </Track>
                
              </Grid>
              <ControlTemplate.Triggers><Trigger Property="IsKeyboardFocusWithin" Value="True"><Setter TargetName="rail" Property="Background" Value="#90216F6B"/></Trigger><Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.4"/></Trigger></ControlTemplate.Triggers>
            </ControlTemplate></Setter.Value></Setter>
          </Style>
        </ResourceDictionary>
        """);
        foreach (var key in controls.Keys) app.Resources[key] = controls[key];
        app.Resources["Glass.Surface"]=Surface;
        app.Resources["Glass.Edge"]=Edge;
        app.Resources["Glass.Reading"]=GlassBrush("#CCF7FDFC");
        app.Resources["Glass.Input"]=GlassBrush("#65FFFFFF");
        app.Resources["Glass.Backdrop"]=Backdrop;
        app.Resources["Glass.Radius"]=new CornerRadius(14);
        GlassControls.Install(app);
    }
}

