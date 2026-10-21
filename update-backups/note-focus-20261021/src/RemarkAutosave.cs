using System;
using System.Linq;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Shike;

// Notes are saved against the latest record, independently of the editor draft.
// Background school updates must never be overwritten by an old form snapshot.
public sealed class RemarkAutosave : IDisposable
{
    readonly Store store;
    readonly string id;
    readonly TextBox input;
    readonly TextBlock status;
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    bool pending;
    public RemarkAutosave(Store store, string id, TextBox input, TextBlock status, AppPanel panel)
    {
        this.store = store; this.id = id; this.input = input; this.status = status;
        status.Text = "备注已保存到本地";
        input.TextChanged += Changed;
        timer.Tick += (_, _) => Flush();
        panel.Closing += (_, e) =>
        {
            if (!Flush() && UiMessage.Show(panel,
                "备注尚未保存成功。是否放弃这次备注修改？选择“否”可返回重试或复制文字。",
                "备注未保存", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning,
                System.Windows.MessageBoxResult.No) != System.Windows.MessageBoxResult.Yes) e.Cancel = true;
        };
        panel.Closed += (_, _) => Dispose();
    }
    void Changed(object sender, TextChangedEventArgs args)
    {
        pending = true; status.Text = "正在保存备注…"; timer.Stop(); timer.Start();
    }
    public bool Flush()
    {
        timer.Stop();
        if (!pending) return true;
        var current = store.Data.Items.FirstOrDefault(i => i.Id == id);
        if (current == null) { status.Text = "原记录已删除；请复制备注后关闭。"; return false; }
        string previous = current.Note; var modified = current.Modified;
        try
        {
            current.Note = input.Text; current.Modified = DateTime.Now;
            store.Save(); pending = false; status.Text = "备注已保存 · " + DateTime.Now.ToString("HH:mm:ss"); return true;
        }
        catch (Exception ex)
        {
            current.Note = previous; current.Modified = modified;
            status.Text = "备注未保存：" + ex.Message + "；请重试或复制文字。"; return false;
        }
    }
    public void Dispose() { timer.Stop(); input.TextChanged -= Changed; }
}
