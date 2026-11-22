using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Shike;
using Shike.UI.Glass;

internal static class PreviewProgram
{
    [STAThread]
    static void Main(string[] args)
    {
        var app = new Application();
        Theme.Install(app);
        Motion.Enabled = true;
        var window = new Window { Title = "拾课 · 原生玻璃组件预览", Width = 820, Height = 820, MinWidth = 640, MinHeight = 650, WindowStartupLocation = WindowStartupLocation.CenterScreen };
        var page = new StackPanel { Margin = new Thickness(30) };
        window.Content = new ScrollViewer { Content = page, Background = GlassTheme.Backdrop };
        page.Children.Add(Theme.Text("拾课 · 玻璃选中态", 26, bold:true));
        page.Children.Add(Theme.Text("点击类型、日期或待办，查看玻璃框连续移动的效果", 13, Theme.Muted));
        page.Children.Add(Theme.Space(22));
        var types = new UniformGrid { Columns = 4 };
        var typeLayer = new GlassSelectionLayer(types, 11);
        var typeButtons = new[] { "课程", "考试", "待办", "个人活动" }.Select(text => Choice(text)).ToArray();
        foreach (var b in typeButtons) { types.Children.Add(b); b.Click += (_, _) => typeLayer.Select(b); }
        page.Children.Add(typeLayer);
        page.Children.Add(Theme.Space(18));
        var form = new StackPanel();
        form.Children.Add(Theme.Field("标题",new TextBox { Text="完成本周课程笔记" }));
        var dateLabel = Theme.Text("2026 / 10 / 25",18,bold:true);
        form.Children.Add(dateLabel);
        form.Children.Add(Theme.Space(10));
        var calendar = new UniformGrid { Columns=7 };
        var calendarLayer = new GlassSelectionLayer(calendar,11);
        foreach (var day in new[] { "一", "二", "三", "四", "五", "六", "日" })
        { var label=Theme.Text(day,12,Theme.Muted); label.HorizontalAlignment=HorizontalAlignment.Center;label.Margin=new Thickness(0,5,0,10);calendar.Children.Add(label); }
        for(int i=0;i<3;i++)calendar.Children.Add(new Border());
        Button? selectedDate=null;
        for(int day=1;day<=31;day++)
        {
            int value=day;var b=Choice(day.ToString());b.Height=39;calendar.Children.Add(b);
            if(day==25)selectedDate=b;
            b.Click+=(_,_)=>{calendarLayer.Select(b);dateLabel.Text=$"2026 / 10 / {value:00}";};
        }
        form.Children.Add(calendarLayer);
        form.Children.Add(Theme.Space(12));
        var reminder=new CheckBox { Content="使用默认提醒",IsChecked=true };
        // Only preview-local styling; selection geometry and animation come from Shike.
        reminder.Template=(ControlTemplate)System.Windows.Markup.XamlReader.Parse("""
        <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="CheckBox">
          <StackPanel Orientation="Horizontal"><Border Width="21" Height="21" CornerRadius="5" BorderThickness="1" BorderBrush="#698675" Background="#30FFFFFF" Margin="0,0,10,0"><TextBlock x:Name="Tick" Text="✓" Foreground="#28725B" HorizontalAlignment="Center" VerticalAlignment="Center" Opacity="0"/></Border><ContentPresenter VerticalAlignment="Center"/></StackPanel>
          <ControlTemplate.Triggers><Trigger Property="IsChecked" Value="True"><Setter TargetName="Tick" Property="Opacity" Value="1"/></Trigger></ControlTemplate.Triggers>
        </ControlTemplate>
        """);
        form.Children.Add(reminder);
        page.Children.Add(Theme.Card(form));
        var tasks=new StackPanel();var taskLayer=new GlassSelectionLayer(tasks,12);
        var first=Choice("完成本周课程笔记     ·     14:00 – 15:30");
        var second=Choice("整理参考资料             ·     尚未完成");
        foreach(var b in new[]{first,second}) { b.HorizontalContentAlignment=HorizontalAlignment.Left;b.Height=48;tasks.Children.Add(b);b.Click+=(_,_)=>taskLayer.Select(b); }
        page.Children.Add(Theme.Text("今日待办",15,bold:true));page.Children.Add(Theme.Space(7));page.Children.Add(taskLayer);
        window.Loaded+=async(_,_)=>
        {
            typeLayer.Select(typeButtons[2]);calendarLayer.Select(selectedDate);taskLayer.Select(first);
            if(!args.Contains("--verify"))return;
            try
            {
                await Task.Delay(650);
                var initial=((TranslateTransform)typeLayer.Frame.RenderTransform).X;
                typeLayer.Select(typeButtons[0]);
                await Task.Delay(180);
                var middle=((TranslateTransform)typeLayer.Frame.RenderTransform).X;
                if(!(middle>0 && middle<initial))throw new Exception("Selection did not interpolate between positions.");
                typeLayer.Select(typeButtons[3]);
                await Task.Delay(650);
                var expected=typeButtons[3].TranslatePoint(new Point(),typeLayer).X;
                if(Math.Abs(((TranslateTransform)typeLayer.Frame.RenderTransform).X-expected)>.5)throw new Exception("Selection did not settle on the retargeted control.");
                var output=Path.Combine(AppContext.BaseDirectory,"verification");Directory.CreateDirectory(output);
                Theme.Screenshot((FrameworkElement)window.Content,Path.Combine(output,"preview.png"));
                File.WriteAllText(Path.Combine(output,"result.txt"),"PASS: original GlassSelectionLayer interpolates and retargets; original GlassTheme.Selection renders.");
            }
            catch(Exception ex){File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"preview-error.txt"),ex.ToString());Environment.ExitCode=1;}
            finally{window.Close();}
        };
        app.Run(window);
    }
    static Button Choice(string text) => new() { Content=text, Height=45, Margin=new Thickness(2), Padding=new Thickness(12,6,12,6), Background=Brushes.Transparent, BorderThickness=new Thickness(0), HorizontalContentAlignment=HorizontalAlignment.Center };
}
