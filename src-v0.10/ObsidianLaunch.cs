using System;
using System.IO;
using System.Text.Json;

namespace Shike;
public static class ObsidianLaunch
{
    // Read-only lookup: Obsidian owns its registration file. Never create/edit it ourselves.
    public static string? RegisteredVault(string vault)
    {
        try
        {
            string config = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "obsidian", "obsidian.json");
            return File.Exists(config) ? FindVaultId(File.ReadAllText(config), vault) : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
    public static string? FindVaultId(string json, string vault)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("vaults", out var entries) || entries.ValueKind != JsonValueKind.Object) return null;
            string expected = Path.GetFullPath(vault).TrimEnd('\\', '/');
            foreach (var entry in entries.EnumerateObject())
            {
                if (entry.Value.ValueKind != JsonValueKind.Object || !entry.Value.TryGetProperty("path", out var path) || path.ValueKind != JsonValueKind.String) continue;
                string? candidate = path.GetString();
                if (!string.IsNullOrWhiteSpace(candidate) && Path.IsPathFullyQualified(candidate) && string.Equals(Path.GetFullPath(candidate).TrimEnd('\\', '/'), expected, StringComparison.OrdinalIgnoreCase)) return entry.Name;
            }
        }
        catch (JsonException) { }
        catch (ArgumentException) { }
        catch (NotSupportedException) { }
        return null;
    }
    public static string UriFor(string? id) => string.IsNullOrWhiteSpace(id) ? "obsidian://choose-vault" : "obsidian://open?vault=" + Uri.EscapeDataString(id);
}
