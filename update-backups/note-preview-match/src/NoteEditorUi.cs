using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Shike;
internal static class NoteEditorUi
{
    public static readonly Brush Paper = Theme.Brush("#F7F8F2");
    public static Border Setting(string label, FrameworkElement control)
    {
        control.Margin = new Thickness(5,0,0,0);
        if(control is ComboBox combo) AnimateMenu(combo);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(Theme.Text(label,12,Theme.Muted)); row.Children.Add(control);
        return new Border { CornerRadius=new CornerRadius(9), BorderBrush=GlassTheme.Edge, BorderThickness=new Thickness(1),
            Background=Theme.Brush("#35FFFFFF"), Padding=new Thickness(9,1,3,1), Margin=new Thickness(0,0,8,6),Child=row };
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
        double to=open?panel.DesiredSize.Height:0;
        panel.ClipToBounds=true;
        var animation=new DoubleAnimation(from,to,TimeSpan.FromMilliseconds(Motion.DurationMs)) { EasingFunction=new CubicEase { EasingMode=EasingMode.EaseOut } };
        animation.Completed+=(_,_)=>{if(!Equals(panel.Tag,open))return;panel.BeginAnimation(FrameworkElement.HeightProperty,null);panel.Height=double.NaN;panel.Visibility=open?Visibility.Visible:Visibility.Collapsed;panel.ClipToBounds=false;};
        panel.BeginAnimation(FrameworkElement.HeightProperty,animation);
    }
    public static void Install(AppPanel panel)
    {
        panel.Resources=(ResourceDictionary)XamlReader.Parse("""
        <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
          <Style TargetType="ComboBox">
            <Setter Property="MinHeight" Value="32"/><Setter Property="FontSize" Value="12"/><Setter Property="FocusVisualStyle" Value="{x:Null}"/>
            <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ComboBox"><Grid>
              <Border x:Name="edge" Background="Transparent" CornerRadius="7"/>
              <ContentPresenter Content="{TemplateBinding SelectionBoxItem}" ContentTemplate="{TemplateBinding SelectionBoxItemTemplate}" Margin="5,4,24,4" VerticalAlignment="Center" IsHitTestVisible="False"/>
              <Path x:Name="arrow" Data="M 0 0 L 4 4 L 8 0" Width="8" Height="4" Stroke="#365E49" StrokeThickness="1.4" HorizontalAlignment="Right" VerticalAlignment="Center" Margin="0,0,9,0" IsHitTestVisible="False"/>
              <ToggleButton Focusable="False" IsChecked="{Binding IsDropDownOpen,RelativeSource={RelativeSource TemplatedParent},Mode=TwoWay}"><ToggleButton.Template><ControlTemplate TargetType="ToggleButton"><Border Background="Transparent"/></ControlTemplate></ToggleButton.Template></ToggleButton>
              <Popup x:Name="PART_Popup" IsOpen="{TemplateBinding IsDropDownOpen}" Placement="Bottom" AllowsTransparency="True" Focusable="False" PopupAnimation="None"><Border x:Name="menu" Background="#E0EADD" BorderBrush="{DynamicResource Glass.Edge}" BorderThickness="1" CornerRadius="11" Padding="5" Margin="0,6,0,0" MinWidth="{Binding ActualWidth,RelativeSource={RelativeSource TemplatedParent}}" MaxHeight="300"><ScrollViewer CanContentScroll="True" VerticalScrollBarVisibility="Auto"><ItemsPresenter KeyboardNavigation.DirectionalNavigation="Contained"/></ScrollViewer></Border></Popup>
            </Grid><ControlTemplate.Triggers><Trigger Property="IsKeyboardFocusWithin" Value="True"><Setter TargetName="edge" Property="Background" Value="#285B9671"/></Trigger><Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.4"/></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter>
          </Style>
          <Style TargetType="ComboBoxItem"><Setter Property="FocusVisualStyle" Value="{x:Null}"/><Setter Property="Foreground" Value="#294C39"/><Setter Property="Padding" Value="10,7"/><Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ComboBoxItem"><Border x:Name="item" Background="Transparent" CornerRadius="7" Padding="{TemplateBinding Padding}"><ContentPresenter/></Border><ControlTemplate.Triggers><Trigger Property="IsSelected" Value="True"><Setter TargetName="item" Property="Background" Value="#30548866"/></Trigger><Trigger Property="IsHighlighted" Value="True"><Setter TargetName="item" Property="Background" Value="#60FFFFFF"/></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter></Style>
          <Style TargetType="ScrollBar"><Setter Property="Width" Value="9"/><Setter Property="Background" Value="Transparent"/><Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ScrollBar"><Grid Background="Transparent"><Track x:Name="PART_Track" Orientation="{TemplateBinding Orientation}" Minimum="{TemplateBinding Minimum}" Maximum="{TemplateBinding Maximum}" Value="{TemplateBinding Value}" ViewportSize="{TemplateBinding ViewportSize}" IsDirectionReversed="True"><Track.DecreaseRepeatButton><RepeatButton Command="ScrollBar.PageUpCommand" Focusable="False"><RepeatButton.Template><ControlTemplate TargetType="RepeatButton"><Border Background="Transparent"/></ControlTemplate></RepeatButton.Template></RepeatButton></Track.DecreaseRepeatButton><Track.Thumb><Thumb><Thumb.Template><ControlTemplate TargetType="Thumb"><Border CornerRadius="3" Margin="2" Background="#65738D7A"/></ControlTemplate></Thumb.Template></Thumb></Track.Thumb><Track.IncreaseRepeatButton><RepeatButton Command="ScrollBar.PageDownCommand" Focusable="False"><RepeatButton.Template><ControlTemplate TargetType="RepeatButton"><Border Background="Transparent"/></ControlTemplate></RepeatButton.Template></RepeatButton></Track.IncreaseRepeatButton></Track></Grid></ControlTemplate></Setter.Value></Setter></Style>
        </ResourceDictionary>
        """);
    }
    public static void AnimateMenu(ComboBox combo)
    {
        combo.DropDownOpened+=(_,_)=>
        {
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
