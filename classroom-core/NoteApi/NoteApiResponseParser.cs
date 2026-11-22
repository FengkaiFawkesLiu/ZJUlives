using System.Text;
using System.Text.Json;

namespace ZJUSS.Core.NoteApi;

/// <summary>读取 OpenAI 兼容响应，只返回 choices[0].message.content 原文。</summary>
internal static class NoteApiResponseParser
{
    internal static NoteApiResponseParseResult Parse(ReadOnlyMemory<byte> body)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return NoteApiResponseParseResult.Unreadable("响应不是有效的 JSON。");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return NoteApiResponseParseResult.Unreadable("响应结构无法识别。");
            }

            if (!root.TryGetProperty("choices", out var choices) ||
                choices.ValueKind != JsonValueKind.Array ||
                choices.GetArrayLength() == 0)
            {
                return NoteApiResponseParseResult.Unreadable("响应缺少 choices。");
            }

            var choice = choices[0];
            if (choice.ValueKind != JsonValueKind.Object)
            {
                return NoteApiResponseParseResult.Unreadable("响应中的 choices 结构无法识别。");
            }

            if (!choice.TryGetProperty("message", out var message) ||
                message.ValueKind != JsonValueKind.Object)
            {
                return NoteApiResponseParseResult.Unreadable("响应缺少 message。");
            }

            if (!message.TryGetProperty("content", out var content))
            {
                return NoteApiResponseParseResult.Unreadable("响应缺少正文内容。");
            }

            if (content.ValueKind == JsonValueKind.Null)
            {
                return NoteApiResponseParseResult.Truncated("响应正文为空。");
            }

            var finishReason = choice.TryGetProperty("finish_reason", out var finish) &&
                               finish.ValueKind == JsonValueKind.String
                ? finish.GetString()
                : null;
            if (string.Equals(finishReason, "length", StringComparison.OrdinalIgnoreCase))
            {
                return NoteApiResponseParseResult.Truncated("响应因长度限制被截断。");
            }

            string? text;
            if (content.ValueKind == JsonValueKind.String)
            {
                text = content.GetString();
            }
            else if (content.ValueKind == JsonValueKind.Array)
            {
                text = ReadContentParts(content, out var recognized);
                if (!recognized)
                {
                    return NoteApiResponseParseResult.Unreadable("响应正文结构无法识别。");
                }
            }
            else
            {
                return NoteApiResponseParseResult.Unreadable("响应正文结构无法识别。");
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                return NoteApiResponseParseResult.Truncated("响应正文为空。");
            }

            return NoteApiResponseParseResult.Ok(text);
        }
    }

    /// <summary>
    /// 兼容 content 为文本块数组的响应形态；只拼接文本块，忽略图片等非文本块，
    /// 不会下载或读取任何外部资源。
    /// </summary>
    private static string ReadContentParts(JsonElement content, out bool recognized)
    {
        recognized = true;
        var builder = new StringBuilder();
        foreach (var part in content.EnumerateArray())
        {
            if (part.ValueKind == JsonValueKind.String)
            {
                builder.Append(part.GetString());
                continue;
            }

            if (part.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (part.TryGetProperty("type", out var type) &&
                type.ValueKind == JsonValueKind.String &&
                !string.Equals(type.GetString(), "text", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (part.TryGetProperty("text", out var text) &&
                text.ValueKind == JsonValueKind.String)
            {
                builder.Append(text.GetString());
            }
        }

        return builder.ToString();
    }
}

internal readonly record struct NoteApiResponseParseResult(
    bool Success,
    string? Content,
    NoteApiFailureKind Failure,
    string? Detail)
{
    internal static NoteApiResponseParseResult Ok(string content) =>
        new(true, content, NoteApiFailureKind.None, null);

    internal static NoteApiResponseParseResult Truncated(string detail) =>
        new(false, null, NoteApiFailureKind.ResponseTruncated, detail);

    internal static NoteApiResponseParseResult Unreadable(string detail) =>
        new(false, null, NoteApiFailureKind.ResponseUnreadable, detail);
}
