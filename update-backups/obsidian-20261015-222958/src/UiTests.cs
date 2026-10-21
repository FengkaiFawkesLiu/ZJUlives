using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Shike;

public sealed partial class MainWindow
{
    static IEnumerable<T> Visuals<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typed) yield return typed;
            foreach (var descendant in Visuals<T>(child)) yield return descendant;
        }
    }
    public void RunUiTests(string dir)
    {
        var results = new List<string>();
        void Check(bool condition, string text) { results.Add((condition ? "PASS " : "FAIL ") + text); File.WriteAllLines(Path.Combine(dir, "ui-test-results.txt"), results); if (!condition) throw new Exception(text); }
        void Modal(Action invoke, Action<Window> operate)
        {
            Exception? error = null; bool operated = false;
            var pending = Dispatcher.BeginInvoke(new Action(() =>
            {
                var dialog = OwnedWindows.Cast<Window>().Last();
                try { operated = true; dialog.UpdateLayout(); operate(dialog); }
                catch (Exception ex) { error = ex; dialog.Tag = "test-abort"; dialog.Close(); }
            }), DispatcherPriority.ApplicationIdle);
            invoke(); if (!operated) { pending.Abort(); throw new Exception("Test dialog did not open synchronously"); } if (error != null) throw error;
        }
        void Click(Window window, string label) => Visuals<Button>(window).First(b => b.Content is string s && s == label).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        try
        {
            if (Environment.GetCommandLineArgs().Contains("--floating-only")) { RunFloatingFormulaTests(dir, Check, Modal, Click); return; }
            SetFullScreen(true); UpdateLayout();
            Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            var screen = System.Windows.Forms.Screen.FromHandle(new System.Windows.Interop.WindowInteropHelper(this).Handle).Bounds;
            var origin = PointToScreen(new Point(0, 0));
            var edge = PointToScreen(new Point(ActualWidth, ActualHeight));
            Check(Math.Abs(origin.X - screen.Left) <= 1 && Math.Abs(origin.Y - screen.Top) <= 1
                && Math.Abs(edge.X - screen.Right) <= 1 && Math.Abs(edge.Y - screen.Bottom) <= 1,
                "Fullscreen covers monitor including taskbar area");
            Hide(); RestoreWindow();
            Check(IsVisible && WindowState == WindowState.Maximized && WindowStyle == WindowStyle.None,
                "Tray restore preserves fullscreen");
            SetFullScreen(false); UpdateLayout();
            Check(WindowState == WindowState.Normal && WindowStyle == WindowStyle.SingleBorderWindow
                && ResizeMode == ResizeMode.CanResize, "Exit fullscreen restores resizable window");
            void Press(System.Windows.Input.Key key) => RaiseEvent(new System.Windows.Input.KeyEventArgs(
                System.Windows.Input.Keyboard.PrimaryDevice, PresentationSource.FromVisual(this), 0, key)
                { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent });
            Press(System.Windows.Input.Key.F11);
            Check(WindowStyle == WindowStyle.None, "F11 enters fullscreen");
            Press(System.Windows.Input.Key.Escape);
            Check(WindowStyle == WindowStyle.SingleBorderWindow, "Escape exits fullscreen");
            if (Environment.GetCommandLineArgs().Contains("--fullscreen-test")) return;
            Modal(() => Edit(null, "活动", DateTime.Today.AddDays(2).AddHours(14)), window =>
            {
                var title = Visuals<TextBox>(window).First(t => t.MaxLength == 300); title.Text = "界面测试：社团面试";
                var note = Visuals<TextBox>(window).First(t => t.AcceptsReturn); note.Text = "准备一分钟自我介绍。";
                window.UpdateLayout(); Theme.Screenshot((FrameworkElement)window.Content, Path.Combine(dir, "事项编辑.png"));
                var wheels = Visuals<TimeWheel>(window).ToList();
                Check(wheels.Count == 2, "Editor exposes separate start and end wheels");
                wheels[0].SetTime(23, 59); wheels[0].StepHours(1); wheels[0].StepMinutes(1);
                Check(wheels[0].Value == TimeSpan.Zero, "Clock wheels wrap independently");
                wheels[0].SetTime(14, 25); wheels[1].SetTime(15, 40);
                Check(Visuals<DatePicker>(window).First().ActualWidth >= 230, "Editor date input is enlarged");
                Click(window, "保存");
            });
            Check(store.Data.Items.Count == 1 && store.Data.Items[0].Title == "界面测试：社团面试", "Create event via real editor Save button: " + string.Join(",", store.Data.Items.Select(i => i.Title)));
            Check(new Store(store.Root).Data.Items[0].Note == "准备一分钟自我介绍。", "Editor save persisted note to disk");
            var original = store.Data.Items[0];
            Check(original.Start.Minute == 25 && original.End.Minute == 40, "Wheel selections persist through Save");
            Modal(() => Edit(new(original, original.Start, original.End)), window => { Visuals<TextBox>(window).First(t => t.MaxLength == 300).Text = "改期后的社团面试"; Click(window, "保存"); });
            Check(store.Data.Items.Count == 1 && store.Data.Items[0].Title == "改期后的社团面试", "Edit replaces existing record without duplication");
            Modal(() => Edit(null, "笔记"), window => { Click(window, "取消"); });
            Check(store.Data.Items.Count == 1, "Cancel editor leaves database unchanged");
            Modal(() => Edit(null, "待办", DateTime.Today.AddHours(18)), window => { Visuals<TextBox>(window).First(t => t.MaxLength == 300).Text = "整理面试材料"; Click(window, "保存"); });
            SwitchView("待办清单"); UpdateLayout();
            var checkbox = Visuals<CheckBox>(body).First(); checkbox.IsChecked = true; checkbox.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Check(store.Data.Items.Single(i => i.Kind == "待办").Done, "Task checkbox marks task complete and persists");
            Modal(() => Edit(null, "笔记"), window => { Visuals<TextBox>(window).First(t => t.MaxLength == 300).Text = "面试复盘"; Visuals<RichTextBox>(window).Single().Document.Blocks.Add(new System.Windows.Documents.Paragraph(new System.Windows.Documents.Run("下次准备更充分。"))); Click(window, "保存"); });
            SwitchView("笔记与附件"); UpdateLayout(); Check(Visuals<TextBlock>(body).Any(t => t.Text == "面试复盘"), "Independent note appears in notes view");
            SwitchView("今日概览"); search.Text = "改期"; UpdateLayout(); Check(Visuals<TextBlock>(body).Any(t => t.Text == "改期后的社团面试"), "Search finds event across dates");
            foreach (var name in views) { SwitchView(name); UpdateLayout(); Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle); }
            Check(true, "All nine navigation destinations render with actual persisted data");
            assistantTab = 3; SwitchView("校园助手"); UpdateLayout();
            Check(Visuals<CheckBox>(body).Any(c => c.Content is string text && text.Contains("每次启动") && c.IsChecked == true), "Startup refresh preference visible and enabled by default");
            Modal(SchoolDialog, window =>
            {
                Check(Visuals<CheckBox>(window).Any(c => c.Content is string text && text.Contains("加密保存") && c.IsChecked == false), "Credential storage is opt-in for a fresh profile");
                window.Close();
            });
            var activity = new CampusActivity { Title = "测试活动 · 非真实通知", Url = "https://cds.zju.edu.cn/a/test.html", Source = "测试", Category = "讲座", Start = DateTime.Today.AddDays(9).AddHours(10), Location = "测试教室", Description = "测试内容", Fetched = DateTime.Now };
            store.Data.CampusActivities.Add(activity); store.Save();
            SwitchView("校园活动"); UpdateLayout();
            Check(Visuals<TextBlock>(body).Any(t => t.Text == activity.Title), "Cached activity appears without network");
            Modal(() => Click(this, "＋ 加入日程"), window => { Check(Visuals<TextBox>(window).First(t => t.MaxLength == 300).Text == activity.Title, "Activity title prefilled into editor"); Click(window, "保存"); });
            Check(store.Data.Items.Count(i => i.CampusUrl == activity.Url) == 1, "Activity converted to personal calendar once");
            SwitchView("校园活动"); UpdateLayout(); Check(Visuals<Button>(body).Any(b => b.Content is string label && label == "查看已加入日程"), "Added activity offers edit rather than duplicate add");
            Modal(() => Edit(null, "笔记"), window => { window.UpdateLayout(); Theme.Screenshot((FrameworkElement)window.Content, Path.Combine(dir, "笔记编辑.png")); Check(!Visuals<TimeWheel>(window).Any(w => w.IsVisible), "Note editor hides time controls"); Click(window, "取消"); });
            var locked = new Item { Title = "只读课程测试", Kind = "课程", SourceKey = "readonly-test", Start = DateTime.Today.AddHours(8), End = DateTime.Today.AddHours(9), Location = "原教室" };
            store.Data.Items.Add(locked); store.Save();
            Modal(() => Edit(new(locked, locked.Start, locked.End)), window =>
            {
                Check(!Visuals<TimeWheel>(window).Any() && !Visuals<DatePicker>(window).Any(), "Course details expose no date or time editors");
                Check(Visuals<TextBox>(window).Count() == 1 && Visuals<TextBox>(window).Single().AcceptsReturn, "Notes are the only editable course field");
                Visuals<TextBox>(window).Single().Text = "课前准备";
                // Simulate a completed background update while the details dialog is open.
                locked.Location = "学校更新后的教室"; locked.Start = locked.Start.AddMinutes(10);
                Theme.Screenshot((FrameworkElement)window.Content, Path.Combine(dir, "课程只读详情.png"));
                Click(window, "保存备注");
            });
            Check(locked.Note == "课前准备" && locked.Location == "学校更新后的教室" && locked.Start.Minute == 10 && !locked.LocalOverride, "Saving note preserves newest school fields and does not create override");
            void RespondToDelete(Window course, string response)
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    var confirmation = course.OwnedWindows.Cast<Window>().Single();
                    confirmation.UpdateLayout();
                    Check(Visuals<Button>(confirmation).Any(b => b.IsDefault && (string)b.Content == "保留课程"), "Deletion confirmation defaults to keeping the course");
                    Click(confirmation, response);
                }), DispatcherPriority.ApplicationIdle);
                Click(course, "删除课程");
            }
            Modal(() => Edit(new(locked, locked.Start, locked.End)), window => { RespondToDelete(window, "保留课程"); Check(!locked.Cancelled, "Cancelling confirmation preserves course"); window.Close(); });
            Modal(() => Edit(new(locked, locked.Start, locked.End)), window => RespondToDelete(window, "确认删除"));
            var reloaded = new Store(store.Root).Data.Items.Single(i => i.Id == locked.Id);
            Check(reloaded.Cancelled && reloaded.LocalOverride && reloaded.Note == "课前准备", "Confirmed school deletion persists cancellation and keeps notes");
            RunNoteUiTests(dir, Check, Modal, Click);
            RunNoteToolsUiTests(dir, Check, Modal, Click);
            RunMarkdownUiTests(dir, Check, Modal, Click);
            RunNoteFreedomUiTests(dir, Check, Modal, Click);
            RunFloatingFormulaTests(dir, Check, Modal, Click);
            var assistantEntry = new AssistantEntry { Key = "ui-homework", Title = "微积分 · 极限与连续", Course = "微积分（甲）", Due = DateTime.Today.AddHours(20), Url = "https://courses.zju.edu.cn/course/1/content#/" };
            AssistantMerge.Apply(store.Data, "courses", "fixture", new() { assistantEntry }, DateTime.Now);
            assistantTab = 0; SwitchView("校园助手"); UpdateLayout();
            Check(Visuals<Button>(navigation).Count(b => b.Content.ToString()!.Contains("校园助手")) == 1, "Single campus assistant navigation entrance");
            Check(Visuals<TextBlock>(body).Any(t => t.Text == assistantEntry.Title), "Assistant coursework list shows imported entries");
            Theme.Screenshot((FrameworkElement)Content, Path.Combine(dir, "校园助手-课程待办.png"));
            var imported = store.Data.Items.Single(i => i.AssistantKey == "courses:fixture:ui-homework");
            Modal(() => Edit(new(imported, imported.Start, imported.End)), window => {
                Check(Visuals<TimeWheel>(window).All(w => !w.IsEnabled), "Imported deadlines cannot be accidentally edited");
                Check(Visuals<TextBox>(window).First(t => t.MaxLength == 300).IsReadOnly, "Imported title follows source");
                Click(window, "取消");
            });
            Check(On(DateTime.Today).Any(o => o.Item.Id == imported.Id), "Imported unfinished task participates in calendar");
            imported.Done = true; Check(!On(DateTime.Today).Any(o => o.Item.Id == imported.Id), "Completed imported task leaves active calendar"); imported.Done = false;
            AssistantMerge.Apply(store.Data, "library", "fixture", new() { new AssistantEntry { Key = "book1", Barcode = "book1", Title = "数学分析原理", Course = "Walter Rudin", Due = DateTime.Today.AddDays(6).AddHours(23).AddMinutes(59), Renewable = true, Url = "https://m.lib.zju.edu.cn/" } }, DateTime.Now);
            assistantTab = 1; Render(); UpdateLayout(); Check(Visuals<Button>(body).Any(b => b.Content.ToString() == "续借此书"), "Library offers per-book renewal");
            Theme.Screenshot((FrameworkElement)Content, Path.Combine(dir, "校园助手-借阅.png"));
            assistantTab = 2; Render(); UpdateLayout(); Check(!signEnabled && Visuals<Button>(body).Any(b => b.Content.ToString() == "开启本次自动签到"), "Attendance defaults off and is session-only");
            Theme.Screenshot((FrameworkElement)Content, Path.Combine(dir, "校园助手-签到.png"));
            assistantTab = 3; Render(); UpdateLayout(); Check(Visuals<Button>(body).Any(b => b.Content.ToString() == "账号与连接"), "Existing school configuration retained inside assistant");
            int itemCountBeforeLogout = store.Data.Items.Count(i => i.Kind is not ("课程" or "考试")), generationBeforeLogout = syncGeneration;
            File.WriteAllText(Path.Combine(store.Root, "school-credentials.dpapi"), "test-only-placeholder-not-a-credential");
            assistantClient = new CampusClient(); signEnabled = true;
            var previousCancel = assistantCancel;
            Click(this, "退出当前账号"); UpdateLayout();
            Check(!Vault.Exists && assistantClient == null && !signEnabled && previousCancel.IsCancellationRequested, "Logout deletes saved credentials and stops session and attendance");
            Check(store.Data.Items.Count == itemCountBeforeLogout && !store.Data.Items.Any(i => i.Kind is "课程" or "考试") && syncGeneration > generationBeforeLogout, "Logout clears courses and exams, preserves other items and invalidates pending school refresh");
            Check(Visuals<TextBlock>(body).Any(t => t.Text.Contains("已退出学校账号")), "Logout visibly confirms account signed out");
            Theme.Screenshot((FrameworkElement)Content, Path.Combine(dir, "校园助手-退出账号.png"));
            assistantTab = 0;

            var raw = new SchoolRecord { Key = "ui-holiday", Title = "45分钟地点可见测试", Start = new DateTime(2026, 10, 6, 9, 0, 0), End = new DateTime(2026, 10, 6, 9, 45, 0), Location = "紫金港东1B-310" };
            SchoolMerge.Apply(store.Data, "ui", new() { AcademicCalendar.Adjust(raw, true) }, false);
            date = new DateTime(2026, 9, 20); SwitchView("周课表"); UpdateLayout(); Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle); UpdateLayout();
            Check(Visuals<TextBlock>(body).Any(t => t.Text == "调课 · 按 10/6"), "Calendar day header shows verified makeup rule");
            Check(Visuals<TextBlock>(body).Any(t => t.Text == "紫金港东1B-310"), "45-minute course tile contains its classroom location");
            Theme.Screenshot((FrameworkElement)Content, Path.Combine(dir, "调课与地点.png"));
            date = new DateTime(2026, 10, 1); SwitchView("周课表"); UpdateLayout(); Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle); UpdateLayout();
            Check(Visuals<TextBlock>(body).Any(t => t.Text == "国庆假期"), "Calendar distinguishes holidays from ordinary days");
            Theme.Screenshot((FrameworkElement)Content, Path.Combine(dir, "国庆假期.png"));
        }
        catch (Exception ex) { results.Add("FAIL " + ex); }
        File.WriteAllLines(Path.Combine(dir, "ui-test-results.txt"), results);
    }
}







