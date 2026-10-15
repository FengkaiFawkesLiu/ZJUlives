using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;

internal static class Launcher
{
    [STAThread]
    private static int Main(string[] args)
    {
        string root = AppDomain.CurrentDomain.BaseDirectory;
        string app = Path.Combine(root, "app-v0.10", "Shike.exe");
        bool check = args.Length == 1 && args[0] == "--check";
        if (!File.Exists(app))
        {
            if (!check) MessageBox.Show("未找到拾课主程序，请将启动拾课.exe与app-v0.10文件夹放在一起。", "拾课", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return 1;
        }
        if (check) return 0;
        try
        {
            Process.Start(new ProcessStartInfo(app) { WorkingDirectory = Path.GetDirectoryName(app), UseShellExecute = true });
            return 0;
        }
        catch (Exception ex)
        {
            MessageBox.Show("拾课启动失败：" + ex.Message, "拾课", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 2;
        }
    }
}
