using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Shike;

public sealed partial class CampusClient
{
    List<OpacTarget>? opacTargets;
    public int LibraryHistoryTotal { get; private set; }
    async Task EnsureOpac(CancellationToken ct)
    {
        if (opacTargets != null) return;
        Stage = "图书馆官网认证";
        opacTargets = OpacParser.Targets(await Follow(new Uri("https://opac.zju.edu.cn/zjusso/"), ct));
    }
    public async Task<List<AssistantEntry>> FetchLibraryHistory(CancellationToken ct)
    {
        await EnsureOpac(ct); Stage = "读取历史借阅";
        var entries = new List<AssistantEntry>(); int total = 0;
        foreach (var target in opacTargets!)
        {
            var uri = new UriBuilder(target.Url) { Query = "func=bor-history-loan&adm_library=" + Uri.EscapeDataString(target.Library) }.Uri;
            var page = OpacParser.Read(await Follow(uri, ct), target.Library, true);
            entries.AddRange(page.Entries); total += page.Total;
        }
        LibraryHistoryTotal = total;
        return entries;
    }
}

internal sealed record OpacTarget(Uri Url, string Library, int Count);
internal sealed record OpacPage(List<AssistantEntry> Entries, int Total);

internal static class OpacParser
{
    const RegexOptions Options = RegexOptions.Singleline | RegexOptions.IgnoreCase;
    public const string Website = "https://opac.zju.edu.cn/F/?func=bor-info";
    static string Clean(string html) => Regex.Replace(Regex.Replace(html, @"<!--.*?-->", "", Options), @"<(script|style)\b[^>]*>.*?</\1>", "", Options);
    static string Text(string html) => Regex.Replace(WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]+>", " ")), @"\s+", " ").Trim();
    static string Attribute(string tag, string name)
    {
        var m = Regex.Match(tag, @"\b" + name + "\\s*=\\s*(?:\"([^\"]*)\"|'([^']*)'|([^\\s>]+))", Options);
        return WebUtility.HtmlDecode(m.Groups.Cast<Group>().Skip(1).FirstOrDefault(g => g.Success)?.Value ?? "");
    }
    static bool Template(string html, string name) => Regex.IsMatch(html, @"<!--\s*filename:\s*" + Regex.Escape(name) + @"\s*-->", Options);
    static void CheckSession(string html)
    {
        if (Template(html, "login-session") || Regex.IsMatch(html, @"name\s*=\s*['" + "\"" + @"]execution['" + "\"" + @"]", Options))
            throw new CampusSessionExpiredException("图书馆官网登录已失效，请重新连接；已有记录保留。");
    }
    static string Query(Uri uri, string key)
    {
        foreach (var pair in uri.Query.TrimStart('?').Split('&'))
        {
            var parts = pair.Split('=', 2);
            if (Uri.UnescapeDataString(parts[0]).Equals(key, StringComparison.OrdinalIgnoreCase)) return parts.Length == 2 ? Uri.UnescapeDataString(parts[1]) : "";
        }
        return "";
    }
    public static List<OpacTarget> Targets(string html)
    {
        CheckSession(html);
        if (!Template(html, "bor-info-single-adm") && !Template(html, "bor-info")) throw new InvalidDataException("图书馆官网未返回读者借阅概况，已有记录保留。");
        var targets = new Dictionary<string, OpacTarget>();
        foreach (Match anchor in Regex.Matches(Clean(html), @"<a\b[^>]*>(.*?)</a>", Options))
        {
            string href = Attribute(anchor.Value[..(anchor.Value.IndexOf('>') + 1)], "href");
            // Extract an HTTPS URL from the site's replacePage('…') wrapper; never execute JavaScript.
            var match = Regex.Match(href, @"https://[^\s'" + "\"" + @"<>]+", Options);
            if (!match.Success || !Uri.TryCreate(match.Value, UriKind.Absolute, out var uri)) continue;
            if (uri.Host != "opac.zju.edu.cn" || !CampusClient.Allowed(uri) || !uri.AbsolutePath.StartsWith("/F/", StringComparison.Ordinal)) continue;
            if (Query(uri, "func") != "bor-loan") continue;
            string library = Query(uri, "adm_library");
            if (!Regex.IsMatch(library, @"^[A-Z0-9]{3,12}$") || !int.TryParse(Text(anchor.Groups[1].Value), out int count) || count < 0 || count > 20000) continue;
            if (targets.TryGetValue(library, out var old) && old.Count != count) throw new InvalidDataException("图书馆借阅概况数量不一致，已有记录保留。");
            targets[library] = new(uri, library, count);
        }
        if (targets.Count == 0) throw new InvalidDataException("无法识别图书馆读者管理库，未把未知结果视为零借阅。");
        return targets.Values.ToList();
    }
    static DateTime? Date(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text is "-" or "--") return null;
        string[] formats = { "yyyyMMdd", "yyyy-MM-dd", "yyyy/MM/dd", "dd/MM/yyyy", "dd.MM.yyyy", "yyyy-M-d", "yyyy/M/d", "d/M/yyyy", "yyyy年M月d日" };
        if (!DateTime.TryParseExact(text.Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) || date.Year < 2000 || date.Year > 2200)
            throw new InvalidDataException("图书馆日期格式发生变化，已有记录保留。");
        return date;
    }
    public static OpacPage Read(string html, string library, bool history)
    {
        CheckSession(html);
        string stem = history ? "bor-history-loan" : "bor-loan";
        string clean = Clean(html);
        if (Template(html, stem + "-no-loan"))
        {
            if (Template(html, stem + "-body")) throw new InvalidDataException("图书馆空列表与记录冲突，已有记录保留。");
            return new(new(), 0);
        }
        if (!Template(html, stem + "-head")) throw new InvalidDataException("图书馆未返回有效的" + (history ? "历史借阅" : "当前借阅") + "列表，已有记录保留。");
        var entries = new List<AssistantEntry>(); bool found = false;
        foreach (Match table in Regex.Matches(clean, @"<table\b[^>]*>(.*?)</table>", Options))
        {
            var headings = Regex.Matches(table.Groups[1].Value, @"<th\b[^>]*>(.*?)</th>", Options).Cast<Match>().Select(m => Text(m.Groups[1].Value)).ToList();
            int title = headings.IndexOf("题名"), due = headings.IndexOf("应还日期"), returned = headings.IndexOf("归还日期");
            if (title < 0 || due < 0 || history && returned < 0) continue;
            if (found) throw new InvalidDataException("图书馆返回多个借阅表格，无法确认完整记录。");
            found = true;
            int author = headings.IndexOf("著者"), branch = headings.IndexOf("分馆"), call = headings.IndexOf("索书号");
            foreach (Match row in Regex.Matches(table.Groups[1].Value, @"<tr\b[^>]*>(.*?)</tr>", Options))
            {
                if (Regex.IsMatch(row.Value, @"<th\b", Options)) continue;
                var cells = Regex.Matches(row.Groups[1].Value, @"<td\b[^>]*>(.*?)</td>", Options).Cast<Match>().Select(m => m.Groups[1].Value).ToList();
                if (cells.Count == 0) continue;
                if (cells.Count != headings.Count) throw new InvalidDataException("图书馆借阅表格列数发生变化，已有记录保留。");
                string Cell(int i) => i >= 0 ? Text(cells[i]) : "";
                var dueDate = Date(Cell(due));
                if (Cell(title).Length == 0 || !history && dueDate == null) throw new InvalidDataException("图书馆借阅记录缺少书名或应还日期，已有记录保留。");
                DateTime? returnedDate = history ? Date(Cell(returned)) : null;
                string identity = "";
                if (!history)
                {
                    foreach (Match input in Regex.Matches(row.Value, @"<input\b[^>]*>", Options))
                    {
                        string name = Attribute(input.Value, "name");
                        if (Attribute(input.Value, "type").Equals("checkbox", StringComparison.OrdinalIgnoreCase) && Regex.IsMatch(name, @"^[A-Za-z0-9_-]{8,80}$")) { identity = name; break; }
                    }
                }
                if (identity.Length == 0) identity = Cell(title) + "|" + Cell(author) + "|" + Cell(branch) + "|" + Cell(call);
                if (history) identity += "|" + Cell(due) + "|" + Cell(returned) + "|" + Cell(0);
                entries.Add(new() {
                    Key = "opac:" + library + ":" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..24],
                    Title = Cell(title), Course = Cell(author), Branch = Cell(branch), CallNumber = Cell(call),
                    Due = dueDate?.AddHours(23).AddMinutes(59), ReturnedAt = returnedDate,
                    Complete = history, Renewable = false, Url = Website
                });
            }
        }
        if (!found || entries.Count == 0 || entries.Select(e => e.Key).Distinct().Count() != entries.Count) throw new InvalidDataException("图书馆借阅列表不完整或包含重复记录，已有记录保留。");
        int total = entries.Count;
        if (history)
        {
            var count = Regex.Match(Text(clean), @"总共\s*(\d+)\s*条中的\s*(\d+)\s*条");
            if (!count.Success || !int.TryParse(count.Groups[1].Value, out total) || !int.TryParse(count.Groups[2].Value, out var shown) || shown != entries.Count || total < shown)
                throw new InvalidDataException("图书馆历史借阅数量与列表不一致，已有记录保留。");
        }
        return new(entries, total);
    }
}
