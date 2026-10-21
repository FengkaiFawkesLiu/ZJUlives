using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Shike;

public static class PintiaClient
{
    public static async Task<List<AssistantEntry>> Fetch(string cookie, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cookie) || cookie.Contains('\r') || cookie.Contains('\n')) throw new InvalidDataException("Pintia Cookie 无效，请重新连接。");
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(25) };
        // Bounded window includes overdue work. Never infer completion from absence in this list.
        string filter = Uri.EscapeDataString(JsonSerializer.Serialize(new { endAtAfter = DateTime.UtcNow.AddDays(-90).ToString("O") }));
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://pintia.cn/api/problem-sets?filter=" + filter + "&limit=100&order_by=END_AT&asc=true");
        req.Headers.Add("Cookie", cookie); req.Headers.Add("Accept", "application/json"); req.Headers.Referrer = new Uri("https://pintia.cn/problem-sets/dashboard");
        using var res = await http.SendAsync(req, ct);
        if ((int)res.StatusCode is >= 300 and < 400) throw new InvalidDataException("Pintia 会话已过期，请更新 Cookie。");
        res.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        var rows = AssistantParser.Array(doc.RootElement, "problemSets");
        if (rows.Count >= 100) throw new InvalidDataException("Pintia 返回了列表上限，无法确认完整性，请在官网查看；保留本地记录。");
        return rows.Select(r => new AssistantEntry { Key = AssistantParser.Id(r), Title = AssistantParser.S(r, "name"), Due = AssistantParser.Date(AssistantParser.S(r, "endAt")), Course = AssistantParser.S(r, "organizationName"), Url = "https://pintia.cn/problem-sets/" + AssistantParser.Id(r) }).ToList();
    }
}
