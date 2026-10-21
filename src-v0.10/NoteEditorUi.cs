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
