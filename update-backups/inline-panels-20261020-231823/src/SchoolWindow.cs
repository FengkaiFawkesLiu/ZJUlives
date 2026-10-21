using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using static Shike.Theme;

namespace Shike;

public sealed partial class MainWindow
{
    void RenderSchool()
    {
        var p = Page();
        schoolFreshness = Text("", 13, Green, true); UpdateFreshness();
        var sync = Button("立即更新", async () => await SyncNow(), true); sync.IsEnabled = !syncBusy;
        var connect = Button("账号与连接", SchoolDialog); connect.IsEnabled = !syncBusy;
        var auto = new CheckBox { Content = "每次启动软件时自动更新课程、考试和地点", IsChecked = store.Data.Settings.AutoSyncOnStartup };
        auto.Click += (_, _) => Commit(() => store.Data.Settings.AutoSyncOnStartup = auto.IsChecked == true);
        p.Children.Add(Card(Stack(Text("连接浙大本科教务", 23, bold: true), Space(12), Text("课程与考试，带回你的本地日历。", 15, Muted), Space(15), schoolFreshness, Space(16), Row(sync, connect, Button("打开教务网站", () => Process.Start(new ProcessStartInfo("https://zdbk.zju.edu.cn/") { UseShellExecute = true }))), Space(12), auto, Text(Vault.Exists ? "账号密码已在本机加密保存（Windows 当前用户保护）。" : "当前未保存账号密码。首次连接后，不保存密码则启动时请你登录更新。", 12, Muted), Space(10), Row(Button("测试本机加密", () =>
        {
            try { CredentialVault.Probe(); MessageBox.Show(this, "Windows 当前用户的加密与解密验证成功。测试没有使用或保存任何真实账号密码。", "本机加密验证"); }
            catch { MessageBox.Show(this, "当前 Windows 用户环境无法使用系统加密。不会退回明文保存；你仍可以不保存密码，每次手动登录。", "本机加密不可用"); }
        }))), "#EDF3E9", 25));
        p.Children.Add(Card(Stack(Text("学校数据如何更新", 18, bold: true), Space(12), Text("• 只向学校发送登录与读取请求，没有上传你的笔记、附件或个人日程。\n• 账号密码保存由你选择；密文受当前 Windows 用户保护，不放入导出备份。\n• 更新失败时保留本地数据，不把失败时间显示为成功更新时间。\n• 更新冲突保留双方内容；需要验证码时请进入“账号与连接”。\n• 导入地点包含教务接口的教室和校区；缺失时显示待公布。", 13))));
        var holidayToggle = new CheckBox { Content = "应用校本部放假与调补课规则", IsChecked = store.Data.Settings.ApplyAcademicHolidays };
        holidayToggle.Click += (_, _) => Commit(() => { store.Data.Settings.ApplyAcademicHolidays = holidayToggle.IsChecked == true; AcademicCalendar.ApplyCached(store.Data); });
        p.Children.Add(Card(Stack(Text("假期也是日程的一部分", 18, bold: true), Space(10), Text(AcademicCalendar.Coverage, 12, Green, true), Space(10), Text("中秋 9/25–27 · 国庆 10/1–7\n9/20 ⇄ 10/6，10/10 ⇄ 10/7 对调\n10/17 补 10/2；2027/1/4 补 12/31\n周末课程不一律停课；补课与原安排重叠时需核对开课单位通知。", 13), Space(10), holidayToggle, Text(store.Data.Settings.HolidayCheckStatus, 12, Muted), Space(6), Text(store.Data.Settings.LastHolidayCheck is DateTime check ? "上次联网核对：" + check.ToString("yyyy-MM-dd HH:mm") : "内置规则核对日期：2026-09-17；未来通知不作推断。", 11, Muted), Space(12), Row(Button("查看学校原文", () => Process.Start(new ProcessStartInfo(AcademicCalendar.SourceUrl) { UseShellExecute = true })), Button("核对通知更新", async () => { await CheckHolidayNotice(); Render(); })))));
        if (store.Data.Items.Any(i => i.HasConflict)) p.Children.Add(ConflictBanner());
        var school = store.Data.Items.Where(i => i.SourceKey != null).ToList();
        p.Children.Add(Text($"已保存在本地：{school.Count(i => i.Kind == "课程" && !i.Cancelled && !(i.SchoolSuspended && !i.LocalOverride))} 次有效课程 · {school.Count(i => i.SchoolSuspended && !i.LocalOverride)} 次节日停课 · {school.Count(i => i.Kind == "考试" && !i.Cancelled)} 场考试", 13, Muted));
    }
    void SchoolDialog()
    {
        if (syncBusy) { MessageBox.Show(this, "正在更新学校数据，请稍候。"); return; }
        var dialog = Dialog(this, "浙大本科教务 · 本地连接", 770, 830);
        var outer = new DockPanel { Margin = new Thickness(24) }; dialog.Content = outer;
        var status = Text("请确认以下学期参数。课表周次以你填写的第一周周一为基准。", 12, Muted); status.Margin = new Thickness(0, 10, 0, 0);
        DockPanel.SetDock(status, Dock.Bottom); outer.Children.Add(status);
        var panel = new StackPanel(); outer.Children.Add(new ScrollViewer { Content = panel });
        var settings = JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(store.Data.Settings))!;
        var year = new TextBox { Text = settings.Year.ToString() };
        var semester = Combo(new[] { "秋冬学期（3）", "春夏学期（12）" }, settings.Semester == "3" ? "秋冬学期（3）" : "春夏学期（12）");
        var monday = new DatePicker { SelectedDate = settings.TermMonday };
        var weeks = new TextBox { Text = settings.Weeks.ToString() };
        var half = new TextBox { Text = settings.SecondHalfWeek.ToString() };
        var periods = new TextBox { Text = settings.Periods, TextWrapping = TextWrapping.Wrap, AcceptsReturn = false, MinHeight = 75 };
        panel.Children.Add(Text("01  学期与上课时间", 18, bold: true)); panel.Children.Add(Space(14));
        panel.Children.Add(Field("学年起始年份（例如 2026 表示 2026—2027 学年）", year)); panel.Children.Add(Field("学期", semester));
        panel.Children.Add(Field("第一周的周一（须按学校校历确认）", monday));
        panel.Children.Add(Field("整个长学期的教学周数", weeks)); panel.Children.Add(Field("冬 / 夏短学期从第几周开始", half));
        panel.Children.Add(Field("校本部第 1—13 节时间（已核对 2026—2027 官方校历及 Celechron）", periods));
        var confirmed = new CheckBox { Content = "我已核对学期起始日、短学期分界和节次时间", IsChecked = settings.TermConfigured || settings.LastSync != null }; panel.Children.Add(confirmed);
        panel.Children.Add(Space(15)); panel.Children.Add(Text("02  学校认证", 18, bold: true)); panel.Children.Add(Space(12));
        var username = new TextBox { MaxLength = 32 }; var password = new PasswordBox { Padding = new Thickness(10, 8, 10, 8), MinHeight = 36 };
        panel.Children.Add(Field("学号", username)); panel.Children.Add(Field("统一身份认证密码", password));
        var remember = new CheckBox { Content = "在本机加密保存账号和密码", IsChecked = Vault.Exists };
        var startup = new CheckBox { Content = "每次启动软件自动更新", IsChecked = settings.AutoSyncOnStartup };
        panel.Children.Add(remember); panel.Children.Add(startup);
        panel.Children.Add(Text("勾选后使用 Windows 当前用户加密；其他 Windows 用户通常无法解密。不进入备份。取消勾选并连接成功后删除旧凭据，也可在学校连接页直接删除。", 11, Muted)); panel.Children.Add(Space(12));
        try { var saved = Vault.Read(); if (saved != null) { username.Text = saved.Username; password.Password = saved.Password; } }
        catch { remember.IsChecked = false; status.Text = "已保存账号无法解密，请重新输入；可在学校连接页删除旧密文。"; }
        var captchaImage = new Image { MaxHeight = 65, HorizontalAlignment = HorizontalAlignment.Left };
        var captcha = new TextBox { Width = 160, HorizontalAlignment = HorizontalAlignment.Left };
        var captchaArea = Stack(captchaImage, Field("教务验证码", captcha)); captchaArea.Visibility = Visibility.Collapsed; panel.Children.Add(captchaArea);
        var previewArea = new StackPanel();
        var provider = new ZjuUndergraduateProvider(); bool logged = false, busy = false, closed = false; string account = "";
        async System.Threading.Tasks.Task GetCaptcha()
        {
            var bytes = await provider.Captcha();
            using var stream = new MemoryStream(bytes); var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.StreamSource = stream; bitmap.EndInit();
            captchaImage.Source = bitmap; captchaArea.Visibility = Visibility.Visible; captcha.Clear(); captcha.Focus();
        }
        var refreshCaptcha = Button("换一张验证码", async () => { try { await GetCaptcha(); } catch { status.Text = "验证码读取失败，请重新连接。"; } }); captchaArea.Children.Add(refreshCaptcha);
        Button? connect = null;
        connect = Button("读取课程与考试", async () =>
        {
            if (busy) return;
            try
            {
                if (confirmed.IsChecked != true) throw new InvalidOperationException("请先核对并确认学期参数。");
                if (!int.TryParse(year.Text, out int y) || y < 2000 || y > 2100 || !int.TryParse(weeks.Text, out int w) || w < 1 || w > 30 || !int.TryParse(half.Text, out int h) || h < 1 || h > w + 1 || monday.SelectedDate == null || monday.SelectedDate.Value.DayOfWeek != DayOfWeek.Monday)
                    throw new InvalidOperationException("请检查学年、周数、短学期分界，第一周日期必须是周一。");
                SchoolParser.Periods(periods.Text);
                settings.Year = y; settings.Semester = semester.SelectedIndex == 0 ? "3" : "12"; settings.Weeks = w; settings.SecondHalfWeek = h; settings.TermMonday = monday.SelectedDate.Value; settings.Periods = periods.Text;
                settings.TermConfigured = true; settings.AutoSyncOnStartup = startup.IsChecked == true;
                if (!logged && (username.Text.Trim().Length == 0 || password.Password.Length == 0)) throw new InvalidOperationException("请在此窗口填写学校学号和密码，无需发送到聊天中。");
                busy = true; connect!.IsEnabled = false; previewArea.Children.Clear(); status.Text = "正在直接连接学校，请稍候…";
                if (!logged)
                {
                    account = username.Text.Trim();
                    string secret = password.Password;
                    try
                    {
                        await provider.Login(account, secret); if (closed) return;
                        if (remember.IsChecked == true) Vault.Save(new(account, secret)); else Vault.Forget();
                        DisconnectAssistant();
                        logged = true; username.IsEnabled = false; password.IsEnabled = false; remember.IsEnabled = false;
                    }
                    finally { password.Clear(); secret = ""; }
                }
                var batch = await provider.Fetch(settings, captcha.Text.Trim()); if (closed) return;
                status.Text = $"已读取 {batch.Courses.Count} 次课程、{batch.Exams.Count} 场考试。尚未写入日历，请核对预览。";
                previewArea.Children.Add(Space(20)); previewArea.Children.Add(Text("03  核对预览", 18, bold: true)); previewArea.Children.Add(Space(12));
                foreach (var warning in batch.Warnings) previewArea.Children.Add(Text("! " + warning, 12, Brush("#A1642B")));
                if (batch.Courses.Count + batch.Exams.Count == 0) previewArea.Children.Add(Text("学校返回的可识别安排为空。请确认学期，不会据此自动删除原数据。", 12, Muted));
                var table = new ListView { Height = 245, ItemsSource = batch.Courses.Concat(batch.Exams).OrderBy(r => r.Start).Select(r => new { Kind = r.Suspended ? "停课" : r.Kind, r.Title, Time = r.Start.ToString("MM-dd HH:mm") + "–" + r.End.ToString("HH:mm"), Location = r.Location.Length == 0 ? "地点待公布" : r.Location }) };
                var gv = new GridView();
                foreach (var (label, binding, width) in new[] { ("类型", "Kind", 50.0), ("名称", "Title", 185.0), ("时间", "Time", 160.0), ("地点", "Location", 170.0) }) gv.Columns.Add(new GridViewColumn { Header = label, DisplayMemberBinding = new System.Windows.Data.Binding(binding), Width = width });
                table.View = gv; previewArea.Children.Add(table); previewArea.Children.Add(Space(12));
                var removeAbsent = new CheckBox { Content = "确认此次列表完整：把学校已移除的旧安排同步标记取消", IsChecked = false, IsEnabled = batch.Warnings.Count == 0 && batch.Courses.Count + batch.Exams.Count > 0 };
                previewArea.Children.Add(removeAbsent);
                previewArea.Children.Add(Text("不勾选时只新增和更新，不移除旧安排。预览发现未识别数据时，停用移除操作。已有本地修改的安排始终进入冲突处理。", 11, Muted));
                previewArea.Children.Add(Space(12));
                previewArea.Children.Add(Button("确认预览，保存到本地", () =>
                {
                    SyncSummary? summary = null;
                    if (Commit(() =>
                    {
                        var current = store.Data.Settings;
                        current.Year = settings.Year; current.Semester = settings.Semester; current.TermMonday = settings.TermMonday; current.Weeks = settings.Weeks; current.SecondHalfWeek = settings.SecondHalfWeek; current.Periods = settings.Periods;
                        current.AutoSyncOnStartup = startup.IsChecked == true; current.TermConfigured = true;
                        summary = SchoolSync.Apply(store.Data, batch, account, settings, DateTime.Now, removeAbsent.IsChecked == true);
                    }))
                    {
                        syncStatus = "已成功读取并保存课程、考试和地点。";
                        dialog.Close();
                        if (summary!.Conflicts > 0)
                        { SwitchView("冲突与提醒"); tray?.ShowBalloonTip(8000, "学校更新存在冲突", "已保留你的安排，请选择采用学校安排或保留本地修改。", System.Windows.Forms.ToolTipIcon.Warning); MessageBox.Show(this, "学校更新与你的本地修改存在冲突，已保留双方内容。请在“冲突与提醒”中处理。", "学校更新提醒"); }
                        else { Render(); MessageBox.Show(this, $"已保存到本地。新增 {summary.Added} 项，更新 {summary.Updated} 项。"); }
                    }
                }, true));
            }
            catch (CaptchaRequiredException)
            {
                if (!closed) { status.Text = "学校要求验证码，请填写后再次读取。"; try { await GetCaptcha(); } catch { status.Text = "验证码读取失败，请关闭窗口后重新连接。"; } }
            }
            catch (Exception ex)
            {
                if (!closed) status.Text = ex is System.Security.Cryptography.CryptographicException ? "本机加密不可用，未保存新凭据。可取消保存密码，重新输入后连接。" : ex is System.Threading.Tasks.TaskCanceledException ? "学校请求超时，本地数据未更改。请检查网络后重试。" : ex is System.Text.Json.JsonException ? "学校返回格式未识别，本地数据未更改。" : "读取未完成：" + ex.Message;
            }
            finally { busy = false; if (!closed) connect!.IsEnabled = true; }
        }, true);
        panel.Children.Add(connect); panel.Children.Add(previewArea);
        dialog.Closed += (_, _) => { closed = true; password.Clear(); provider.Dispose(); }; dialog.ShowDialog();
    }
}
