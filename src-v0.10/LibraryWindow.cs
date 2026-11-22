using System;
using System.Linq;
using System.Windows.Controls;
using static Shike.Theme;

namespace Shike;
public sealed partial class MainWindow
{
    void RenderLibraryLists(StackPanel p, AssistantSource source)
    {
        var current = store.Data.Assistant.Library;
        var history = store.Data.Assistant.LibraryHistory;
        string Count(AssistantSource s, string unit) => s.Updated == null ? "未读取" : s.Entries.Count + unit;
        p.Children.Add(Row(
            TabButton("当前待归还 · " + Count(current, " 本"), () => { showLibraryHistory = false; assistantPage = 0; Render(); }, !showLibraryHistory),
            TabButton("历史借阅 · " + Count(history, " 条"), () => { showLibraryHistory = true; assistantPage = 0; Render(); }, showLibraryHistory)));
        p.Children.Add(Space(12));
        p.Children.Add(Text(showLibraryHistory ? "按归还日期从近到远显示官网可查询的历史；未显示日期的记录保留为空。" : "还书后刷新即可更新；续借请在官网办理，再刷新获取新的应还日期。", 12, Muted));
        if (showLibraryHistory && source.TotalAvailable > source.Entries.Count)
            p.Children.Add(Card(Text($"官网当前提供 {source.Entries.Count} 条，共有 {source.TotalAvailable} 条历史记录。完整列表需向图书馆流通台查询。", 13, Muted)));
        var entries = showLibraryHistory
            ? source.Entries.OrderByDescending(e => e.ReturnedAt).ThenByDescending(e => e.Due).ToList()
            : source.Entries.OrderBy(e => e.Due ?? DateTime.MaxValue).ToList();
        if (entries.Count == 0)
        {
            string title = source.Updated == null ? "尚未读取成功" : source.Status.StartsWith("读取成功", StringComparison.Ordinal)
                ? (showLibraryHistory ? "读取成功，暂无历史借阅记录" : "读取成功，当前待归还 0 本")
                : (showLibraryHistory ? "上次读取：历史借阅 0 条" : "上次读取：当前待归还 0 本");
            p.Children.Add(Card(Stack(Text(title, 18, bold: true), Space(10), Text(source.Updated == null ? "连接学校账号后点击“立即更新”；读取失败的原因显示在上方。" : "此结果来自图书馆官网。读取失败会保留上次记录和更新时间。", 13, Muted))));
        }
        assistantPage = Math.Min(assistantPage, Math.Max(0, (entries.Count - 1) / 40));
        foreach (var entry in entries.Skip(assistantPage * 40).Take(40))
        {
            string state = showLibraryHistory ? (entry.ReturnedAt.HasValue ? "已归还" : "历史借阅") : entry.Due < DateTime.Now ? "已逾期" : "待归还";
            var actions = new WrapPanel();
            if (!showLibraryHistory)
            {
                var item = store.Data.Items.FirstOrDefault(i => i.AssistantKey == "library:" + source.Account + ":" + entry.Key);
                if (item != null && !item.Cancelled) actions.Children.Add(Button("查看 / 备注", () => Edit(new Occurrence(item, item.Start, item.End))));
                if (item?.Cancelled == true) actions.Children.Add(Button("恢复到待办", () => Commit(() => item.Cancelled = false)));
                if (entry.Renewable && entry.Barcode.Length > 0) {
                    var renew = Button("续借此书", async () => await RenewAssistant(entry)); renew.IsEnabled = !assistantBusy && !signBusy; actions.Children.Add(renew);
                }
            }
            actions.Children.Add(Button(showLibraryHistory ? "官网查看" : "官网查看 / 续借", () => OpenAssistantUrl(AssistantMerge.LibraryWebsite)));
            var details = Stack(Row(Text(entry.Title, 17, bold: true), Text("  " + state, 12, state == "已逾期" ? Brush("#AD633D") : Green)), Space(8));
            if (!string.IsNullOrWhiteSpace(entry.Course)) details.Children.Add(Text(entry.Course, 12, Muted));
            string location = string.Join(" · ", new[] { entry.Branch, entry.CallNumber }.Where(s => !string.IsNullOrWhiteSpace(s)));
            if (location.Length > 0) details.Children.Add(Text(location, 12, Muted));
            details.Children.Add(Space(6));
            details.Children.Add(Text("应还日期  " + (entry.Due?.ToString("yyyy-MM-dd") ?? "官网未提供"), 13));
            if (showLibraryHistory) details.Children.Add(Text("归还日期  " + (entry.ReturnedAt?.ToString("yyyy-MM-dd") ?? "官网未提供"), 13));
            details.Children.Add(Space(10)); details.Children.Add(actions); p.Children.Add(Card(details));
        }
        if (entries.Count > 40) p.Children.Add(Row(Button("上一页", () => { assistantPage = Math.Max(0, assistantPage - 1); Render(); }), Text($"{assistantPage + 1} / {(entries.Count + 39) / 40}", 12), Button("下一页", () => { assistantPage++; Render(); })));
    }
}
