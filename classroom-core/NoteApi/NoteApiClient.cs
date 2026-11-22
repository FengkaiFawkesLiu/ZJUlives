using System.Net.Http.Headers;
using System.Text.Json;
using ZJUSS.Core.Logging;

namespace ZJUSS.Core.NoteApi;

/// <summary>
/// OpenAI 兼容文本 API 客户端。只发送所选课节的文字和元数据，不读取文件、
/// 不自动重试、不 fallback，也不记录密钥、请求体、完整地址或课堂文字。
/// </summary>
public sealed class NoteApiClient : INoteApiClient, IDisposable
{
    private const string ModuleName = "NoteApi";
    private const string InvalidConfigurationDetail = "笔记 API 配置不完整或接口地址无效。";
    private const string RemoteHttpNotConfirmedDetail = "远程 HTTP 地址尚未确认风险，未发送请求。";
    private const string NoMaterialDetail = "本节没有可用于生成笔记的材料。";
    private const string MaterialTooLongDetail = "输入材料超过长度限制，未发送请求。";
    private const string ResponseTooLargeDetail = "响应过大，超过允许读取的字节数。";
    private const string TimeoutDetail = "等待笔记 API 响应超时。";
    private const string CancelledDetail = "本次生成已取消。";
    private const string NetworkDetail = "无法连接笔记 API。";

    private readonly HttpClient _httpClient;
    private readonly NoteApiOptions _options;
    private readonly IAppLogger _logger;

    public NoteApiClient(
        HttpMessageHandler handler,
        NoteApiOptions? options = null,
        IAppLogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(handler);

        _options = options ?? new NoteApiOptions();
        if (_options.MaxInputCharacters <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MaxInputCharacters must be positive.");
        }

        if (_options.MaxResponseBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MaxResponseBytes must be positive.");
        }

        if (_options.MaxCompletionTokens <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MaxCompletionTokens must be positive.");
        }

        if (_options.RequestTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "RequestTimeout must be positive.");
        }

        _httpClient = new HttpClient(handler, disposeHandler: false)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        _logger = logger ?? NullAppLogger.Instance;
    }

    public async Task<NoteApiResult> GenerateAsync(
        NoteApiConfigSnapshot config,
        NoteApiRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!TryValidateConfiguration(
                config,
                out var endpoint,
                out var configFailure,
                out var configDetail))
        {
            return NoteApiResult.Fail(configFailure, configDetail);
        }

        if (request is null || !request.HasMaterial)
        {
            return NoteApiResult.Fail(NoteApiFailureKind.NoMaterial, NoMaterialDetail);
        }

        if (NoteApiRequestBuilder.CountInputCharacters(request) > _options.MaxInputCharacters)
        {
            return NoteApiResult.Fail(NoteApiFailureKind.MaterialTooLong, MaterialTooLongDetail);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return NoteApiResult.Fail(NoteApiFailureKind.Cancelled, CancelledDetail);
        }

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new ByteArrayContent(
                NoteApiRequestBuilder.BuildRequestBody(config.Model, request, _options))
        };
        httpRequest.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json")
        {
            CharSet = "utf-8"
        };
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        try
        {
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.ApiKey);
        }
        catch (FormatException)
        {
            return NoteApiResult.Fail(NoteApiFailureKind.InvalidConfiguration, InvalidConfigurationDetail);
        }

        using var timeoutCts = new CancellationTokenSource();
        timeoutCts.CancelAfter(_options.RequestTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutCts.Token);

        HttpResponseMessage? response = null;
        try
        {
            response = await _httpClient
                .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, linkedCts.Token)
                .ConfigureAwait(false);

            var statusCode = (int)response.StatusCode;
            var body = await ReadBodyAsync(response, linkedCts.Token).ConfigureAwait(false);
            if (body.TooLarge)
            {
                LogFailure(NoteApiFailureKind.ResponseUnreadable, statusCode);
                return NoteApiResult.Fail(
                    NoteApiFailureKind.ResponseUnreadable,
                    ResponseTooLargeDetail,
                    statusCode);
            }

            if (!response.IsSuccessStatusCode)
            {
                var failure = MapStatus(statusCode, TryReadErrorType(body.Bytes));
                LogFailure(failure, statusCode);
                return NoteApiResult.Fail(failure, StatusDetail(failure, statusCode), statusCode);
            }

            // 个别兼容服务会用 200 返回错误对象；额度不足仍按失败分类。
            if (string.Equals(
                    TryReadErrorType(body.Bytes),
                    "insufficient_balance_error",
                    StringComparison.OrdinalIgnoreCase))
            {
                LogFailure(NoteApiFailureKind.InsufficientBalance, statusCode);
                return NoteApiResult.Fail(
                    NoteApiFailureKind.InsufficientBalance,
                    StatusDetail(NoteApiFailureKind.InsufficientBalance, statusCode),
                    statusCode);
            }

            var parsed = NoteApiResponseParser.Parse(body.Bytes);
            if (!parsed.Success)
            {
                LogFailure(parsed.Failure, statusCode);
                return NoteApiResult.Fail(parsed.Failure, parsed.Detail, statusCode);
            }

            _logger.Info(ModuleName, "Note API request completed.");
            return new NoteApiResult
            {
                Success = true,
                Content = parsed.Content,
                Failure = NoteApiFailureKind.None,
                HttpStatusCode = statusCode
            };
        }
        catch (OperationCanceledException)
        {
            var cancelled = cancellationToken.IsCancellationRequested;
            var failure = cancelled ? NoteApiFailureKind.Cancelled : NoteApiFailureKind.Timeout;
            LogFailure(failure, null);
            return NoteApiResult.Fail(failure, cancelled ? CancelledDetail : TimeoutDetail);
        }
        catch (HttpRequestException)
        {
            LogFailure(NoteApiFailureKind.NetworkUnreachable, null);
            return NoteApiResult.Fail(NoteApiFailureKind.NetworkUnreachable, NetworkDetail);
        }
        catch (IOException)
        {
            LogFailure(NoteApiFailureKind.NetworkUnreachable, null);
            return NoteApiResult.Fail(NoteApiFailureKind.NetworkUnreachable, NetworkDetail);
        }
        finally
        {
            response?.Dispose();
        }
    }

    public void Dispose() => _httpClient.Dispose();

    private static bool TryValidateConfiguration(
        NoteApiConfigSnapshot? config,
        out Uri endpoint,
        out NoteApiFailureKind failure,
        out string detail)
    {
        endpoint = null!;
        failure = NoteApiFailureKind.InvalidConfiguration;
        detail = InvalidConfigurationDetail;

        if (config is null || !config.HasEndpoint || !config.HasApiKey || !config.HasModel)
        {
            return false;
        }

        if (!NoteApiEndpointPolicy.TryValidate(
                config.Endpoint,
                out var parsed,
                out _,
                out var requiresConfirmation) ||
            parsed is null)
        {
            return false;
        }

        if (requiresConfirmation && !config.RemoteHttpConfirmed)
        {
            failure = NoteApiFailureKind.RemoteHttpNotConfirmed;
            detail = RemoteHttpNotConfirmedDetail;
            return false;
        }

        endpoint = parsed;
        failure = NoteApiFailureKind.None;
        detail = string.Empty;
        return true;
    }

    private async Task<(byte[] Bytes, bool TooLarge)> ReadBodyAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.Content is null)
        {
            return (Array.Empty<byte>(), false);
        }

        var contentLength = response.Content.Headers.ContentLength;
        if (contentLength.HasValue && contentLength.Value > _options.MaxResponseBytes)
        {
            return (Array.Empty<byte>(), true);
        }

        await using var stream = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (buffer.Length + read > _options.MaxResponseBytes)
            {
                return (Array.Empty<byte>(), true);
            }

            buffer.Write(chunk, 0, read);
        }

        return (buffer.ToArray(), false);
    }

    private static NoteApiFailureKind MapStatus(int statusCode, string? errorType)
    {
        if (statusCode is 401 or 403)
        {
            return NoteApiFailureKind.AuthenticationFailed;
        }

        if (statusCode == 402 ||
            string.Equals(errorType, "insufficient_balance_error", StringComparison.OrdinalIgnoreCase))
        {
            return NoteApiFailureKind.InsufficientBalance;
        }

        if (statusCode == 429)
        {
            return NoteApiFailureKind.RateLimited;
        }

        // 5xx 以及其他非成功状态码都归为服务端错误，并保留状态码。
        return NoteApiFailureKind.ServerError;
    }

    private static string? TryReadErrorType(byte[] body)
    {
        if (body.Length == 0)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("error", out var error) ||
                error.ValueKind != JsonValueKind.Object ||
                !error.TryGetProperty("type", out var type) ||
                type.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            return type.GetString();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string StatusDetail(NoteApiFailureKind failure, int statusCode) => failure switch
    {
        NoteApiFailureKind.AuthenticationFailed => "笔记 API 认证失败，未生成笔记。",
        NoteApiFailureKind.InsufficientBalance => "笔记 API 账户余额不足，未生成笔记。",
        NoteApiFailureKind.RateLimited => "笔记 API 请求过于频繁，未生成笔记。",
        _ => $"笔记 API 服务返回 HTTP {statusCode}。"
    };

    private void LogFailure(NoteApiFailureKind failure, int? statusCode)
    {
        var suffix = statusCode.HasValue ? $" (HTTP {statusCode.Value})" : string.Empty;
        _logger.Warn(ModuleName, $"Note API request failed: {failure}{suffix}.");
    }
}
