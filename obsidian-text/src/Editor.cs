using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using static Shike.Theme;

namespace Shike;

public sealed partial class MainWindow
{
    void Edit(Occurrence? occurrence, string kind = "活动", DateTime? at = null, CampusActivity? campus = null)
    {
        if (occurrence?.Item.Kind == "笔记" || occurrence == null && kind == "笔记") { EditKnowledgeNote(occurrence?.Item); return; }
        if (occurrence?.Item.Kind == "课程") { CourseDetails(occurrence); return; }
        Item? original = occurrence?.Item; bool single = false;
        if (original?.Repeat != null && original.Repeat != "不重复")
        {
            var scope = MessageBox.Show(this, "修改哪部分？\n\n“是”：仅这一次\n“否”：整个重复系列\n“取消”：返回", "重复日程", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (scope == MessageBoxResult.Cancel) return; single = scope == MessageBoxResult.Yes;
        }
        var start = at ?? date.Date.AddHours(DateTime.Now.Hour < 22 ? DateTime.Now.Hour + 1 : 20);
        var draft = original?.Copy() ?? new Item { Kind = kind, Start = start, End = start.AddHours(1), Until = start.AddMonths(4).Date };
        bool remoteDoneAtOpen = draft.AssistantRemoteDone;
        if (campus != null) { draft.Title = campus.Title; draft.Location = campus.Location; draft.CampusUrl = campus.Url; draft.Note = campus.Description + "\n来源：" + campus.Url; }
        if (single && occurrence != null) { draft.Start = occurrence.Start; draft.End = occurrence.End; draft.Repeat = "不重复"; draft.Excluded.Clear(); draft.Id = Guid.NewGuid().ToString("N"); draft.ParentId = original!.Id; }
        var dialog = Dialog(this, original == null ? "新建事项 · 拾课" : "事项详情 · 拾课", 800, 880);
        var dock = new DockPanel { Margin = new Thickness(25), Background = Cream }; dialog.Content = dock;
        var actions = new DockPanel { Margin = new Thickness(0, 15, 0, 0) }; DockPanel.SetDock(actions, Dock.Bottom); dock.Children.Add(actions);
        var header = Stack(Text(original == null ? (kind == "笔记" ? "留下一点想法" : "为新的安排留好位置") : "安排与记录", 25, bold: true), Space(6), Text("日程、笔记与附件，保存在这台电脑。", 12, Muted), Space(18));
        DockPanel.SetDock(header, Dock.Top); dock.Children.Add(header);
        var panel = new StackPanel(); dock.Children.Add(new ScrollViewer { Content = panel, Padding = new Thickness(0, 0, 10, 0) });
        var title = new TextBox { Text = draft.Title, FontSize = 20, MaxLength = 300 };
        var type = Combo(new[] { "活动", "课程", "考试", "待办" }, draft.Kind); type.IsEnabled = original?.SourceKey == null && original?.AssistantKey == null;
        panel.Children.Add(Field("标题", title)); panel.Children.Add(Field("类型", type));
        var dateStart = new DatePicker { SelectedDate = draft.Start.Date, Width = 235, VerticalAlignment = VerticalAlignment.Center };
        var dateEnd = new DatePicker { SelectedDate = draft.End.Date, Width = 235, VerticalAlignment = VerticalAlignment.Center };
        var timeStart = new TimeWheel(draft.Start); var timeEnd = new TimeWheel(draft.End);
        var startField = Field("开始时间 / 待办截止时间", Row(dateStart, timeStart)); var endField = Field("结束时间", Row(dateEnd, timeEnd));
        var timeHint = Text(campus == null ? "上下滚动或拖动数字选择时间 · 24 小时制" : campus.Start == null ? "官网未提供活动时间，请选择实际日期和起止时间。" : "官网未提供结束时间，当前预填 1 小时，请确认。", 11, Muted);
        var timeCard = Card(Stack(startField, endField, timeHint), padding: 18); panel.Children.Add(timeCard);
        if (draft.AssistantKey != null) { title.IsReadOnly = true; dateStart.IsEnabled = dateEnd.IsEnabled = timeStart.IsEnabled = timeEnd.IsEnabled = false; timeHint.Text = "标题和截止时间跟随学校更新；个人备注、附件和提醒可自由设置。"; }
        var location = new TextBox { Text = draft.Location }; var locationField = Field("地点", location); panel.Children.Add(locationField);
        if (draft.Teacher.Length > 0) { panel.Children.Add(Text("授课教师：" + draft.Teacher, 12, Muted)); panel.Children.Add(Space(10)); }
        if (draft.SchoolAdjustment.Length > 0) { panel.Children.Add(Text(draft.SchoolAdjustment + (draft.SchoolSuspended ? "（本地调整可保留该安排）" : ""), 12, Green)); panel.Children.Add(Space(10)); }
        var repeat = Combo(new[] { "不重复", "每天", "每周" }, draft.Repeat); repeat.IsEnabled = draft.SourceKey == null && draft.AssistantKey == null;
        var until = new DatePicker { SelectedDate = draft.Until, Width = 235, Margin = new Thickness(12, 0, 0, 0) };
        var repeatField = Field("重复规则 / 重复截止日期", Row(repeat, until)); panel.Children.Add(repeatField);
        var reminderDefault = new CheckBox { Content = "使用该类型的默认提醒（课程 / 考试）", IsChecked = draft.Reminders == null };
        var reminder = new TextBox { Text = draft.Reminders == null ? "" : string.Join(',', draft.Reminders), IsEnabled = draft.Reminders != null };
        reminderDefault.Checked += (_, _) => reminder.IsEnabled = false; reminderDefault.Unchecked += (_, _) => reminder.IsEnabled = true;
        var reminderPanel = Stack(reminderDefault, Field("自定义提前分钟数，逗号分隔；留空不提醒，例如 60,15", reminder)); panel.Children.Add(reminderPanel);
        var related = new ComboBox();
        var parents = new List<Item> { new() { Id = "", Title = "不关联其他事项" } };
        parents.AddRange(store.Data.Items.Where(i => i.Id != draft.Id && !i.Cancelled && i.Kind != "课程").OrderBy(i => i.Title)); related.ItemsSource = parents.Select(i => i.Title).ToList(); related.SelectedIndex = Math.Max(0, parents.FindIndex(i => i.Id == draft.ParentId));
        bool relatedChanged = false; related.SelectionChanged += (_, _) => relatedChanged = true;
        panel.Children.Add(Field("关联事项", related));
        var note = new TextBox { Text = draft.Note, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MinHeight = 170, MaxHeight = 350 };
        panel.Children.Add(Card(Stack(Text("笔记与准备", 16, bold: true), Space(7), Text("随手记录想法，也可以整理要带的材料。", 11, Muted), Space(12), note)));
        var files = new StackPanel(); panel.Children.Add(Text("附件 · 复制到本地资料库", 12, Muted)); panel.Children.Add(Space(8)); panel.Children.Add(files);
        var staged = new Dictionary<Attachment, string>();
        void DrawFiles()
        {
            files.Children.Clear();
            foreach (var attachment in draft.Attachments.ToList())
            {
                var row = new DockPanel { Margin = new Thickness(0, 0, 0, 7) };
                var remove = Button("移除", () => { draft.Attachments.Remove(attachment); staged.Remove(attachment); DrawFiles(); }); DockPanel.SetDock(remove, Dock.Right); row.Children.Add(remove);
                var open = Button(attachment.Name, () =>
                {
                    var path = staged.TryGetValue(attachment, out var source) ? source : Path.Combine(store.Files, attachment.File);
                    try
                    {
                        var ext = Path.GetExtension(path).ToLowerInvariant();
                        if (new[] { ".exe", ".bat", ".cmd", ".ps1", ".vbs", ".js", ".msi", ".scr", ".com", ".lnk", ".url", ".hta" }.Contains(ext))
                        { MessageBox.Show(dialog, "此附件可能是程序或脚本。请从数据文件夹自行检查后打开。"); return; }
                        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                    }
                    catch (Exception ex) { MessageBox.Show(dialog, "附件无法打开：" + ex.Message); }
                }); open.HorizontalContentAlignment = HorizontalAlignment.Left; row.Children.Add(open); files.Children.Add(row);
            }
        }
        DrawFiles();
        panel.Children.Add(Button("＋ 添加附件", () =>
        {
            var picker = new OpenFileDialog { Multiselect = true, Title = "选择要复制到本地的附件" };
            if (picker.ShowDialog(dialog) == true) foreach (var file in picker.FileNames) { var a = new Attachment { Name = Path.GetFileName(file) }; draft.Attachments.Add(a); staged[a] = file; }
            DrawFiles();
        }));
        panel.Children.Add(Space());
        var cancelled = new CheckBox { Content = "已取消此安排（保留笔记和附件）", IsChecked = draft.Cancelled };
        var done = new CheckBox { Content = "待办已完成", IsChecked = draft.Done }; panel.Children.Add(cancelled); panel.Children.Add(done);
        if (draft.Original != null)
            panel.Children.Add(Card(Text($"学校原始安排：{draft.Original.Title}\n{draft.Original.Start:yyyy-MM-dd HH:mm} — {draft.Original.End:HH:mm}\n{draft.Original.Location}\n修改时间、地点、标题或取消会保留为本地调整。", 12, Muted), "#EDF2E9", 14));
        if (original != null)
        {
            var linked = store.Data.Items.Where(i => i.ParentId == original.Id && !i.Cancelled).ToList();
            if (linked.Count > 0) { panel.Children.Add(Text("关联记录", 14, bold: true)); foreach (var child in linked) panel.Children.Add(Text("· " + child.Title + "（" + child.Kind + "）", 12, Muted)); }
        }
        void ApplyType()
        {
            var selected = type.SelectedItem?.ToString(); bool isNote = selected == "笔记", isTask = selected == "待办";
            timeCard.Visibility = isNote ? Visibility.Collapsed : Visibility.Visible;
            note.MinHeight = isNote ? 290 : 170;
            ((UIElement)endField).Visibility = isNote || isTask ? Visibility.Collapsed : Visibility.Visible;
            ((UIElement)locationField).Visibility = isNote || isTask ? Visibility.Collapsed : Visibility.Visible;
            ((UIElement)repeatField).Visibility = isNote || isTask ? Visibility.Collapsed : Visibility.Visible;
            reminderPanel.Visibility = isNote ? Visibility.Collapsed : Visibility.Visible; done.Visibility = isTask ? Visibility.Visible : Visibility.Collapsed;
        }
        type.SelectionChanged += (_, _) => ApplyType(); ApplyType();
        var right = Row(Button("取消", () => dialog.Close()), Button("保存", () =>
        {
            try
            {
                if (string.IsNullOrWhiteSpace(title.Text)) throw new InvalidOperationException("请填写标题。");
                draft.Title = title.Text.Trim(); draft.Kind = type.SelectedItem!.ToString()!;
                if (draft.Kind != "笔记")
                {
                    if (dateStart.SelectedDate == null) throw new InvalidOperationException("请选择开始日期。");
                    var st = timeStart.Value;
                    draft.Start = dateStart.SelectedDate.Value.Date + st;
                    if (draft.Kind == "待办") draft.End = draft.Start.AddMinutes(1);
                    else
                    {
                        if (dateEnd.SelectedDate == null) throw new InvalidOperationException("请选择结束日期。");
                        var et = timeEnd.Value;
                        draft.End = dateEnd.SelectedDate.Value.Date + et;
                        if (draft.End <= draft.Start) throw new InvalidOperationException("结束时间必须晚于开始时间。");
                    }
                }
                draft.Location = location.Text.Trim(); draft.Note = note.Text; draft.Modified = DateTime.Now;
                draft.Repeat = draft.Kind is "待办" or "笔记" ? "不重复" : repeat.SelectedItem!.ToString()!;
                draft.Until = until.SelectedDate ?? draft.Start.Date;
                if (draft.Repeat != "不重复" && draft.Until.Date < draft.Start.Date) throw new InvalidOperationException("重复截止日期不能早于开始日期。");
                if (draft.Repeat != "不重复" && (draft.End - draft.Start).TotalDays > (draft.Repeat == "每天" ? 1 : 7)) throw new InvalidOperationException("每次日程的时长不能超过重复间隔。");
                draft.Reminders = draft.Kind == "笔记" ? new() : reminderDefault.IsChecked == true ? null : Engine.ParseReminders(reminder.Text);
                if (relatedChanged || original == null) draft.ParentId = related.SelectedIndex > 0 ? parents[related.SelectedIndex].Id : null;
                draft.Done = done.IsChecked == true; draft.Cancelled = cancelled.IsChecked == true;
                if (draft.SourceKey != null) draft.LocalOverride = !SchoolRecord.From(draft).Same(draft.Original) || draft.Cancelled;
                var future = Engine.Between(new[] { draft }, draft.Start.Date, draft.Start.Date.AddDays(31)).ToList();
                bool conflict = future.Any(a => Engine.Between(store.Data.Items.Where(i => i.Id != original?.Id && i.Kind is not ("待办" or "笔记")), a.Start, a.End).Any(b => Engine.Overlaps(a, b)));
                if (draft.Kind is not ("待办" or "笔记") && conflict && MessageBox.Show(dialog, "此安排在未来 31 天内与其他日程重叠。仍然保存吗？", "时间冲突提醒", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
                foreach (var pair in staged.ToList()) { var saved = store.AddFile(pair.Value); pair.Key.File = saved.File; staged.Remove(pair.Key); }
                if (Commit(() =>
                {
                    if (original?.AssistantKey != null) {
                        var latest = store.Data.Items.FirstOrDefault(i => i.Id == original.Id) ?? throw new InvalidOperationException("来源记录已变化，请关闭后重试。");
                        draft.Title = latest.Title; draft.Start = latest.Start; draft.End = latest.End; draft.Location = latest.Location;
                        draft.AssistantNoDue = latest.AssistantNoDue; draft.AssistantRemoteDone = latest.AssistantRemoteDone;
                        if (latest.AssistantRemoteDone != remoteDoneAtOpen) draft.Done = latest.Done;
                    }
                    if (single && original != null && occurrence != null) original.Excluded.Add(occurrence.Start.ToString("yyyy-MM-dd"));
                    else if (original != null) store.Data.Items.Remove(original);
                    store.Data.Items.Add(draft);
                })) dialog.Close();
            }
            catch (Exception ex) { MessageBox.Show(dialog, ex.Message, "请检查输入", MessageBoxButton.OK, MessageBoxImage.Warning); }
        }, true));
        DockPanel.SetDock(right, Dock.Right); actions.Children.Add(right);
        if (original != null) actions.Children.Add(Button("删除", () =>
        {
            if (MessageBox.Show(dialog, (original.SourceKey != null || original.AssistantKey != null) ? "学校安排将标记取消，以免下次读取时重新出现。继续吗？" : "删除此记录？如有需要，请先导出备份。", "确认删除", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            if (Commit(() =>
            {
                if (single && occurrence != null) original.Excluded.Add(occurrence.Start.ToString("yyyy-MM-dd"));
                else if (original.SourceKey != null || original.AssistantKey != null) { original.Cancelled = true; original.LocalOverride = true; }
                else { store.Data.Items.Remove(original); foreach (var child in store.Data.Items.Where(i => i.ParentId == original.Id)) child.ParentId = null; }
            })) dialog.Close();
        }));
        dialog.Loaded += (_, _) => title.Focus(); dialog.ShowDialog();
    }
}
