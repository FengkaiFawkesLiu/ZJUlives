using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using static Shike.Theme;

namespace Shike;

public sealed record SyncSummary(int Added, int Updated, int Conflicts, int Removed);

public static class SchoolSync
{
    public static async Task<SchoolBatch> Fetch(SchoolCredential credential, Settings settings, Func<ISchoolProvider>? factory = null)
    {
        using var provider = factory?.Invoke() ?? new ZjuUndergraduateProvider();
        await provider.Login(credential.Username, credential.Password);
        return await provider.Fetch(settings, "");
    }
    public static SyncSummary Apply(AppData data, SchoolBatch batch, string account, Settings term, DateTime now, bool allowRemoval)
    {
        string scope = "zju-" + Engine.Hash(account) + "-" + term.Year + "-" + term.Semester;
        if (batch.Courses.Count == 0 && batch.Exams.Count == 0 && data.Items.Any(i => i.Scope == scope + "-course" || i.Scope == scope + "-exam"))
            throw new InvalidDataException("学校本次返回空列表；保留已有记录与上次更新时间，请核对学期或稍后重试。");
        bool complete = allowRemoval && batch.Warnings.Count == 0;
        var courses = batch.Courses.Select(c => AcademicCalendar.Adjust(c, data.Settings.ApplyAcademicHolidays)).ToList();
        // An empty collection is never considered proof that every old class disappeared.
        var cr = SchoolMerge.Apply(data, scope + "-course", courses, complete && courses.Count > 0);
        var er = SchoolMerge.Apply(data, scope + "-exam", batch.Exams, complete && batch.Exams.Count > 0);
        data.Settings.LastSync = now;
        data.Settings.TermConfigured = true;
        string message = $"{now:MM-dd HH:mm} · 学校更新：新增 {cr.Added + er.Added}，更新 {cr.Updated + er.Updated}，冲突 {cr.Conflicts + er.Conflicts}，取消 {cr.Removed + er.Removed}。";
        data.Alerts.Add(message);
        foreach (var warning in batch.Warnings.Take(30)) data.Alerts.Add($"{now:MM-dd HH:mm} · 本次未完整更新：{warning}");
        return new(cr.Added + er.Added, cr.Updated + er.Updated, cr.Conflicts + er.Conflicts, cr.Removed + er.Removed);
    }
}

public sealed partial class MainWindow
{
    bool syncBusy;
    int syncGeneration;
    string syncStatus = "";
    TextBlock? schoolFreshness;
    CredentialVault Vault => new(store.Root);
    void UpdateFreshness()
    {
        string when = Freshness.Format(store.Data.Settings.LastSync, DateTime.Now);
        footer.Text = $"● 本地保存  ·  {store.Data.Items.Count} 条记录  ·  {when}" + (syncBusy ? "  ·  正在更新…" : "") + "  ·  Ctrl+N 新建";
        if (syncStatus.Length > 0) footer.Text += "\n" + syncStatus;
        footer.ToolTip = store.Data.Settings.LastSync is DateTime t ? "上次成功更新：" + t.ToString("yyyy-MM-dd HH:mm:ss") : "尚无成功更新记录";
        if (schoolFreshness != null) schoolFreshness.Text = when + (syncStatus.Length == 0 ? "" : "\n" + syncStatus);
    }
    async Task StartupSync()
    {
        if (preview || exiting) return;
        if (store.Data.Settings.AutoSyncOnStartup)
        {
            if (Vault.Exists && (store.Data.Settings.TermConfigured || store.Data.Settings.LastSync != null)) await SyncNow();
            else if (store.Data.Settings.LastSync != null)
            {
                syncStatus = "未保存账号密码，请登录完成本次启动更新。"; Render();
                SchoolDialog();
            }
            else { syncStatus = "请先连接一次学校并确认学期参数；之后每次启动自动更新。"; UpdateFreshness(); }
        }
        if (!exiting && store.Data.Settings.ApplyAcademicHolidays) await CheckHolidayNotice();
    }
    async Task SyncNow()
    {
        if (syncBusy || exiting) return;
        if (!Vault.Exists) { SchoolDialog(); return; }
        if (!store.Data.Settings.TermConfigured && store.Data.Settings.LastSync == null) { SchoolDialog(); return; }
        syncBusy = true; int generation = syncGeneration; syncStatus = "正在连接学校并读取课表、考试与地点…"; Render();
        try
        {
            var credential = Vault.Read() ?? throw new InvalidDataException("本地凭据已删除，请重新登录。");
            var settings = JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(store.Data.Settings))!;
            var batch = await SchoolSync.Fetch(credential, settings);
            if (exiting || generation != syncGeneration) return;
            SyncSummary? summary = null;
            if (Commit(() => summary = SchoolSync.Apply(store.Data, batch, credential.Username, settings, DateTime.Now, true)))
            {
                syncStatus = batch.Warnings.Count == 0 ? "本次启动更新完成。" : $"已更新可识别记录，另有 {batch.Warnings.Count} 项提示，请在提醒记录查看。";
                if (summary!.Conflicts > 0)
                {
                    syncStatus += $"有 {summary.Conflicts} 项学校更新冲突，已保留你的安排。";
                    tray?.ShowBalloonTip(8000, "学校更新存在冲突", "请在“冲突与提醒”中确认安排。", System.Windows.Forms.ToolTipIcon.Warning);
                    SwitchView("冲突与提醒");
                }
            }
            else syncStatus = "数据已读取，但本地保存失败；更新时间未改变。";
        }
        catch (CaptchaRequiredException) { syncStatus = "学校要求验证码，自动更新暂停。请点击“账号与连接”填写验证码；当前显示上次成功更新的数据。"; }
        catch (CryptographicException) { syncStatus = "无法解密本地账号，可能更换了 Windows 用户。请删除已保存账号并重新连接。"; }
        catch (Exception ex) { syncStatus = ex is TaskCanceledException or HttpRequestException ? "连接学校失败或超时，保留本地数据；可稍后重新连接。" : "更新未完成：" + ex.Message; }
        finally { syncBusy = false; if (!exiting) { Render(); UpdateFreshness(); } }
    }
    async Task CheckHolidayNotice()
    {
        try
        {
            bool same = await AcademicCalendar.CheckNotice(); if (exiting) return;
            Commit(() => { store.Data.Settings.LastHolidayCheck = DateTime.Now; store.Data.Settings.HolidayCheckStatus = same ? "官网通知已核对，已知调课规则未改变" : "官网通知有变化或格式无法识别：保留已核对规则，请查看原文"; });
        }
        catch { if (!exiting) { store.Data.Settings.HolidayCheckStatus = "本次未能联网核对通知，继续使用已缓存的 2026 下半年规则"; UpdateFreshness(); } }
    }
    void ForgetCredentials()
    {
        DisconnectAssistant(); syncGeneration++;
        var failures = new List<string>();
        try { Vault.Forget(); } catch { failures.Add("账号密码文件未能删除，下次启动仍可能自动登录"); }
        try { AcademicLogout.Purge(store); } catch { failures.Add("部分课程、考试或历史缓存未能清除，请检查数据文件夹写入权限后重试退出"); }
        assistantStatus = syncStatus = failures.Count == 0
            ? "已退出学校账号：课程、考试及其关联已清除，笔记、待办与其他活动保留。"
            : "本次会话已停止，但退出未完全完成：" + string.Join("；", failures);
        Render(); UpdateFreshness();
        if (failures.Count > 0) MessageBox.Show(this, string.Join("\n", failures), "退出未完全完成");
    }
    UIElement HolidayBanner(DateTime day)
    {
        var notice = store.Data.Settings.ApplyAcademicHolidays ? AcademicCalendar.Day(day) : null;
        if (notice == null) return new Border();
        return Card(Stack(Text("◈  " + notice.Label, 15, Green, true), Space(5), Text(notice.Detail, 12, Muted)), "#EEF3E8", 14);
    }
}

