using System.Text.Json;
using ZJUSS.Core.Models;

namespace ZJUSS.Core.Adapters.Shared;

internal static class JsonValueReader
{
    public static string? GetOptionalString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        var value = ReadScalar(property);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    public static string GetRequiredString(JsonElement element, string propertyName, string phase)
    {
        var value = GetOptionalString(element, propertyName);
        if (value is null)
        {
            throw new AdapterFailureException(
                SourceFailureReason.SchemaChanged,
                phase,
                $"缺少必需字段 {propertyName}。");
        }

        return value;
    }

    public static string? ReadScalar(JsonElement element)
        => element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
}
