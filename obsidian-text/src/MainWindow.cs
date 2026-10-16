using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using static Shike.Theme;

namespace Shike;

public sealed partial class MainWindow : Window
{
    readonly Store store;
    readonly bool preview;
    readonly Grid shell = new();
    readonly ContentControl body = new();
    readonly StackPanel navigation = new();
    readonly TextBlock heading = Text("", 27, bold: true), subtitle = Text("", 12, Muted);
    readonly TextBlock footer = Text("", 11, Muted);
    readonly TextBox search = new() { Width = 220, ToolTip = "搜索标题、地点与笔记；输入后跨日期查找", VerticalAlignment = VerticalAlignment.Center };
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(15) };
    readonly System.Windows.Forms.NotifyIcon? tray;
    readonly System.Drawing.Icon? trayIcon;
    DateTime date = DateTime.Today;
    string view;
    bool exiting;
    readonly string[] views = { "今日概览", "今日时间轴", "周课表", "待办清单", "笔记与附件", "考试安排", "校园活动", "冲突与提醒", "校园助手", "设置与备份" };
    readonly string[] symbols = { "◈", "◷", "▦", "☑", "▤", "◇", "❋", "◉", "⇄", "⚙" };
    public MainWindow(Store store, bool preview = false)
    {
        this.store = store; this.preview = preview; view = store.Data.Settings.LastView;
        if (view == "学校连接") view = "校园助手";
        if (!views.Contains(view)) view = views[0];
        Title = "拾课 · 本地校园日程"; Width = 1330; Height = 870; MinWidth = 1060; MinHeight = 650;
        Background = Cream; Foreground = Ink; FontFamily = new FontFamily("Microsoft YaHei UI"); FontSize = 13;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var iconUri = new Uri("pack://application:,,,/Shike;component/Shike.ico", UriKind.Absolute);
        Icon = System.Windows.Media.Imaging.BitmapFrame.Create(iconUri);
        if (!preview) SetFullScreen(true);
        if (!preview) AcademicCalendar.MigrateCached(store);
        PreviewMouseLeftButtonDown += (_, e) => { if (quickNoteKey != null) { ExitQuickAssociation(); e.Handled = true; } };
        BuildShell(); Render();
        if (!preview)
        {
            using (var iconStream = Application.GetResourceStream(iconUri).Stream)
            using (var embeddedIcon = new System.Drawing.Icon(iconStream))
                trayIcon = (System.Drawing.Icon)embeddedIcon.Clone();
            tray = new System.Windows.Forms.NotifyIcon { Icon = trayIcon, Text = "拾课 · 本地日程提醒", Visible = true };
            tray.DoubleClick += (_, _) => Dispatcher.Invoke(RestoreWindow);
            var menu = new System.Windows.Forms.ContextMenuStrip();
            menu.Items.Add("打开拾课", null, (_, _) => Dispatcher.Invoke(RestoreWindow));
            menu.Items.Add("退出（停止提醒）", null, (_, _) => Dispatcher.Invoke(Exit)); tray.ContextMenuStrip = menu;
            tray.BalloonTipClicked += (_, _) => Dispatcher.Invoke(() => { RestoreWindow(); SwitchView("冲突与提醒"); });
            timer.Tick += (_, _) => { UpdateFreshness(); Tick(); _ = AssistantTick(); }; timer.Start();
            Loaded += async (_, _) => { Tick(); if (store.Data.Settings.AutoCampusRefresh) _ = RefreshCampus(); if (store.Data.Assistant.AutoRefresh) _ = RefreshAssistant(false); await StartupSync(); };
        }
        Closing += (_, e) => { if (!exiting) { e.Cancel = true; Hide(); tray?.ShowBalloonTip(3500, "拾课仍在运行", "已收起到托盘，提醒继续。右键托盘图标可退出。", System.Windows.Forms.ToolTipIcon.Info); } };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.N && Keyboard.Modifiers == ModifierKeys.Control) { NewCurrentItem(); e.Handled = true; } if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control) { search.Focus(); e.Handled = true; } };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.F11 && Keyboard.Modifiers == ModifierKeys.None)
            { SetFullScreen(WindowStyle != WindowStyle.None); e.Handled = true; }
            else if (e.Key == Key.Escape && WindowStyle == WindowStyle.None)
            { SetFullScreen(false); e.Handled = true; }
        };
    }
    void BuildShell()
    {
        shell.Background = Cream;
        shell.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(208) }); shell.ColumnDefinitions.Add(new ColumnDefinition());
        var side = new DockPanel { Margin = new Thickness(20, 28, 16, 20) };
        var brand = Stack(Text("拾课", 30, Brush("#EDF6EF"), true), Space(5), Text("SHIKE  /  CAMPUS PLANNER", 9, Brush("#A8BDB2")), Space(33));
        DockPanel.SetDock(brand, Dock.Top); side.Children.Add(brand);
        var local = Stack(Text("●  仅存于此电脑", 12, Brush("#C3D9CA")), Space(8), Text("无需云端 · 自己掌握每一天", 10, Brush("#8DA89B")), Space(17), Button("退出程序", Exit));
        DockPanel.SetDock(local, Dock.Bottom); side.Children.Add(local); side.Children.Add(new ScrollViewer { Content = navigation });
        shell.Children.Add(new Border { Background = Brush("#183C32"), Child = side });
        var main = new DockPanel { Margin = new Thickness(30, 26, 30, 18) }; Grid.SetColumn(main, 1); shell.Children.Add(main);
        var top = new DockPanel { Margin = new Thickness(0, 0, 0, 22) };
        var searchBox = new Grid(); searchBox.Children.Add(search);
        var hint = Text("搜索标题、地点、笔记", 11, Muted); hint.Margin = new Thickness(11, 0, 0, 0); hint.IsHitTestVisible = false; searchBox.Children.Add(hint);
        search.TextChanged += (_, _) => hint.Visibility = search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        var actions = Row(searchBox, new Border { Width = 12 }, Button("＋ 新建", NewCurrentItem, true)); DockPanel.SetDock(actions, Dock.Right); top.Children.Add(actions);
        top.Children.Add(Stack(heading, Space(6), subtitle)); DockPanel.SetDock(top, Dock.Top); main.Children.Add(top);
        footer.Margin = new Thickness(0, 9, 0, 0); DockPanel.SetDock(footer, Dock.Bottom); main.Children.Add(footer);
        main.Children.Add(body); Content = shell;
        search.TextChanged += (_, _) => Render();
    }
    void NewCurrentItem() { if (view == "笔记与附件") EditKnowledgeNote(null); else Edit(null); }
    void SetFullScreen(bool enabled)
    {
        WindowState = WindowState.Normal;
        WindowStyle = enabled ? WindowStyle.None : WindowStyle.SingleBorderWindow;
        ResizeMode = enabled ? ResizeMode.NoResize : ResizeMode.CanResize;
        if (enabled) WindowState = WindowState.Maximized;
    }
    void RestoreWindow()
    {
        Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowStyle == WindowStyle.None ? WindowState.Maximized : WindowState.Normal;
        Activate();
    }
    public void Exit() { exiting = true; DisconnectAssistant(); syncGeneration++; timer.Stop(); tray?.Dispose(); trayIcon?.Dispose(); Close(); Application.Current.Shutdown(); }
    public void SwitchView(string name)
    {
        if (name == "学校连接") name = "校园助手";
        ExitQuickAssociation(); view = name; search.Text = ""; store.Data.Settings.LastView = name;
        if (!preview) store.Save(); Render();
    }
    public bool Commit(Action action)
    {
        var before = JsonSerializer.Serialize(store.Data);
        try { action(); store.Save(); Render(); return true; }
        catch (Exception ex) { store.Data = JsonSerializer.Deserialize<AppData>(before)!; MessageBox.Show(this, "未保存：" + ex.Message, "拾课", MessageBoxButton.OK, MessageBoxImage.Error); return false; }
    }
    void Render()
    {
        navigation.Children.Clear();
        for (int n = 0; n < views.Length; n++)
        {
            string target = views[n];
            if (n is 3 or 8) navigation.Children.Add(Space(15));
            var button = Button(symbols[n] + "    " + (target == "笔记与附件" ? "知识笔记" : target), () => SwitchView(target));
            button.HorizontalContentAlignment = HorizontalAlignment.Left; button.Margin = new Thickness(0, 0, 0, 5); button.Padding = new Thickness(14, 12, 4, 12);
            button.Background = target == view ? Brush("#31594A") : Brushes.Transparent;
            button.Foreground = target == view ? Brushes.White : Brush("#AFC5B9"); button.BorderThickness = new Thickness(0);
            navigation.Children.Add(button);
        }
        heading.Text = view;
        subtitle.Text = view switch { "今日概览" => "给日程留好位置，也给生活留一点空白。", "校园助手" => "课程待办 · 借阅 · 签到  /  读取后保存到本地", "设置与备份" => "你的记录、附件与选择，都留在这台电脑。", _ => "课程 · 考试 · 生活，一处安排。" };
        UpdateFreshness();
        if (view == "校园活动") { RenderCampus(); return; }
        if (view == "笔记与附件") { RenderNotes(); return; }
        if (!string.IsNullOrWhiteSpace(search.Text)) { RenderSearch(); return; }
        switch (view)
        {
            case "今日概览": RenderOverview(); break;
            case "今日时间轴": RenderCalendar(false); break;
            case "周课表": RenderCalendar(true); break;
            case "校园助手": RenderAssistant(); break;
            case "设置与备份": RenderSettings(); break;
            case "冲突与提醒": RenderConflicts(); break;
            default: RenderList(); break;
        }
    }
    StackPanel Page() { var p = new StackPanel(); body.Content = new ScrollViewer { Content = p }; return p; }
    IEnumerable<Occurrence> On(DateTime d) => Engine.Between(store.Data.Items.Where(i => (i.Kind != "待办" || i.AssistantKey != null && !i.Done)), d.Date, d.Date.AddDays(1)).OrderBy(o => o.Start);
    UIElement DateBar(bool week = false)
    {
        var bar = new DockPanel { Margin = new Thickness(0, 0, 0, 18) };
        var picker = new DatePicker { SelectedDate = date, Width = 195, Margin = new Thickness(8, 0, 0, 0) };
        picker.SelectedDateChanged += (_, _) => { if (picker.SelectedDate is DateTime selected) { date = selected; Render(); } };
        var buttons = Row(Button("‹", () => { date = date.AddDays(week ? -7 : -1); Render(); }), Button("今天", () => { date = DateTime.Today; Render(); }), Button("›", () => { date = date.AddDays(week ? 7 : 1); Render(); }), picker);
        DockPanel.SetDock(buttons, Dock.Right); bar.Children.Add(buttons);
        var first = week ? Engine.Monday(date) : date;
        bar.Children.Add(Text(week ? $"{first:MM月dd日} — {first.AddDays(6):MM月dd日}" : date.ToString("M月d日 · dddd", CultureInfo.GetCultureInfo("zh-CN")), 19, bold: true)); return bar;
    }
    UIElement Empty(string title, string description, Action? action = null, string label = "＋ 添加第一项")
    {
        var content = Stack(Text("○", 32, Green), Space(12), Text(title, 17, bold: true), Space(8), Text(description, 12, Muted));
        if (action != null) { content.Children.Add(Space(18)); var b = Button(label, action, true); b.HorizontalAlignment = HorizontalAlignment.Left; content.Children.Add(b); }
        return Card(content, padding: 26);
    }
    UIElement Stat(string value, string title, string detail, string color)
    {
        return Card(Stack(Text(title, 12, Muted), Space(12), Text(value, 32, Brush(color), true), Space(6), Text(detail, 11, Muted)), padding: 18);
    }
    void RenderOverview()
    {
        var p = Page(); p.Children.Add(DateBar()); p.Children.Add(HolidayBanner(date));
        var today = On(date).ToList(); var tasks = store.Data.Items.Where(i => i.Kind == "待办" && !i.Done && !i.Cancelled && i.Start.Date <= date.Date).OrderBy(i => i.Start).ToList();
        var exams = store.Data.Items.Count(i => i.Kind == "考试" && !i.Cancelled && i.Start >= DateTime.Today && i.Start < DateTime.Today.AddDays(14));
        var stats = new Grid(); for (int i = 0; i < 3; i++) stats.ColumnDefinitions.Add(new ColumnDefinition());
        var cards = new[] { Stat(today.Count.ToString("00"), "当日安排", "课程与个人活动，一起查看", "#28725B"), Stat(tasks.Count.ToString("00"), "待完成事项", "包含之前尚未完成的待办", "#517CA4"), Stat(exams.ToString("00"), "未来两周考试", "为准备留出时间", "#B86E34") };
        for (int i = 0; i < 3; i++) { ((FrameworkElement)cards[i]).Margin = new Thickness(0, 0, i < 2 ? 14 : 0, 18); Grid.SetColumn(cards[i], i); stats.Children.Add(cards[i]); } p.Children.Add(stats);
        if (store.Data.Items.Any(i => i.HasConflict)) p.Children.Add(ConflictBanner());
        var columns = new Grid(); columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.35, GridUnitType.Star) }); columns.ColumnDefinitions.Add(new ColumnDefinition());
        var left = new StackPanel { Margin = new Thickness(0, 0, 20, 0) }; var right = new StackPanel(); Grid.SetColumn(right, 1); columns.Children.Add(left); columns.Children.Add(right); p.Children.Add(columns);
        left.Children.Add(Section("日程一览", "查看时间轴", () => SwitchView("今日时间轴")));
        if (today.Count == 0) left.Children.Add(Empty("今天，留白待填", "添加一次面试、一场活动，或连接学校课表。", () => Edit(null)));
        else foreach (var o in today) left.Children.Add(ItemCard(o, today.Any(x => Engine.Overlaps(o, x))));
        right.Children.Add(Section("待办事项", "全部待办", () => SwitchView("待办清单")));
        if (tasks.Count == 0) right.Children.Add(Empty("暂时没有到期的待办", "把需要准备的事情记下来，让大脑轻一点。", () => Edit(null, "待办"), "＋ 添加待办"));
        else foreach (var i in tasks.Take(8)) right.Children.Add(TaskCard(i));
        var memo = Stack(Text("随手记，也能有所归属", 16, bold: true), Space(9), Text("在日程详情中保存准备笔记和附件。面试前，材料就在手边。", 12, Muted), Space(15), Button("＋ 写一条笔记", () => Edit(null, "笔记")));
        right.Children.Add(Card(memo, "#EDF2E9"));
    }
    UIElement Section(string title, string action, Action click)
    {
        var bar = new DockPanel { Margin = new Thickness(0, 0, 0, 14) }; var b = Button(action + " →", click); b.Background = Brushes.Transparent; b.BorderThickness = new Thickness(0); b.Padding = new Thickness(5);
        DockPanel.SetDock(b, Dock.Right); bar.Children.Add(b); bar.Children.Add(Text(title, 16, bold: true)); return bar;
    }
    UIElement ItemCard(Occurrence o, bool overlap = false)
    {
        var i = o.Item;
        var content = new DockPanel(); var time = Stack(Text(o.Start.ToString("HH:mm"), 18, Kind(i.Kind), true), Space(4), Text(o.End.ToString("HH:mm"), 11, Muted));
        time.Width = 67; DockPanel.SetDock(time, Dock.Left); content.Children.Add(time);
        var edit = Button("查看", () => Edit(o)); edit.Margin = new Thickness(8, 0, 0, 0); edit.Padding = new Thickness(10, 7, 10, 7); DockPanel.SetDock(edit, Dock.Right); content.Children.Add(edit);
        content.Children.Add(Stack(Text(i.Title, 14, bold: true), Space(5), Text(i.Kind + " · " + (i.Location.Length > 0 ? i.Location : "地点待公布") + (i.SourceKey != null ? " · 学校" : "") + (i.LocalOverride ? " · 本地调整" : ""), 11, Muted), i.SchoolAdjustment.Length > 0 ? Text(i.SchoolAdjustment, 11, Green) : Space(0), overlap ? Text("时间重叠 · 请自行安排", 11, Brush("#AE6B32")) : Space(0)));
        return Card(content, padding: 17);
    }
    UIElement TaskCard(Item i)
    {
        var row = new DockPanel(); var box = new CheckBox { IsChecked = i.Done, ToolTip = "标记完成", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        box.Click += (_, _) => Commit(() => i.Done = box.IsChecked == true); DockPanel.SetDock(box, Dock.Left); row.Children.Add(box);
        var b = Button("编辑", () => Edit(new(i, i.Start, i.End))); b.Padding = new Thickness(8, 5, 8, 5); DockPanel.SetDock(b, Dock.Right); row.Children.Add(b);
        row.Children.Add(Stack(Text(i.Title, 13, bold: true), Space(6), Text((i.Done ? "已完成 · " : i.Start < DateTime.Now ? "已到期 · " : "截止 · ") + i.Start.ToString("MM-dd HH:mm"), 11, Muted))); return Card(row, padding: 15);
    }
    void RenderList()
    {
        var p = Page(); var kind = view == "待办清单" ? "待办" : view == "笔记与附件" ? "笔记" : "考试";
        p.Children.Add(Section(kind == "笔记" ? "独立笔记与所有附件" : "全部" + kind, "新建" + kind, () => Edit(null, kind)));
        var items = store.Data.Items.Where(i => !i.Cancelled && (i.Kind == kind || kind == "笔记" && (i.Note.Length > 0 || i.Attachments.Count > 0))).OrderBy(i => i.Done).ThenBy(i => i.Start).ToList();
        if (items.Count == 0) p.Children.Add(Empty("这里还没有" + kind, kind == "考试" ? "连接教务系统读取考试安排，也可以手动添加。" : "你的记录会保存在这台电脑上。", () => Edit(null, kind)));
        foreach (var i in items)
        {
            if (kind == "待办") { p.Children.Add(TaskCard(i)); continue; }
            var note = i.Note.Length > 150 ? i.Note[..150] + "…" : i.Note;
            var content = Stack(Text(i.Title, 16, bold: true), Space(7), Text(kind == "考试" ? $"{i.Start:yyyy-MM-dd HH:mm} — {i.End:HH:mm}  ·  {i.Location}" : i.Kind + " · " + i.Modified.ToString("MM-dd HH:mm") + " · " + i.Attachments.Count + " 个附件", 12, Muted), Space(10), Text(note, 12, Muted), Space(12), Button("打开详情", () => Edit(new(i, i.Start, i.End))));
            p.Children.Add(Card(content));
        }
    }
    void RenderSearch()
    {
        var p = Page(); string query = search.Text.Trim();
        var items = store.Data.Items.Where(i => (i.Title + " " + i.Location + " " + i.Note).Contains(query, StringComparison.OrdinalIgnoreCase)).OrderBy(i => i.Start).ToList();
        p.Children.Add(Text($"搜索结果 · {items.Count} 项（包括已取消安排）", 16, bold: true)); p.Children.Add(Space());
        foreach (var i in items) p.Children.Add(Card(Stack(Text(i.Title + (i.Cancelled ? "（已取消）" : i.SchoolSuspended && !i.LocalOverride ? "（学校停课）" : ""), 16, bold: true), Space(8), Text($"{i.Kind} · {i.Start:yyyy-MM-dd HH:mm} · {i.Location}", 12, Muted), Space(12), Button("查看", () => Edit(new(i, i.Start, i.End))))));
        if (items.Count == 0) p.Children.Add(Empty("没有找到匹配记录", "试试课程名称、地点或笔记中的词语。"));
    }
    UIElement ConflictBanner() => Card(Row(Text("!  学校更新与你的本地修改存在冲突", 13, Brush("#A1642B"), true), new Border { Width = 20 }, Button("查看并处理", () => SwitchView("冲突与提醒"))), "#FFF5E6", 14);
    void RenderConflicts()
    {
        var p = Page(); var conflicts = store.Data.Items.Where(i => i.HasConflict).ToList();
        p.Children.Add(Text("学校更新冲突", 17, bold: true)); p.Children.Add(Space());
        if (conflicts.Count == 0) p.Children.Add(Empty("没有待处理的学校更新冲突", "更新不会静默覆盖你的个人修改。"));
        foreach (var i in conflicts)
        {
            var content = Stack(Text(i.Title, 16, bold: true), Space(10), Text($"你的安排：{i.Start:MM-dd HH:mm}—{i.End:HH:mm} · {i.Location}"), Space(7), Text(i.PendingRemoval ? "学校最新：该安排已从此次完整列表中移除" : $"学校最新：{i.Pending!.Title} · {i.Pending.Start:MM-dd HH:mm}—{i.Pending.End:HH:mm} · {i.Pending.Location}\n{i.Pending.Adjustment}", 13, Brush("#A1642B")), Space(15), Row(Button("采用学校安排", () => Commit(() => SchoolMerge.Resolve(i, true)), true), Button("保留我的安排", () => Commit(() => SchoolMerge.Resolve(i, false)))));
            p.Children.Add(Card(content));
        }
        p.Children.Add(Space()); p.Children.Add(Text("未来 7 天的时间重叠", 17, bold: true)); p.Children.Add(Space());
        var occurrences = Engine.Between(store.Data.Items.Where(i => i.Kind is not ("待办" or "笔记")), DateTime.Today, DateTime.Today.AddDays(7)).OrderBy(o => o.Start).ToList(); int count = 0;
        for (int a = 0; a < occurrences.Count; a++) for (int b = a + 1; b < occurrences.Count && occurrences[b].Start < occurrences[a].End; b++)
            if (Engine.Overlaps(occurrences[a], occurrences[b]) && count++ < 40)
                p.Children.Add(Card(Text($"{occurrences[a].Start:MM-dd HH:mm}  ·  {occurrences[a].Item.Title}  /  {occurrences[b].Item.Title}", 13, Brush("#A1642B")), "#FFF5E6", 14));
        if (count == 0) p.Children.Add(Text("没有发现时间重叠。", 12, Muted));
        p.Children.Add(Space(22)); p.Children.Add(Section("提醒记录", "清空记录", () => Commit(() => store.Data.Alerts.Clear())));
        foreach (var message in store.Data.Alerts.AsEnumerable().Reverse().Take(100)) p.Children.Add(Card(Text(message, 12), padding: 14));
        if (store.Data.Alerts.Count == 0) p.Children.Add(Text("提醒触发后会显示在这里。关闭窗口后，托盘运行时仍可提醒；退出程序或关机后不提醒。", 12, Muted));
    }
    void Tick()
    {
        var due = Engine.Due(store.Data, DateTime.Now); if (due.Count == 0) return;
        var now = DateTime.Now;
        if (Commit(() =>
        {
            foreach (var d in due) { store.Data.Fired.Add(d.Key); store.Data.Alerts.Add($"{now:MM-dd HH:mm}  ·  {d.Occ.Item.Title}  ·  安排于 {d.Occ.Start:MM-dd HH:mm}  {d.Occ.Item.Location}"); }
            if (store.Data.Alerts.Count > 500) store.Data.Alerts = store.Data.Alerts.TakeLast(500).ToList();
        })) tray?.ShowBalloonTip(8000, due.Count == 1 ? "日程提醒 · " + due[0].Occ.Item.Title : $"有 {due.Count} 项日程提醒", $"{due[0].Occ.Start:MM-dd HH:mm}  {due[0].Occ.Item.Location}\n点击查看提醒记录。", System.Windows.Forms.ToolTipIcon.Info);
    }
    void RenderSettings()
    {
        var p = Page(); var s = store.Data.Settings;
        var courses = new CheckBox { Content = "开启课程默认提醒", IsChecked = s.CourseReminder };
        var minutes = new TextBox { Text = s.CourseMinutes.ToString(), Width = 120, HorizontalAlignment = HorizontalAlignment.Left };
        var exam = new TextBox { Text = s.ExamMinutes };
        p.Children.Add(Card(Stack(Text("提醒偏好", 18, bold: true), Space(10), courses, Field("课程提前分钟数", minutes), Field("考试提前分钟数（逗号分隔；1440,60 = 提前一天和一小时；留空关闭）", exam), Text("每个事项可以覆盖默认设置。提醒仅在程序运行或驻留托盘时触发；休眠期间不会唤醒电脑。重新运行时补发最近 24 小时内错过的提醒。Windows 勿扰模式可能隐藏弹窗，提醒记录仍会保留。", 12, Muted), Space(15), Button("保存提醒设置", () =>
        {
            if (!int.TryParse(minutes.Text, out int n) || n < 0 || n > 525600) { MessageBox.Show(this, "课程提前时间应为 0～525600 分钟。"); return; }
            Commit(() => { Engine.ParseReminders(exam.Text); s.CourseReminder = courses.IsChecked == true; s.CourseMinutes = n; s.ExamMinutes = exam.Text; });
        }, true))));
        p.Children.Add(Card(Stack(Text("本地数据与备份", 18, bold: true), Space(12), Text("笔记、附件、日程与加密凭据存储位置", 12, Muted), Space(6), Text(store.Root, 12), Space(14), Row(Button("打开数据文件夹", () => Process.Start(new ProcessStartInfo(store.Root) { UseShellExecute = true })), Button("导出完整备份", ExportBackup), Button("从备份恢复", ImportBackup)), Space(12), Text("备份包含日程、笔记与附件，不包含加密账号密码。恢复前自动保存当前数据的完整备份。凭据保存和启动更新可在“校园助手 → 连接与设置”管理。请把软件放在未启用云同步的本地目录。", 12, Muted))));
        p.Children.Add(Card(Stack(Text("关于拾课 · Windows 预览版 0.7.5", 18, bold: true), Space(10), Text("本地优先的校园日程工具。不会自动排程；没有遥测、云端账号或云同步。校园助手访问学校服务，Pintia 为可选连接，支持每次启动自动读取。", 12, Muted), Space(10), Text("后续扩展：已定义学校数据提供器接口；插件安装与管理界面尚未实现。", 12, Muted))));
    }
    void ExportBackup()
    {
        var dialog = new SaveFileDialog { Filter = "拾课完整备份 (*.zip)|*.zip", FileName = "拾课备份-" + DateTime.Now.ToString("yyyyMMdd-HHmm") + ".zip" };
        if (dialog.ShowDialog(this) == true) try { store.Backup(dialog.FileName); MessageBox.Show(this, "备份已保存，包含日程、笔记和附件。"); } catch (Exception ex) { MessageBox.Show(this, "备份未完成：" + ex.Message); }
    }
    void ImportBackup()
    {
        var dialog = new OpenFileDialog { Filter = "拾课完整备份 (*.zip)|*.zip" };
        if (dialog.ShowDialog(this) != true) return;
        if (MessageBox.Show(this, "恢复会替换当前记录。系统将先保留一份恢复前备份，是否继续？", "恢复本地备份", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try { DisconnectAssistant(); pintiaSession = null; syncGeneration++; store.Restore(dialog.FileName); PeriodSchedule.Migrate(store); NotesModel.Migrate(store); AcademicCalendar.ApplyCached(store.Data); store.Save(); Render(); MessageBox.Show(this, "恢复完成。原数据的备份保存在 data 文件夹。账号密码不在备份中。"); } catch (Exception ex) { MessageBox.Show(this, "未恢复：" + ex.Message); }
    }
}
