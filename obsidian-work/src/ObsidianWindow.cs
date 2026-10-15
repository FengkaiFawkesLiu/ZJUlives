using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using static Shike.Theme;

namespace Shike;
public sealed partial class MainWindow
{
    void ShowObsidian()
    {
        var dialog = Dialog(this, "Obsidian · 本地笔记导出", 720, 640);
        var libraries = store.Data.NoteLibraries.ToList();
        var select = Combo(libraries.Select(l => l.Name).ToArray(), CurrentLibrary.Name);
        select.SelectedIndex = libraries.FindIndex(l => l.Id == CurrentLibrary.Id);
        string vault = ObsidianBridge.Vault(store);
        var status = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, Height = 155, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Text = "选择一个笔记库后导出。只处理已保存且未删除的笔记，不会导出账号密码。" };
        void Open(string target)
        {
            try { Directory.CreateDirectory(vault); Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
            catch (Exception ex) { MessageBox.Show(dialog, "无法打开：" + ex.Message + "\n请先启动一次已安装的 Obsidian。", "Obsidian"); }
        }
        var export = Button("导出所选笔记库", () =>
        {
            if (select.SelectedIndex < 0) return;
            try
            {
                var result = new ObsidianBridge().Export(store, libraries[select.SelectedIndex].Id);
                status.Text = $"导出完成：写入 {result.Written} 篇，未变更 {result.Unchanged} 篇，冲突 {result.Conflicts.Count} 篇，失败 {result.Errors.Count} 篇。";
                if (result.Conflicts.Count > 0) status.Text += "\n\n以下笔记在 Obsidian 中已修改，已跳过，未覆盖：\n" + string.Join("\n", result.Conflicts);
                if (result.Errors.Count > 0) status.Text += "\n\n失败详情：\n" + string.Join("\n", result.Errors);
            }
            catch (Exception ex) { status.Text = "导出未完成：" + ex.Message; }
        }, true);
        dialog.Content = new Border { Padding = new Thickness(24), Child = Stack(
            Text("把一个笔记库带到 Obsidian", 22, bold: true), Space(14), Field("选择笔记库", select),
            Text("仓库位置（全部本地保存）", 12, Muted), new TextBox { Text = vault, IsReadOnly = true }, Space(12),
            Text("首次使用：点击“首次设置”，在 Obsidian 选择“打开本地仓库 / Open folder as vault”，选择上面的文件夹。以后点击“打开 Obsidian”即可。", 12, Muted), Space(12),
            Row(export, Button("打开 Obsidian", () => {
                var file = Directory.Exists(vault) ? Directory.EnumerateFiles(vault, "*.md", SearchOption.AllDirectories).FirstOrDefault() : null;
                Open(file == null ? "obsidian://choose-vault" : "obsidian://open?path=" + Uri.EscapeDataString(file));
            })),
            Row(Button("首次设置", () => Open("obsidian://choose-vault")), Button("打开仓库文件夹", () => Open(vault))), Space(12),
            Text("单向导出 · Obsidian 中的编辑暂不回写。冲突文件跳过；源笔记删除后，已导出的文件仍保留。富文本与手写附原始预览。", 12, Muted), Space(12), status
        ) };
        dialog.ShowDialog();
    }
}
