using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using static Shike.Theme;

namespace Shike;

public sealed partial class MainWindow
{
    int assistantTab, assistantPage;
    bool showPintia;
    SchoolCredential? pintiaSession;
    CredentialVault PintiaVault => new(store.Root, true);
    bool assistantBusy, signBusy, signEnabled;
    string assistantStatus = "", signStatus = "未开启";
    DateTime assistantNext = DateTime.MinValue, signNext = DateTime.MinValue;
    CampusClient? assistantClient;
    CancellationTokenSource assistantCancel = new(), signCancel = new();
    readonly HashSet<string> signNotified = new();
    TextBlock? assistantFreshness;

    void RenderAssistant()
    {
        StackPanel p;
        if (assistantTab == 3) { RenderSchool(); p = (StackPanel)((ScrollViewer)body.Content).Content; }
        else p = Page();
        var tabs = new[] { "课程待办", "图书借阅", "课堂签到", "连接与设置" };
        var tabRow = new WrapPanel { Margin = new Thickness(0, 0, 0, 18) };
        for (int n = 0; n < tabs.Length; n++) { int target = n; tabRow.Children.Add(Button(tabs[n], () => { assistantTab = target; assistantPage = 0; Render(); }, assistantTab == n)); }
        p.Children.Insert(0, tabRow);
        if (assistantTab == 3)
        {
            var auto = new CheckBox { Content = "启动时及运行期间每 10 分钟更新校园助手", IsChecked = store.Data.Assistant.AutoRefresh };
            auto.Click += (_, _) => Commit(() => store.Data.Assistant.AutoRefresh = auto.IsChecked == true);
            p.Children.Insert(1, Card(Stack(Text("校园助手连接", 20, bold: true), Space(10), Text("沿用学校统一身份认证账号。退出账号会清除全部课程与考试（含手动添加）、相关连线和本机自动历史备份中的对应数据；笔记、待办和其他活动保留。", 13, Muted), Space(12), Row(Button("管理共享账号", AssistantAccountDialog), Button("退出当前账号", ForgetCredentials)), Space(12), auto, Space(8), Text("自动签到每次启动后手动开启；关闭窗口收到托盘后仍可运行，退出程序则停止。", 12, Muted))));
            return;
        }
        var source = assistantTab == 1 ? store.Data.Assistant.Library : showPintia ? store.Data.Assistant.Pintia : store.Data.Assistant.Courses;
        assistantFreshness = Text(AssistantFreshness(source), 12, Muted);
        if (assistantTab < 2)
        {
            var refresh = Button(assistantBusy ? "正在更新…" : "立即更新", async () => await RefreshAssistant(true), true); refresh.IsEnabled = !assistantBusy && !signBusy;
            p.Children.Add(Card(Stack(Text(assistantTab == 1 ? "让每本书如期归还" : "把截止日期放回日历", 23, bold: true), Space(10), Text(assistantTab == 1 ? "借阅到期自动加入待办和日历；续借后按学校返回的新日期更新。" : "合并课程作业、测验与课堂互动；保留个人备注，同步学校确认的提交状态。", 13, Muted), Space(14), Row(refresh, Button(Vault.Exists ? "使用已保存账号连接" : "连接账号", AssistantLogin), Button("打开官网", () => OpenAssistantUrl(assistantTab == 1 ? "https://libweb.zju.edu.cn/" : "https://courses.zju.edu.cn/"))), Space(12), assistantFreshness, Space(6), Text(assistantStatus, 12, Muted)), "#EDF3E9", 24));
            if (assistantTab == 0)
            {
                p.Children.Add(Row(Button("学在浙大", () => { showPintia = false; assistantPage = 0; Render(); }, !showPintia), Button("Pintia · 可选", () => { showPintia = true; assistantPage = 0; Render(); }, showPintia)));
                if (showPintia) p.Children.Add(Card(Stack(Text("Pintia 截止事项", 18, bold: true), Space(8), Text("同步近 90 天以来截止的题目集；提交状态无法核实，请在待办中自行标记完成。Cookie 只用于连接 Pintia。", 12, Muted), Space(10), Row(Button("设置 Pintia", PintiaDialog), Button("断开并删除 Pintia 凭据", () => { try { PintiaVault.Forget(); pintiaSession = null; Commit(() => store.Data.Assistant.PintiaEnabled = false); } catch { MessageBox.Show(this, "本机凭据文件删除失败，请检查文件权限。"); } })))));
            }
            if (assistantTab == 1) p.Children.Add(Card(Stack(Text("官网与借阅同步", 16, bold: true), Space(8), Text("官网入口已改为浙大图书馆主站，可从主站进入“我的图书馆”。借阅同步使用单独接口；接口连接失败时保留已有记录，请先在官网查询或续借。", 12, Muted))));
            var entries = source.Entries.OrderBy(e => e.Complete).ThenBy(e => e.Due ?? DateTime.MaxValue).ToList();
            if (entries.Count == 0) p.Children.Add(Card(Stack(Text(source.Updated == null ? "连接后，事项会出现在这里" : "本次没有相关事项", 18, bold: true), Space(10), Text(source.Updated == null ? "账号密码可选本机加密保存。未设置截止日期的事项仅在这里展示，不会编造日历时间。" : "缓存仍可离线查看；接口失败不会清空已有内容。", 13, Muted))));
            assistantPage = Math.Min(assistantPage, Math.Max(0, (entries.Count - 1) / 40));
            foreach (var entry in entries.Skip(assistantPage * 40).Take(40))
            {
                string prefix = (assistantTab == 1 ? "library:" : showPintia ? "pintia:" : "courses:") + source.Account + ":";
                var item = store.Data.Items.FirstOrDefault(i => i.AssistantKey == prefix + entry.Key);
                var actions = new WrapPanel();
                if (item != null && !item.Cancelled) actions.Children.Add(Button("查看 / 备注", () => Edit(new Occurrence(item, item.Start, item.End))));
                if (item?.Cancelled == true) actions.Children.Add(Button("恢复到待办", () => Commit(() => item.Cancelled = false)));
                actions.Children.Add(Button("查看原文", () => OpenAssistantUrl(entry.Url)));
                if (assistantTab == 1 && entry.Renewable)
                {
                    var renew = Button("续借此书", async () => await RenewAssistant(entry)); renew.IsEnabled = !assistantBusy && !signBusy; actions.Children.Add(renew);
                }
                string state = !entry.CompletionKnown && item?.Done != true ? "提交状态待核实" : entry.Complete ? "已提交" : item?.Done == true ? "本地已完成" : item?.Cancelled == true ? "已从待办隐藏" : entry.Due < DateTime.Now ? "已逾期" : "待完成";
                p.Children.Add(Card(Stack(Row(Text(entry.Title, 17, bold: true), new Border { Width = 14 }, Text(state, 11, state == "已逾期" ? Brush("#AD633D") : Green)), Space(8), Text(entry.Course, 12, Muted), Space(6), Text(entry.Due is DateTime due ? (assistantTab == 1 ? "归还日期  " + due.ToString("yyyy-MM-dd") : "截止时间  " + due.ToString("MM-dd ddd HH:mm")) : "尚无截止时间", 13), Space(10), actions)));
            }
            if (entries.Count > 40) p.Children.Add(Row(Button("上一页", () => { assistantPage = Math.Max(0, assistantPage - 1); Render(); }), Text($"{assistantPage + 1} / {(entries.Count + 39) / 40}", 12), Button("下一页", () => { assistantPage++; Render(); })));
            return;
        }
        var toggle = Button(signEnabled ? "停止自动签到" : "开启本次自动签到", async () => await ToggleAssistantSign(), !signEnabled);
        toggle.IsEnabled = signEnabled || !assistantBusy && !signBusy;
        p.Children.Add(Card(Stack(Text("课堂签到", 23, bold: true), Space(10), Text(signStatus, 14, Green, true), Space(12), Text("开启后每分钟检查正在进行的签到。雷达签到使用这台电脑的实时定位；数字和二维码签到会提醒你打开学在浙大完成。", 13, Muted), Space(14), Row(toggle, Button("打开学在浙大", () => OpenAssistantUrl("https://courses.zju.edu.cn/"))), Space(10), Text("需要 Windows 定位权限与有效学校会话。定位不足、网络中断或学校未确认时，会记录为待核实，绝不显示成功。每场仅自动尝试一次。", 12, Muted)), "#EDF3E9", 24));
        p.Children.Add(Text("最近记录 · 仅保存在本机", 16, bold: true)); p.Children.Add(Space(12));
        foreach (var line in store.Data.Assistant.SignLog.AsEnumerable().Reverse()) p.Children.Add(Card(Text(line, 12)));
        if (store.Data.Assistant.SignLog.Count == 0) p.Children.Add(Card(Text("还没有签到记录。开启后，检查结果会显示在这里。", 13, Muted)));
    }
    static void OpenAssistantUrl(string url)
    {
        if (!AssistantMerge.SafeUrl(url)) return;
        if (new Uri(url).Host == "m.lib.zju.edu.cn") url = AssistantMerge.LibraryWebsite;
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }
    static string AssistantFreshness(AssistantSource source) => Freshness.Format(source.Updated, DateTime.Now) + " · " + source.Status;
    void RefreshAssistantLabel() { if (view == "校园助手" && assistantFreshness != null && assistantTab < 2) assistantFreshness.Text = AssistantFreshness(assistantTab == 1 ? store.Data.Assistant.Library : showPintia ? store.Data.Assistant.Pintia : store.Data.Assistant.Courses); }
    void PaintAssistant() { if (!exiting && view == "校园助手") Render(); }
    static string AssistantError(Exception ex) => ex is InvalidDataException or CampusSessionExpiredException or CampusEndpointUnavailableException ? ex.Message : ex is System.Security.Cryptography.CryptographicException ? "本机共享账号无法解密，请在连接与设置中重新保存账号。" : ex is OperationCanceledException ? "请求已取消或超时，保留上次记录。" : ex is HttpRequestException ? "无法连接学校服务，请检查校园网络；上次数据已保留。" : "本次更新未完成，请重新连接；上次数据已保留。";
    void DisconnectAssistant()
    {
        signEnabled = false; signCancel.Cancel(); assistantCancel.Cancel(); assistantClient?.Dispose(); assistantClient = null;
        assistantCancel = new(); signNotified.Clear(); signStatus = "已停止"; assistantStatus = "已断开本次会话，本地缓存保留。";
    }
    void AssistantLogin()
    {
        if (assistantBusy || signBusy) return;
        if (Vault.Exists) { DisconnectAssistant(); _ = RefreshAssistant(true); }
        else AssistantAccountDialog();
    }
    void AssistantAccountDialog()
    {
        if (assistantBusy || signBusy) return;
        var dialog = Dialog(this, "校园助手 · 学校认证", 550, 510);
        var panel = new StackPanel { Margin = new Thickness(28) }; dialog.Content = panel;
        panel.Children.Add(Text("连接你的校园事项", 23, bold: true)); panel.Children.Add(Space(18));
        var user = new TextBox { MaxLength = 64 }; var pass = new PasswordBox { MinHeight = 38, Padding = new Thickness(10, 8, 10, 8) };
        var remember = new CheckBox { Content = "使用 Windows 加密在本机保存账号密码", IsChecked = Vault.Exists };
        panel.Children.Add(Field("学号", user)); panel.Children.Add(Field("统一身份认证密码", pass)); panel.Children.Add(remember); panel.Children.Add(Space(12));
        panel.Children.Add(Text("与教务连接共用本机账号；取消保存并成功登录后会删除旧密文。无需在聊天中提供密码。", 12, Muted));
        var status = Text("", 12, Muted); panel.Children.Add(Space(14)); panel.Children.Add(status);
        try {
            var saved = Vault.Read();
            if (saved != null) { user.Text = saved.Username; pass.Password = saved.Password; status.Text = "已读取教务课表使用的同一套本机加密账号。"; }
        } catch { remember.IsChecked = false; status.Text = "本机保存账号无法解密，请在此重新输入。原密文不会在连接失败时被删除。"; }
        bool closed = false, logged = false; using var cancel = new CancellationTokenSource();
        dialog.Closed += (_, _) => { closed = true; cancel.Cancel(); };
        var connect = Button("连接", () => { }, true);
        connect.Click += async (_, _) =>
        {
            if (user.Text.Trim().Length == 0 || pass.Password.Length == 0) { status.Text = "请填写学号和密码。"; return; }
            connect.IsEnabled = false; status.Text = "正在直接连接学校…";
            CampusClient? client = new();
            try
            {
                var credential = new SchoolCredential(user.Text.Trim(), pass.Password);
                await client.Login(credential, cancel.Token); cancel.Token.ThrowIfCancellationRequested();
                if (remember.IsChecked == true) Vault.Save(credential); else Vault.Forget();
                DisconnectAssistant(); assistantClient = client; client = null; logged = true; dialog.Close();
            }
            catch (Exception ex) { if (!closed) { status.Text = AssistantError(ex); connect.IsEnabled = true; } }
            finally { client?.Dispose(); }
        };
        panel.Children.Add(Space(16)); panel.Children.Add(Row(connect, Button("取消", () => dialog.Close())));
        dialog.ShowDialog(); pass.Clear();
        if (logged) _ = RefreshAssistant(false);
    }
    void PintiaDialog()
    {
        if (assistantBusy || signBusy) return;
        var dialog = Dialog(this, "Pintia · 可选连接", 560, 510);
        var p = new StackPanel { Margin = new Thickness(28) }; dialog.Content = p;
        var account = new TextBox { MaxLength = 100 };
        var cookie = new PasswordBox { MinHeight = 38, Padding = new Thickness(10, 8, 10, 8) };
        var save = new CheckBox { Content = "在本机加密保存 Cookie（不进入导出备份）" };
        p.Children.Add(Text("连接 Pintia", 22, bold: true)); p.Children.Add(Space(16));
        p.Children.Add(Field("Pintia 账号标识（用于区分本机缓存）", account)); p.Children.Add(Field("浏览器中已登录 Pintia 的 Cookie", cookie)); p.Children.Add(save);
        p.Children.Add(Text("在 Pintia 官网登录后，从浏览器开发者工具的请求头复制 Cookie。它可代表你的登录状态，请不要发送到聊天中。", 12, Muted));
        var status = Text("", 12, Muted); p.Children.Add(Space(12)); p.Children.Add(status);
        bool connected = false;
        p.Children.Add(Button("保存设置并更新", () => {
            if (string.IsNullOrWhiteSpace(account.Text) || string.IsNullOrWhiteSpace(cookie.Password) || cookie.Password.Contains('\n') || cookie.Password.Contains('\r')) { status.Text = "请填写账号标识和单行 Cookie。"; return; }
            try {
                var secret = new SchoolCredential(account.Text.Trim(), cookie.Password);
                if (save.IsChecked == true) PintiaVault.Save(secret); else PintiaVault.Forget();
                pintiaSession = secret;
                if (!Commit(() => store.Data.Assistant.PintiaEnabled = true)) return;
                connected = true; dialog.Close();
            } catch { status.Text = "本机加密保存失败，可取消保存后重试。"; }
        }, true));
        dialog.ShowDialog(); cookie.Clear(); if (connected) _ = RefreshAssistant(false);
    }
    async Task RefreshAssistant(bool interactive)
    {
        if (preview || exiting || assistantBusy || signBusy) return;
        if (assistantClient == null && !Vault.Exists && !store.Data.Assistant.PintiaEnabled) { if (interactive) AssistantLogin(); return; }
        assistantBusy = true; var token = assistantCancel.Token; assistantStatus = "正在更新课程待办与借阅信息…"; PaintAssistant();
        try
        {
            if (assistantClient == null && Vault.Exists)
            {
                var client = new CampusClient();
                try { var credential = Vault.Read() ?? throw new InvalidDataException("请先连接学校账号。"); await client.Login(credential, token); token.ThrowIfCancellationRequested(); assistantClient = client; }
                catch (Exception ex) {
                    client.Dispose(); token.ThrowIfCancellationRequested();
                    Commit(() => { store.Data.Assistant.Courses.Status = AssistantError(ex); store.Data.Assistant.Library.Status = AssistantError(ex); });
                }
            }
            var active = assistantClient; bool anyFailure = active == null;
            async Task Fetch(string source, Func<CancellationToken, Task<List<AssistantEntry>>> fetch)
            {
                try
                {
                    var entries = await fetch(token); token.ThrowIfCancellationRequested();
                    if (exiting || active != assistantClient) return;
                    Commit(() => {
                        AssistantMerge.Apply(store.Data, source, active!.Account, entries, DateTime.Now);
                        if (source == "courses" && active.CourseWarnings.Count > 0) store.Data.Assistant.Courses.Status = "已更新截止事项；" + string.Join("；", active.CourseWarnings);
                    });
                }
                catch (Exception ex)
                {
                    if (token.IsCancellationRequested || exiting) return;
                    anyFailure = true;
                    Commit(() => (source == "courses" ? store.Data.Assistant.Courses : store.Data.Assistant.Library).Status = AssistantError(ex));
                }
            }
            if (active != null) {
                await Fetch("courses", active.FetchCourses);
                if (!token.IsCancellationRequested) await Fetch("library", active.FetchLibrary);
            }
            if (!token.IsCancellationRequested && store.Data.Assistant.PintiaEnabled)
            {
                try {
                    var secret = pintiaSession ?? PintiaVault.Read() ?? throw new InvalidDataException("请重新设置 Pintia Cookie。");
                    var entries = await PintiaClient.Fetch(secret.Password, token); token.ThrowIfCancellationRequested();
                    if (!exiting && store.Data.Assistant.PintiaEnabled) Commit(() => AssistantMerge.Apply(store.Data, "pintia", Engine.Hash(secret.Username), entries, DateTime.Now));
                } catch (Exception ex) { if (!exiting && !token.IsCancellationRequested) Commit(() => store.Data.Assistant.Pintia.Status = AssistantError(ex)); }
            }
            if (!token.IsCancellationRequested) assistantStatus = anyFailure ? "部分服务未更新，请查看下方对应来源的具体原因；本地数据保留。" : "本次检查结束，各来源结果见更新时间。";
        }
        catch (Exception ex) { if (!exiting && !token.IsCancellationRequested) { assistantStatus = AssistantError(ex); assistantClient?.Dispose(); assistantClient = null; } }
        finally { assistantBusy = false; assistantNext = DateTime.Now.AddMinutes(10); PaintAssistant(); }
    }
    async Task RenewAssistant(AssistantEntry entry)
    {
        if (preview || assistantBusy || signBusy) return;
        if (assistantClient == null) { AssistantLogin(); return; }
        if (assistantClient.Account != store.Data.Assistant.Library.Account) { MessageBox.Show(this, "请先更新当前账号的借阅列表。"); return; }
        if (MessageBox.Show(this, "向图书馆申请续借《" + entry.Title + "》？", "确认续借", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        assistantBusy = true; var token = assistantCancel.Token; var client = assistantClient; bool accepted = false; PaintAssistant();
        try
        {
            await client.Renew(entry.Barcode, token); accepted = true;
            var entries = await client.FetchLibrary(token); token.ThrowIfCancellationRequested();
            if (!exiting && client == assistantClient && Commit(() => AssistantMerge.Apply(store.Data, "library", client.Account, entries, DateTime.Now))) assistantStatus = "续借成功，还书日期已按学校数据更新。";
        }
        catch (Exception ex) { if (!exiting) assistantStatus = accepted ? "学校已确认续借，但新日期读取失败。请点击更新，不要重复续借。" : "未能确认续借结果，请在官网核对后再试。" + AssistantError(ex); }
        finally { assistantBusy = false; PaintAssistant(); }
    }
    async Task ToggleAssistantSign()
    {
        if (signEnabled) { signEnabled = false; signCancel.Cancel(); signStatus = "已停止；已发送的请求请在官网核实结果。"; PaintAssistant(); return; }
        if (preview || assistantBusy || signBusy) return;
        if (assistantClient == null) { AssistantLogin(); return; }
        signCancel = CancellationTokenSource.CreateLinkedTokenSource(assistantCancel.Token); var token = signCancel.Token;
        signEnabled = true; signBusy = true; signStatus = "正在检查 Windows 定位权限…"; PaintAssistant();
        try
        {
            await DeviceLocation.Read(new WindowInteropHelper(this).Handle, true, token); token.ThrowIfCancellationRequested();
            signStatus = "已开启 · 每分钟检查 · 仅本次运行"; signNext = DateTime.MinValue;
        }
        catch (Exception ex) { signEnabled = false; signStatus = AssistantError(ex); }
        finally { signBusy = false; PaintAssistant(); }
        if (signEnabled) await PollAssistantSign();
    }
    async Task PollAssistantSign()
    {
        if (preview || exiting || !signEnabled || signBusy || assistantBusy || assistantClient == null) return;
        signBusy = true; signNext = DateTime.Now.AddMinutes(1); var client = assistantClient; var token = signCancel.Token;
        try
        {
            var calls = await client.Rollcalls(token); token.ThrowIfCancellationRequested();
            signStatus = "已开启 · 最近检查 " + DateTime.Now.ToString("HH:mm:ss");
            foreach (var call in calls)
            {
                token.ThrowIfCancellationRequested(); if (!signEnabled || client != assistantClient) return;
                if (AssistantParser.S(call, "rollcall_status") != "in_progress" || AssistantParser.SignSuccess(call)) continue;
                string id = AssistantParser.Id(call, "rollcall_id"), key = client.Account + ":" + id;
                string title = AssistantParser.S(call, "course_title") + " · " + AssistantParser.S(call, "title");
                if (!AssistantParser.B(call, "is_radar") || AssistantParser.B(call, "is_number"))
                {
                    if (signNotified.Add(key)) { Commit(() => AssistantMerge.Log(store.Data, title + "：需要在学在浙大完成数字或二维码签到。")); tray?.ShowBalloonTip(5000, "课堂签到", title + "：请打开学在浙大完成。", System.Windows.Forms.ToolTipIcon.Info); }
                    continue;
                }
                if (store.Data.Assistant.SignAttempted.Contains(key)) continue;
                try
                {
                    var position = await DeviceLocation.Read(new WindowInteropHelper(this).Handle, false, token);
                    token.ThrowIfCancellationRequested(); if (!signEnabled || client != assistantClient) return;
                    // Persist an attempt before sending. An ambiguous response must not cause duplicate submissions.
                    if (!Commit(() => { store.Data.Assistant.SignAttempted.Add(key); AssistantMerge.Log(store.Data, title + "：正在提交雷达签到；结果待学校确认。"); })) return;
                    token.ThrowIfCancellationRequested();
                    bool ok = await client.Sign(id, store.Data.Assistant.DeviceId, position, token);
                    token.ThrowIfCancellationRequested();
                    string outcome = ok ? "学校已确认签到成功。" : "学校未确认成功，请打开学在浙大核实。";
                    Commit(() => AssistantMerge.Log(store.Data, title + "：" + outcome));
                    tray?.ShowBalloonTip(5000, "课堂签到", title + "：" + outcome, System.Windows.Forms.ToolTipIcon.Info);
                }
                catch (Exception ex)
                {
                    if (token.IsCancellationRequested) return;
                    if (signNotified.Add(key))
                    {
                        Commit(() => AssistantMerge.Log(store.Data, title + "：" + AssistantError(ex)));
                        tray?.ShowBalloonTip(5000, "签到需要处理", title + "：请在学在浙大确认签到。", System.Windows.Forms.ToolTipIcon.Warning);
                    }
                }
            }
        }
        catch (Exception ex) { if (!token.IsCancellationRequested) { signEnabled = false; signStatus = "已暂停：" + AssistantError(ex); Commit(() => AssistantMerge.Log(store.Data, signStatus)); tray?.ShowBalloonTip(5000, "自动签到已暂停", "连接失败，请在校园助手重新连接。", System.Windows.Forms.ToolTipIcon.Warning); } }
        finally { signBusy = false; PaintAssistant(); }
    }
    async Task AssistantTick()
    {
        RefreshAssistantLabel();
        if (preview || exiting || OwnedWindows.Count > 0) return;
        if (signEnabled && DateTime.Now >= signNext) await PollAssistantSign();
        if (store.Data.Assistant.AutoRefresh && DateTime.Now >= assistantNext) await RefreshAssistant(false);
    }
}

