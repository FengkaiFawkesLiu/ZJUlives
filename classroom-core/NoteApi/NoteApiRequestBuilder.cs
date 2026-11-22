using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZJUSS.Core.NoteApi;

/// <summary>构造 OpenAI 兼容的纯文本 Chat Completions 请求体。</summary>
internal static class NoteApiRequestBuilder
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>输入字符数：指令 + 课程/课节元数据 + 两种文字，不包含 JSON 包装。</summary>
    internal static long CountInputCharacters(NoteApiRequest request)
    {
        return Length(request.Instructions) +
               Length(request.CourseName) +
               Length(request.Term) +
               Length(request.LessonDateText) +
               Length(request.SessionText) +
               Length(request.CoursewareText) +
               Length(request.TranscriptText);
    }

    internal static string BuildUserContent(NoteApiRequest request)
    {
        var builder = new StringBuilder();
        builder.Append("课程名称：").Append(request.CourseName ?? string.Empty).Append('\n');
        builder.Append("学期：").Append(request.Term ?? string.Empty).Append('\n');
        if (!string.IsNullOrWhiteSpace(request.LessonDateText))
        {
            builder.Append("课节日期：").Append(request.LessonDateText).Append('\n');
        }

        if (!string.IsNullOrWhiteSpace(request.SessionText))
        {
            builder.Append("节次：").Append(request.SessionText).Append('\n');
        }

        if (request.HasCoursewareText && request.HasTranscriptText)
        {
            builder.Append("材料说明：本节课同时提供课件文字和语音文字，请只依据以下两种材料生成笔记。\n");
        }
        else if (request.HasCoursewareText)
        {
            builder.Append("材料说明：本节课没有可用的语音文字，仅提供课件文字。请不要编造或补全语音内容。\n");
        }
        else if (request.HasTranscriptText)
        {
            builder.Append("材料说明：本节课没有可用的课件文字，仅提供语音文字。请不要编造或补全课件内容。\n");
        }

        if (request.HasCoursewareText)
        {
            builder.Append("\n【课件文字】\n");
            builder.Append(request.CoursewareText);
            builder.Append('\n');
        }

        if (request.HasTranscriptText)
        {
            builder.Append("\n【语音文字】\n");
            builder.Append(request.TranscriptText);
            builder.Append('\n');
        }

        return builder.ToString();
    }

    internal static byte[] BuildRequestBody(
        string model,
        NoteApiRequest request,
        NoteApiOptions options)
    {
        var payload = new ChatCompletionRequest
        {
            Model = model,
            Stream = false,
            MaxTokens = options.MaxCompletionTokens,
            Messages =
            [
                new ChatMessage { Role = "system", Content = request.Instructions ?? string.Empty },
                new ChatMessage { Role = "user", Content = BuildUserContent(request) }
            ]
        };

        return JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
    }

    private static long Length(string? value) => value?.Length ?? 0;

    private sealed class ChatCompletionRequest
    {
        [JsonPropertyName("model")]
        public required string Model { get; init; }

        [JsonPropertyName("messages")]
        public required IReadOnlyList<ChatMessage> Messages { get; init; }

        [JsonPropertyName("stream")]
        public bool Stream { get; init; }

        [JsonPropertyName("max_tokens")]
        public int MaxTokens { get; init; }
    }

    private sealed class ChatMessage
    {
        [JsonPropertyName("role")]
        public required string Role { get; init; }

        [JsonPropertyName("content")]
        public required string Content { get; init; }
    }
}
