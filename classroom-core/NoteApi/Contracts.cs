using System.Text.Json.Serialization;

namespace ZJUSS.Core.NoteApi;

/// <summary>
/// 一次生成任务使用的内存配置快照。只能在解锁会话中取得；
/// 不序列化、不写入日志、备份、笔记、待办或导出文件。
/// </summary>
public sealed class NoteApiConfigSnapshot
{
    public NoteApiConfigSnapshot(string endpoint, string apiKey, string model, bool remoteHttpConfirmed)
    {
        Endpoint = endpoint ?? string.Empty;
        ApiKey = apiKey ?? string.Empty;
        Model = model ?? string.Empty;
        RemoteHttpConfirmed = remoteHttpConfirmed;
    }

    [JsonIgnore]
    public string Endpoint { get; }

    [JsonIgnore]
    public string ApiKey { get; }

    [JsonIgnore]
    public string Model { get; }

    [JsonIgnore]
    public bool RemoteHttpConfirmed { get; }

    public bool HasEndpoint => !string.IsNullOrWhiteSpace(Endpoint);

    public bool HasApiKey => !string.IsNullOrWhiteSpace(ApiKey);

    public bool HasModel => !string.IsNullOrWhiteSpace(Model);

    public override string ToString() =>
        $"NoteApiConfigSnapshot {{ Endpoint = {MaskEndpoint(Endpoint)}, ApiKey = ***, Model = {Model}, RemoteHttpConfirmed = {RemoteHttpConfirmed} }}";

    private static string MaskEndpoint(string endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return "(empty)";
        }

        return Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
            ? uri.GetLeftPart(UriPartial.Authority) + "/***"
            : "***";
    }
}

/// <summary>固定生成指令与所选课节的文字材料；不含文件、图片或二进制字段。</summary>
public sealed class NoteApiRequest
{
    public required string CourseName { get; init; }
    public required string Term { get; init; }
    public string? LessonDateText { get; init; }
    public string? SessionText { get; init; }

    /// <summary>本机从一份课件提取的文字；不可用时为空。</summary>
    public string? CoursewareText { get; init; }

    /// <summary>本机保存的智云课堂语音文字原文；不可用时为空。</summary>
    public string? TranscriptText { get; init; }

    /// <summary>M15 生成并固定的中文指令；M14 不修改。</summary>
    public required string Instructions { get; init; }

    public bool HasCoursewareText => !string.IsNullOrWhiteSpace(CoursewareText);

    public bool HasTranscriptText => !string.IsNullOrWhiteSpace(TranscriptText);

    public bool HasMaterial => HasCoursewareText || HasTranscriptText;
}

public enum NoteApiFailureKind
{
    None,
    NotConfigured,
    InvalidConfiguration,
    RemoteHttpNotConfirmed,
    NoMaterial,
    AuthenticationFailed,
    InsufficientBalance,
    RateLimited,
    NetworkUnreachable,
    Timeout,
    MaterialTooLong,
    ResponseUnreadable,
    ResponseTruncated,
    ServerError,
    Cancelled
}

public sealed class NoteApiResult
{
    public bool Success { get; init; }
    public string? Content { get; init; }
    public NoteApiFailureKind Failure { get; init; } = NoteApiFailureKind.None;
    public string? Detail { get; init; }
    public int? HttpStatusCode { get; init; }

    public static NoteApiResult Ok(string content) => new()
    {
        Success = true,
        Content = content,
        Failure = NoteApiFailureKind.None
    };

    public static NoteApiResult Fail(NoteApiFailureKind failure, string? detail = null, int? statusCode = null) =>
        new()
        {
            Success = false,
            Failure = failure,
            Detail = detail,
            HttpStatusCode = statusCode
        };
}

public sealed class NoteApiOptions
{
    /// <summary>单次请求最大输入字符数（课件文字 + 语音文字 + 元数据 + 指令）。</summary>
    public int MaxInputCharacters { get; init; } = 200_000;

    /// <summary>读取响应的最大字节数；超过按“响应无法读取/过大”失败。</summary>
    public int MaxResponseBytes { get; init; } = 8 * 1024 * 1024;

    /// <summary>一次请求的完成 token 上限；必须为 reasoning + 正文留出空间。</summary>
    public int MaxCompletionTokens { get; init; } = 32_768;

    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromMinutes(5);
}

public interface INoteApiClient
{
    /// <summary>
    /// 发送一次纯文本生成请求。配置校验失败、材料为空或远程 HTTP 未确认时不发起网络请求。
    /// 不自动重试、不拆分材料、不读取文件。
    /// </summary>
    Task<NoteApiResult> GenerateAsync(
        NoteApiConfigSnapshot config,
        NoteApiRequest request,
        CancellationToken cancellationToken = default);
}

public enum NoteApiConfigSaveError
{
    None,
    NotUnlocked,
    EmptyEndpoint,
    EmptyApiKey,
    EmptyModel,
    InvalidEndpoint,
    RemoteHttpNotConfirmed,
    SaveFailed,
    CryptoFailed,
    Corrupted,
    Incompatible
}

public sealed class NoteApiConfigStatus
{
    public bool Configured { get; init; }
    public string MaskedKeyHint { get; init; } = string.Empty;
    public bool RemoteHttpRisk { get; init; }
    public string? EndpointHost { get; init; }
    public string? Model { get; init; }
}

public sealed class NoteApiConfigSaveResult
{
    public bool Success { get; init; }
    public NoteApiConfigSaveError Error { get; init; } = NoteApiConfigSaveError.None;
    public string Message { get; init; } = string.Empty;
    public NoteApiConfigStatus Status { get; init; } = new();
}

public sealed class NoteApiConfigInput
{
    public required string Endpoint { get; init; }
    public required string ApiKey { get; init; }
    public required string Model { get; init; }

    /// <summary>仅在地址为远程 HTTP 时必须为 true。</summary>
    public bool ConfirmRemoteHttp { get; init; }
}

public interface INoteApiConfigService
{
    Task<NoteApiConfigStatus> GetStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>只在解锁会话中返回内存快照；未配置、未解锁或密文损坏时返回 null。</summary>
    Task<NoteApiConfigSnapshot?> GetSnapshotAsync(CancellationToken cancellationToken = default);

    Task<NoteApiConfigSaveResult> SaveAsync(
        NoteApiConfigInput input,
        CancellationToken cancellationToken = default);

    Task<NoteApiConfigSaveResult> ClearAsync(CancellationToken cancellationToken = default);
}

public static class NoteApiEndpointPolicy
{
    public static bool IsLocalHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        // Uri.Host 对 IPv6 保留方括号（例如 [::1]），这里统一去掉后再比较。
        var normalized = host.Length >= 2 && host[0] == '[' && host[^1] == ']'
            ? host[1..^1]
            : host;

        return string.Equals(normalized, "localhost", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(normalized, "127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(normalized, "::1", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>返回 true 表示需要使用者确认远程 HTTP 风险。</summary>
    public static bool TryValidate(
        string? endpoint,
        out Uri? uri,
        out NoteApiConfigSaveError error,
        out bool requiresConfirmation)
    {
        uri = null;
        requiresConfirmation = false;
        error = NoteApiConfigSaveError.None;

        if (string.IsNullOrWhiteSpace(endpoint))
        {
            error = NoteApiConfigSaveError.EmptyEndpoint;
            return false;
        }

        if (!Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out var parsed) ||
            (parsed.Scheme != Uri.UriSchemeHttps && parsed.Scheme != Uri.UriSchemeHttp))
        {
            error = NoteApiConfigSaveError.InvalidEndpoint;
            return false;
        }

        if (string.IsNullOrWhiteSpace(parsed.Host))
        {
            error = NoteApiConfigSaveError.InvalidEndpoint;
            return false;
        }

        uri = parsed;
        if (parsed.Scheme == Uri.UriSchemeHttp && !IsLocalHost(parsed.Host))
        {
            requiresConfirmation = true;
        }

        return true;
    }

    public static bool IsRemoteHttp(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttp && !IsLocalHost(uri.Host);
}
