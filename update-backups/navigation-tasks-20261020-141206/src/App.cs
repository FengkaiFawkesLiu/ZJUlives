using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Markup;
using System.Windows.Media.Imaging;

namespace Shike;

public static class Program
{
    public static string DataDirectory(string appDirectory) => Path.GetFullPath(Path.Combine(appDirectory, "..", "data"));

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--formula-test")) return FormulaEditorTests.Run(args[^1]);
        if (args.Contains("--self-test")) return Tests.Run(args);
        // User data lives beside versioned app folders, independently of upgrades.
        string root = DataDirectory(AppContext.BaseDirectory);
        bool uiTest = args.Contains("--ui-test") || args.Contains("--fullscreen-test");
        bool preview = args.Contains("--preview") || uiTest;
        if (preview) root = Path.Combine(Path.GetTempPath(), "Shike-preview-" + Guid.NewGuid().ToString("N"));
        using var mutex = new Mutex(true, "Local\\Shike-" + Engine.Hash(root), out bool owned);
        if (!owned) { MessageBox.Show("拾课已在运行。升级时请在旧版侧栏或托盘选择“退出程序”（仅关闭窗口不会退出），然后重新打开新版。", "拾课"); return 0; }
        if (args.Contains("--repair-periods"))
        {
            try { var data = new Store(root); int count = PeriodSchedule.Migrate(data); File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "period-repair.txt"), $"Corrected courses: {count}; records: {data.Data.Items.Count}; schedule: {data.Data.Settings.Periods}"); return 0; }
            catch (Exception ex) { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "period-repair.txt"), "FAILED: " + ex.Message); return 1; }
        }
        if (args.Contains("--upgrade-notes"))
        {
            try { var data = new Store(root); NotesModel.Migrate(data); File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "note-upgrade.txt"), $"Notes schema: {data.Data.NoteSchemaVersion}; libraries: {data.Data.NoteLibraries.Count}; notes: {data.Data.Items.Count(i => i.Kind == "笔记")}; records: {data.Data.Items.Count}"); return 0; }
            catch (Exception ex) { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "note-upgrade.txt"), "FAILED: " + ex.Message); return 1; }
        }
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        Theme.Install(app);
        try
        {
            var store = new Store(root);
            if (!preview) { PeriodSchedule.Migrate(store); NotesModel.Migrate(store); }
            if (preview && !uiTest) Tests.Seed(store);
            var seed = Path.Combine(AppContext.BaseDirectory, "campus-seed.json");
            if (!uiTest && store.Data.CampusActivities.Count == 0 && File.Exists(seed))
            {
                try {
                    var entries = System.Text.Json.JsonSerializer.Deserialize<System.Collections.Generic.List<CampusActivity>>(File.ReadAllText(seed))!;
                    CampusFeeds.Merge(store.Data, entries);
                    foreach (var group in entries.GroupBy(a => a.Source)) store.Data.CampusSources.Add(new() { Name = group.Key, Updated = group.Max(a => a.Fetched), Status = "已载入随版本提供的官网缓存" });
                    store.Save();
                } catch { /* An optional public seed must not prevent opening personal data. */ }
            }
            var window = new MainWindow(store, preview);
            app.MainWindow = window;
            if (preview)
            {
                window.Loaded += (_, _) => window.Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        var dir = args.Length > 1 ? args[^1] : Path.Combine(AppContext.BaseDirectory, "previews");
                        Directory.CreateDirectory(dir);
                        if (uiTest) { window.RunUiTests(dir); return; }
                        foreach (var view in new[] { "今日概览", "周课表", "今日时间轴", "笔记与附件", "校园助手", "校园活动", "设置与备份" })
                        {
                            window.SwitchView(view); window.UpdateLayout();
                            window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle); window.UpdateLayout();
                            Theme.Screenshot((FrameworkElement)window.Content, Path.Combine(dir, view + ".png"));
                        }
                        File.WriteAllText(Path.Combine(dir, "render-ok.txt"), "All six views rendered successfully.");
                    }
                    catch (Exception ex) { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "preview-error.txt"), ex.ToString()); }
                    finally { window.Exit(); }
                }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            }
            app.DispatcherUnhandledException += (_, e) =>
            {
                MessageBox.Show("操作未完成：" + e.Exception.Message + "\n请检查本地存储空间或输入内容。", "拾课", MessageBoxButton.OK, MessageBoxImage.Error); e.Handled = true;
            };
            window.Show(); app.Run(); return 0;
        }
        catch (Exception ex)
        {
            MessageBox.Show("拾课无法启动：" + ex.Message + "\n\n原始数据不会被重置。请保留 data 文件夹；如数据损坏，可以先复制备份，再使用 data.json.bak 恢复。", "拾课", MessageBoxButton.OK, MessageBoxImage.Error);
            return 1;
        }
    }
}

public static class Theme
{
    public static void Screenshot(FrameworkElement element, string path)
    {
        var rect = new Rect(0, 0, element.ActualWidth, element.ActualHeight);
        var drawing = new DrawingVisual();
        using (var dc = drawing.RenderOpen()) { dc.DrawRectangle(Cream, null, rect); dc.DrawRectangle(new VisualBrush(element), null, rect); }
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(rect.Width), (int)Math.Ceiling(rect.Height), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(drawing); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var fs = File.Create(path); encoder.Save(fs);
    }
    public static Brush Ink = Brush("#223B36"), Muted = Brush("#798680"), Green = Brush("#28725B"), Cream = Brush("#F5F6F2"), Line = Brush("#E4E9E2");
    public static Brush Brush(string hex) => new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
    public static Brush Kind(string kind) => Brush(kind switch { "课程" => "#357F68", "考试" => "#B86E34", "待办" => "#517CA4", "笔记" => "#8A719F", _ => "#7463A5" });
    public static Brush Tint(string kind) => Brush(kind switch { "课程" => "#EAF3ED", "考试" => "#FFF1DF", "待办" => "#ECF2F8", "笔记" => "#F3EEF6", _ => "#F0EDF8" });
    public static void Install(Application app)
    {
        app.Resources = (ResourceDictionary)XamlReader.Parse("""
        <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
          <Style TargetType="Window"><Setter Property="FontFamily" Value="Microsoft YaHei UI"/><Setter Property="FontSize" Value="13"/><Setter Property="Foreground" Value="#223B36"/><Setter Property="Background" Value="#F5F6F2"/></Style>
          <Style TargetType="Button">
            <Setter Property="Background" Value="White"/><Setter Property="Foreground" Value="#284F43"/><Setter Property="BorderBrush" Value="#E2E8E1"/><Setter Property="BorderThickness" Value="1"/><Setter Property="Padding" Value="14,9"/><Setter Property="Cursor" Value="Hand"/><Setter Property="Margin" Value="0,0,8,0"/>
            <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="Button"><Border x:Name="bd" CornerRadius="8" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}" Padding="{TemplateBinding Padding}"><ContentPresenter HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}" VerticalAlignment="Center"/></Border><ControlTemplate.Triggers><Trigger Property="IsMouseOver" Value="True"><Setter TargetName="bd" Property="Opacity" Value="0.78"/></Trigger><Trigger Property="IsEnabled" Value="False"><Setter TargetName="bd" Property="Opacity" Value="0.4"/></Trigger><Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="bd" Property="BorderBrush" Value="#4B997A"/></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter>
          </Style>
          <Style TargetType="TextBox"><Setter Property="Padding" Value="10,8"/><Setter Property="Background" Value="White"/><Setter Property="BorderBrush" Value="#DCE4DD"/><Setter Property="BorderThickness" Value="1"/><Setter Property="VerticalContentAlignment" Value="Center"/><Setter Property="MinHeight" Value="36"/><Setter Property="SelectionBrush" Value="#90BDA6"/>
            <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="TextBox"><Border x:Name="frame" CornerRadius="8" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}"><ScrollViewer x:Name="PART_ContentHost" Margin="{TemplateBinding Padding}"/></Border><ControlTemplate.Triggers><Trigger Property="IsKeyboardFocusWithin" Value="True"><Setter TargetName="frame" Property="BorderBrush" Value="#28725B"/></Trigger><Trigger Property="IsEnabled" Value="False"><Setter TargetName="frame" Property="Opacity" Value="0.5"/></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter></Style>
          <Style TargetType="ComboBox"><Setter Property="Padding" Value="12,8"/><Setter Property="MinHeight" Value="42"/><Setter Property="Background" Value="White"/><Setter Property="BorderBrush" Value="#DCE4DD"/><Setter Property="VerticalContentAlignment" Value="Center"/>
          <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ComboBox"><Grid>
          <Border Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="1" CornerRadius="8"/>
          <ContentPresenter Content="{TemplateBinding SelectionBoxItem}" ContentTemplate="{TemplateBinding SelectionBoxItemTemplate}" Margin="12,6,35,6" VerticalAlignment="Center" IsHitTestVisible="False"/>
          <TextBlock Text="⌄" HorizontalAlignment="Right" VerticalAlignment="Center" Margin="0,0,12,0" Foreground="#28725B"/>
          <ToggleButton Focusable="False" IsChecked="{Binding IsDropDownOpen, RelativeSource={RelativeSource TemplatedParent}, Mode=TwoWay}"><ToggleButton.Template><ControlTemplate TargetType="ToggleButton"><Border Background="Transparent"/></ControlTemplate></ToggleButton.Template></ToggleButton>
          <Popup x:Name="PART_Popup" IsOpen="{TemplateBinding IsDropDownOpen}" Placement="Bottom" AllowsTransparency="True" Focusable="False" PopupAnimation="Slide"><Border Background="White" BorderBrush="#DCE4DD" BorderThickness="1" CornerRadius="8" MinWidth="{Binding ActualWidth, RelativeSource={RelativeSource TemplatedParent}}" MaxHeight="300" Padding="4"><ScrollViewer><ItemsPresenter KeyboardNavigation.DirectionalNavigation="Contained"/></ScrollViewer></Border></Popup>
          </Grid><ControlTemplate.Triggers><Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.5"/></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter></Style>
          <Style TargetType="ComboBoxItem"><Setter Property="Padding" Value="12,9"/><Setter Property="Background" Value="White"/><Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ComboBoxItem"><Border x:Name="item" Background="{TemplateBinding Background}" CornerRadius="5" Padding="{TemplateBinding Padding}"><ContentPresenter/></Border><ControlTemplate.Triggers><Trigger Property="IsHighlighted" Value="True"><Setter TargetName="item" Property="Background" Value="#EAF3ED"/></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter></Style>
          <Style TargetType="DatePicker"><Setter Property="FontSize" Value="16"/><Setter Property="Background" Value="White"/><Setter Property="BorderBrush" Value="#DCE4DD"/><Setter Property="Padding" Value="10"/><Setter Property="MinHeight" Value="54"/><Setter Property="VerticalContentAlignment" Value="Center"/></Style>
          <Style TargetType="Calendar"><Setter Property="FontSize" Value="15"/><Setter Property="LayoutTransform"><Setter.Value><ScaleTransform ScaleX="1.3" ScaleY="1.3"/></Setter.Value></Setter></Style>
          <Style TargetType="CheckBox"><Setter Property="Margin" Value="0,8,0,8"/><Setter Property="VerticalContentAlignment" Value="Center"/></Style>
          <Style TargetType="ScrollViewer"><Setter Property="VerticalScrollBarVisibility" Value="Auto"/><Setter Property="HorizontalScrollBarVisibility" Value="Disabled"/></Style>
        </ResourceDictionary>
        """);
    }
    public static TextBlock Text(string value, double size = 13, Brush? color = null, bool bold = false) => new() { Text = value, FontSize = size, Foreground = color ?? Ink, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    public static Button Button(string label, Action action, bool primary = false)
    {
        var b = new Button { Content = label, HorizontalContentAlignment = HorizontalAlignment.Center };
        if (primary) { b.Background = Green; b.Foreground = Brushes.White; b.BorderBrush = Green; }
        b.Click += (_, _) => action(); return b;
    }
    public static Border Card(UIElement child, string? bg = null, double padding = 20) => new() { Background = bg == null ? Brushes.White : Brush(bg), BorderBrush = Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = new Thickness(padding), Child = child, Margin = new Thickness(0, 0, 0, 14) };
    public static StackPanel Stack(params UIElement[] children) { var p = new StackPanel(); foreach (var c in children) p.Children.Add(c); return p; }
    public static StackPanel Row(params UIElement[] children) { var p = Stack(children); p.Orientation = Orientation.Horizontal; return p; }
    public static FrameworkElement Space(double h = 12) => new Border { Height = h };
    public static UIElement Field(string label, FrameworkElement input) => Stack(Text(label, 12, Muted), Space(6), input, Space(14));
    public static ComboBox Combo(string[] values, string selected) { var c = new ComboBox { ItemsSource = values }; c.SelectedItem = values.Contains(selected) ? selected : values[0]; return c; }
    public static Window Dialog(Window owner, string title, double width = 660, double height = 680) => new() { Owner = owner, Title = title, Width = width, Height = height, MinWidth = 500, MinHeight = 380, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false };
}
