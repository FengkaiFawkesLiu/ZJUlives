using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using static Shike.Theme;

namespace Shike;

public sealed partial class MainWindow
{
    void CourseDetails(Occurrence occurrence)
    {
        var item = occurrence.Item;
        var dialog = Dialog(this, "课程详情 · 拾课", 700, 730);
        var dock = new DockPanel { Margin = new Thickness(26) }; dialog.Content = dock;
        var actions = new DockPanel { Margin = new Thickness(0, 16, 0, 0) };
        DockPanel.SetDock(actions, Dock.Bottom); dock.Children.Add(actions);
        var panel = Stack(Text("课程详情", 25, bold: true), Space(8), Text("课表信息只读，仅可编辑备注。", 12, Muted), Space(20));
        dock.Children.Add(new ScrollViewer { Content = panel });
        panel.Children.Add(Card(Stack(Text(item.Title, 21, bold: true), Space(15),
            Text($"{occurrence.Start:yyyy年MM月dd日}  {occurrence.Start:HH:mm} — {occurrence.End:yyyy年MM月dd日 HH:mm}", 15, Green), Space(10),
            Text("上课地点：" + (item.Location.Length > 0 ? item.Location : "待公布")), Space(8),
            Text("授课教师：" + (item.Teacher.Length > 0 ? item.Teacher : "未提供")), Space(8),
            Text(item.Cancelled ? "已从个人课表删除" : item.SchoolAdjustment.Length > 0 ? item.SchoolAdjustment : item.SourceKey != null ? "学校导入的课程" : "个人添加的课程", 12, Muted))));
        if (item.Repeat != "不重复") panel.Children.Add(Text($"{item.Repeat}重复 · 截止 {item.Until:yyyy-MM-dd} · 备注属于整个课程系列", 12, Muted));
        var note = new TextBox { Text = item.Note, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 210, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        panel.Children.Add(Card(Stack(Text("课程备注", 16, bold: true), Space(10), note)));
        if (item.Attachments.Count > 0) panel.Children.Add(Text("已有附件（保留）：" + string.Join("、", item.Attachments.Select(a => a.Name)), 12, Muted));
        var save = Button("保存备注", () =>
        {
            // Resolve the latest record after a background refresh. Never replace course fields with the dialog snapshot.
            var current = store.Data.Items.FirstOrDefault(i => i.Id == item.Id);
            if (current == null) { MessageBox.Show(dialog, "课程已不存在，请关闭后重新查看课表。"); return; }
            if (Commit(() => { current.Note = note.Text; current.Modified = DateTime.Now; })) dialog.Close();
        }, true);
        var right = Row(Button("关闭", () => dialog.Close()), save); DockPanel.SetDock(right, Dock.Right); actions.Children.Add(right);
        if (!item.Cancelled) actions.Children.Add(Button("删除课程", () =>
        {
            if (!ConfirmCourseDeletion(dialog, item, occurrence)) return;
            var current = store.Data.Items.FirstOrDefault(i => i.Id == item.Id);
            if (current == null) { dialog.Close(); return; }
            if (Commit(() =>
            {
                if (current.SourceKey != null) { current.Cancelled = true; current.LocalOverride = true; current.Modified = DateTime.Now; }
                else { store.Data.Items.Remove(current); foreach (var child in store.Data.Items.Where(i => i.ParentId == current.Id)) child.ParentId = null; }
            })) dialog.Close();
        }));
        dialog.ShowDialog();
    }
    bool ConfirmCourseDeletion(Window owner, Item item, Occurrence occurrence)
    {
        var confirm = Dialog(owner, "确认删除课程", 540, 350);
        var panel = Stack(Text("确定删除这门课程？", 22, bold: true), Space(15), Text(item.Title, 16, bold: true), Space(8),
            Text($"{occurrence.Start:yyyy-MM-dd HH:mm} · {item.Location}", 13, Muted), Space(14),
            Text(item.SourceKey != null ? "仅从你的本地课表隐藏，不会向学校退课。已有备注和附件会保留，后续同步不会直接恢复该课程。" : item.Repeat != "不重复" ? "将删除整个重复课程系列及其备注。此操作不会更改学校系统。" : "将删除这条个人课程及其备注。此操作不会更改学校系统。", 13), Space(20));
        var cancel = Button("保留课程", () => confirm.DialogResult = false); cancel.IsCancel = true; cancel.IsDefault = true;
        var delete = Button("确认删除", () => confirm.DialogResult = true);
        panel.Children.Add(Row(cancel, delete));
        confirm.Content = new Border { Padding = new Thickness(24), Child = panel };
        confirm.Loaded += (_, _) => cancel.Focus();
        return confirm.ShowDialog() == true;
    }
}
