using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Shike;
public sealed partial class MainWindow
{
    public void RunLibraryUiTests(string dir)
    {
        var report = new List<string>();
        void Check(bool ok, string label) { report.Add((ok ? "PASS " : "FAIL ") + label); if (!ok) throw new Exception(label); }
        void Paint() { Render(); UpdateLayout(); Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle); UpdateLayout(); }
        bool Has(string text) => Visuals<TextBlock>(body).Any(t => t.Text.Contains(text, StringComparison.Ordinal));
        void ClickPrefix(string text) => Visuals<Button>(body).First(b => b.Content is string s && s.StartsWith(text, StringComparison.Ordinal)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        try
        {
            Width = 1330; Height = 870; assistantTab = 1; SwitchView("校园助手"); Paint();
            Check(Has("尚未读取成功") && !Has("读取成功，当前待归还 0 本"), "Unconnected state never implies an empty successful read");
            var now = DateTime.Now;
            AssistantMerge.Apply(store.Data, "library", "fixture", new(), now);
            AssistantMerge.Apply(store.Data, "library-history", "fixture", new(), now); Paint();
            Check(Has("读取成功，当前待归还 0 本"), "Verified current zero is explicit");
            Theme.Screenshot((FrameworkElement)Content, Path.Combine(dir, "图书借阅-空列表.png"));
            ClickPrefix("历史借阅 ·"); Paint();
            Check(Has("读取成功，暂无历史借阅记录"), "History tab has independent empty success");
            var current = OpacParser.Read(LibraryTests.Current(), "ZJU50", false).Entries;
            AssistantMerge.Apply(store.Data, "library", "fixture", current, now);
            int count = store.Data.Items.Count;
            AssistantMerge.Apply(store.Data, "library-history", "fixture", OpacParser.Read(LibraryTests.History(), "ZJU50", true).Entries, now);
            Check(store.Data.Items.Count == count, "History never creates calendar tasks");
            Paint(); Check(Has("同一本书") && Has("归还日期  2026-10-20") && Has("已归还"), "History renders real title and return date");
            Check(!Visuals<Button>(body).Any(b => b.Content?.ToString() == "查看 / 备注"), "History has no current-loan task action");
            Theme.Screenshot((FrameworkElement)Content, Path.Combine(dir, "图书借阅-历史记录.png"));
            ClickPrefix("当前待归还 ·"); Paint();
            Check(Has("线性代数") && Has("应还日期  2026-11-21") && !Has("同一本书"), "Current tab displays only outstanding books");
            Check(Visuals<Button>(body).Any(b => b.Content?.ToString() == "官网查看 / 续借"), "Current book links to official renewal");
            Theme.Screenshot((FrameworkElement)Content, Path.Combine(dir, "图书借阅-当前待归还.png"));
            store.Data.Assistant.Library.Status = "图书馆连接失败；已有记录保留。"; Paint();
            Check(Has("连接失败") && Has("线性代数"), "Failed refresh preserves cached books and exposes error");
            AssistantMerge.Apply(store.Data, "library", "fixture", new(), now);
            store.Data.Assistant.Library.Status = "图书馆连接失败；已有记录保留。"; Paint();
            Check(Has("上次读取：当前待归还 0 本") && !Has("读取成功，当前待归还 0 本"), "Stale zero is not presented as a fresh success");
            store.Save(); var saved = new Store(store.Root);
            Check(saved.Data.Assistant.LibraryHistory.Entries.Count == 2 && saved.Data.Assistant.Library.Entries.Count == 0, "Separate snapshots survive restart");
        }
        catch (Exception ex) { report.Add("FAIL " + ex.Message); }
        File.WriteAllLines(Path.Combine(dir, "library-ui-results.txt"), report);
    }
}
