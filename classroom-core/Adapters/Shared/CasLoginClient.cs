using System.Net;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ZJUSS.Core.Logging;
using ZJUSS.Core.Models;
using ZJUSS.Core.Security;

namespace ZJUSS.Core.Adapters.Shared;

internal enum CasLoginStatus
{
    Success,
    LoginFailed,
    VerificationRequired
}

internal sealed record AdapterHttpResponse(Uri Uri, HttpStatusCode StatusCode, string Body);

/// <summary>
/// 学校统一身份认证的只读客户端：手动跟随跳转、只在内存保存 Cookie、
/// 遇到验证码/二次验证立即返回，不发送任何数据读取之外的后续请求。
/// </summary>
internal sealed class CasLoginClient
{
    private const int MaxRedirects = 10;

    private static readonly SemaphoreSlim LoginGate = new(1, 1);
    private static readonly object LoginOrderLock = new();
    private static DateTimeOffset _lastLoginCompletedUtc;
    private static string? _lastLoginServiceAuthority;

    private readonly HttpClient _httpClient;
    private readonly Uri _serviceUri;
    private readonly Uri _loginEntryUri;
    private readonly CookieContainer _cookies = new();
    private readonly IAppLogger _logger;
    private readonly TimeSpan _crossServiceCooldown;

    public CasLoginClient(
        HttpClient httpClient,
        Uri serviceUri,
        IAppLogger? logger = null,
        Uri? loginEntryUri = null,
        TimeSpan? crossServiceCooldown = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _serviceUri = serviceUri ?? throw new ArgumentNullException(nameof(serviceUri));
        _loginEntryUri = loginEntryUri ?? serviceUri;
        _logger = logger ?? NullAppLogger.Instance;
        _crossServiceCooldown = crossServiceCooldown ?? TimeSpan.Zero;
        if (_crossServiceCooldown < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(crossServiceCooldown));
        }
    }

    public static async Task<(CasLoginClient Client, CasLoginStatus Status)> LoginWithRetryAsync(
        Func<CasLoginClient> clientFactory,
        SchoolCredentials credentials,
        IAppLogger logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(clientFactory);
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(logger);

        var client = clientFactory();
        var status = await client.LoginAsync(credentials, cancellationToken).ConfigureAwait(false);
        if (status != CasLoginStatus.LoginFailed)
        {
            return (client, status);
        }

        // CAS 偶尔拒绝同一账号的首次提交；换全新 Cookie 会话后只重试一次。间隔保持较短，
        // 避免在 5 秒启动窗口内把两个来源的读取预算耗光。
        logger.Info("CAS", "step=retry reason=loginRejected");
        await Task.Delay(TimeSpan.FromMilliseconds(900), cancellationToken).ConfigureAwait(false);
        client = clientFactory();
        status = await client.LoginAsync(credentials, cancellationToken).ConfigureAwait(false);
        return (client, status);
    }

    public async Task<CasLoginStatus> LoginAsync(
        SchoolCredentials credentials,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credentials);

        // 两个来源同时登录同一账号时，CAS 常拒绝其中一个；只串行化登录，不串行化数据读取。
        await LoginGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await WaitForCrossServiceCooldownAsync(cancellationToken).ConfigureAwait(false);
            return await LoginCoreAsync(credentials, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (LoginOrderLock)
            {
                _lastLoginCompletedUtc = DateTimeOffset.UtcNow;
                _lastLoginServiceAuthority = _serviceUri.GetLeftPart(UriPartial.Authority);
            }

            LoginGate.Release();
        }
    }

    private async Task WaitForCrossServiceCooldownAsync(CancellationToken cancellationToken)
    {
        if (_crossServiceCooldown <= TimeSpan.Zero)
        {
            return;
        }

        var authority = _serviceUri.GetLeftPart(UriPartial.Authority);
        TimeSpan remaining;
        lock (LoginOrderLock)
        {
            if (string.IsNullOrEmpty(_lastLoginServiceAuthority) ||
                string.Equals(_lastLoginServiceAuthority, authority, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            remaining = _crossServiceCooldown - (DateTimeOffset.UtcNow - _lastLoginCompletedUtc);
        }

        if (remaining > TimeSpan.Zero)
        {
            _logger.Info("CAS", $"step=cooldown waitMs={(int)remaining.TotalMilliseconds}");
            await Task.Delay(remaining, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<CasLoginStatus> LoginCoreAsync(
        SchoolCredentials credentials,
        CancellationToken cancellationToken)
    {
        using var initialRequest = new HttpRequestMessage(HttpMethod.Get, _loginEntryUri);
        var initial = await SendCoreAsync(initialRequest, cancellationToken).ConfigureAwait(false);
        EnsureLoginTransportStatus(initial, "Login");
        _logger.Info("CAS", $"step=initial status={(int)initial.StatusCode} host={initial.Uri.Host}");

        if (IsCasLoginUri(initial.Uri) || HtmlScanner.LooksLikeLoginPage(initial.Body))
        {
            if (!IsCasLoginUri(initial.Uri))
            {
                // 目标站点自己返回登录表单，但没有进入 CAS，无法在不绕过的情况下继续。
                return HtmlScanner.HasVerificationSignal(initial.Body, out _)
                    ? CasLoginStatus.VerificationRequired
                    : CasLoginStatus.LoginFailed;
            }

            // authcode 输入框在浙大 CAS 页中始终存在，但通常由 getKaptchaStatus 隐藏。
            // 只有显式验证码输入（如 captcha）或服务端开启验证码时才停止；不尝试绕过。
            if (HtmlScanner.HasVerificationSignal(initial.Body, out var signal))
            {
                if (!IsOptionalAuthCodeSignal(signal))
                {
                    return CasLoginStatus.VerificationRequired;
                }

                var kaptchaRequired = await GetKaptchaStatusAsync(initial.Uri, cancellationToken)
                    .ConfigureAwait(false);
                if (kaptchaRequired == true ||
                    (kaptchaRequired is null && HtmlScanner.HasExplicitCaptchaPrompt(initial.Body)))
                {
                    return CasLoginStatus.VerificationRequired;
                }
            }

            return await SubmitCredentialsAsync(initial.Uri, initial.Body, credentials, cancellationToken)
                .ConfigureAwait(false);
        }

        if (IsTargetUri(initial.Uri))
        {
            return HasTargetCookies() ? CasLoginStatus.Success : CasLoginStatus.LoginFailed;
        }

        throw new AdapterFailureException(
            SourceFailureReason.SchemaChanged,
            "Login",
            "登录后跳转到了无法识别的页面。");
    }

    internal async Task<string> SendReadAsync(
        HttpRequestMessage request,
        string phase,
        CancellationToken cancellationToken)
    {
        var response = await SendCoreAsync(request, cancellationToken).ConfigureAwait(false);
        _logger.Info(
            "HTTP",
            $"phase={phase} status={(int)response.StatusCode} host={response.Uri.Host} path={response.Uri.AbsolutePath}");
        if ((int)response.StatusCode >= 500)
        {
            throw new AdapterFailureException(
                SourceFailureReason.Maintenance,
                phase,
                "学校网站暂时无法访问。");
        }

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new AdapterFailureException(
                SourceFailureReason.LoginFailed,
                phase,
                "读取页面时登录状态失效。");
        }

        if ((int)response.StatusCode is < 200 or >= 300)
        {
            throw new AdapterFailureException(
                SourceFailureReason.SchemaChanged,
                phase,
                "读取页面返回了非预期状态。");
        }

        if (IsCasLoginUri(response.Uri) || HtmlScanner.LooksLikeLoginPage(response.Body))
        {
            throw new AdapterFailureException(
                SourceFailureReason.LoginFailed,
                phase,
                "读取页面时被重定向到登录页。");
        }

        return response.Body;
    }

    internal async Task<AdapterHttpResponse> SendRawAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
        => await SendCoreAsync(request, cancellationToken).ConfigureAwait(false);

    /// <summary>下载二进制内容（课件图片）时使用同一 Cookie 容器；不按文本解析响应。</summary>
    internal async Task<(HttpStatusCode Status, byte[] Body)> SendBytesAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (request.RequestUri is null)
        {
            throw new AdapterFailureException(
                SourceFailureReason.SchemaChanged,
                "Http",
                "请求缺少地址。");
        }

        var method = request.Method;
        var uri = request.RequestUri;
        for (var redirect = 0; redirect <= MaxRedirects; redirect++)
        {
            using var attempt = new HttpRequestMessage(method, uri);
            foreach (var header in request.Headers)
            {
                attempt.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            AddCookies(attempt);
            using var response = await _httpClient
                .SendAsync(attempt, HttpCompletionOption.ResponseContentRead, cancellationToken)
                .ConfigureAwait(false);
            CaptureCookies(uri, response);

            if (!IsRedirect(response.StatusCode))
            {
                var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                return (response.StatusCode, bytes);
            }

            var location = response.Headers.Location;
            if (location is null)
            {
                throw new AdapterFailureException(
                    SourceFailureReason.SchemaChanged,
                    "Http",
                    "跳转响应缺少目标地址。");
            }

            var next = location.IsAbsoluteUri ? location : new Uri(uri, location);
            if (!next.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
                !next.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                throw new AdapterFailureException(
                    SourceFailureReason.SchemaChanged,
                    "Http",
                    "跳转目标不是网页地址。");
            }

            if (response.StatusCode is HttpStatusCode.SeeOther ||
                ((response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found) &&
                 method == HttpMethod.Post))
            {
                method = HttpMethod.Get;
            }

            uri = next;
        }

        throw new AdapterFailureException(
            SourceFailureReason.Unreachable,
            "Http",
            "跳转次数过多。");
    }

    /// <summary>
    /// 读取当前会话中某个主机的 Cookie 值（例如智云课堂的 _token）。
    /// 只允许内部采集代码使用；值不得写入日志、学习数据或备份。
    /// </summary>
    internal string? GetCookieValue(Uri uri, string name)
    {
        var cookie = _cookies.GetCookies(uri)[name];
        return cookie?.Value;
    }

    internal static bool IsCasLoginUri(Uri uri)
    {
        if (uri.Host.Equals("zjuam.zju.edu.cn", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.Equals("identity.zju.edu.cn", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.Equals("cas.zju.edu.cn", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var path = uri.AbsolutePath.TrimEnd('/');
        return path.EndsWith("/cas/login", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith("/cas/login.html", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<CasLoginStatus> SubmitCredentialsAsync(
        Uri casUri,
        string loginHtml,
        SchoolCredentials credentials,
        CancellationToken cancellationToken)
    {
        var form = HtmlScanner.ParseLoginForm(loginHtml, casUri);
        _logger.Info(
            "CAS",
            $"step=form parsed={(form is null ? 0 : 1)} execution={(string.IsNullOrWhiteSpace(form?.Execution) ? 0 : 1)} inputs={(form?.Inputs.Count ?? 0)}");
        if (form is null || string.IsNullOrWhiteSpace(form.Execution))
        {
            throw new AdapterFailureException(
                SourceFailureReason.SchemaChanged,
                "Login",
                "统一身份认证登录页缺少必要的登录参数。");
        }

        var pubKeyUri = new Uri(
            casUri.GetLeftPart(UriPartial.Authority) + "/cas/v2/getPubKey",
            UriKind.Absolute);
        using var keyRequest = new HttpRequestMessage(HttpMethod.Get, pubKeyUri);
        keyRequest.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
        var keyResponse = await SendCoreAsync(keyRequest, cancellationToken).ConfigureAwait(false);
        _logger.Info("CAS", $"step=pubKey status={(int)keyResponse.StatusCode} length={keyResponse.Body.Length}");
        EnsureLoginTransportStatus(keyResponse, "Login");

        string encryptedPassword;
        try
        {
            encryptedPassword = EncryptPassword(credentials.Password, keyResponse.Body);
        }
        catch (JsonException exception)
        {
            _logger.Info("CAS", "step=encrypt error=Json");
            throw new AdapterFailureException(
                SourceFailureReason.SchemaChanged,
                "Login",
                "无法解析统一身份认证公钥。", exception);
        }
        catch (FormatException exception)
        {
            _logger.Info("CAS", "step=encrypt error=Format");
            throw new AdapterFailureException(
                SourceFailureReason.SchemaChanged,
                "Login",
                "统一身份认证公钥格式异常。", exception);
        }
        catch (CryptographicException exception)
        {
            _logger.Info("CAS", "step=encrypt error=Crypto");
            throw new AdapterFailureException(
                SourceFailureReason.SchemaChanged,
                "Login",
                "无法使用统一身份认证公钥。", exception);
        }
        catch (Exception exception) when (exception is NotSupportedException or OverflowException)
        {
            _logger.Info("CAS", $"step=encrypt error={exception.GetType().Name}");
            throw new AdapterFailureException(
                SourceFailureReason.SchemaChanged,
                "Login",
                "无法使用统一身份认证公钥。", exception);
        }

        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var input in form.Inputs)
        {
            if (string.IsNullOrWhiteSpace(input.Name) ||
                input.Name.Equals("username", StringComparison.OrdinalIgnoreCase) ||
                input.Name.Equals("password", StringComparison.OrdinalIgnoreCase) ||
                input.Name.Equals("rememberMe", StringComparison.OrdinalIgnoreCase) ||
                input.Name.Equals("authcode", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (input.IsHidden || input.Name.Equals("_eventId", StringComparison.OrdinalIgnoreCase))
            {
                fields[input.Name] = input.Value;
            }
        }

        fields["username"] = credentials.Account;
        fields["password"] = encryptedPassword;
        fields["execution"] = form.Execution;
        fields["_eventId"] = "submit";
        fields["geolocation"] = string.Empty;
        fields["authcode"] = string.Empty;

        using var submitRequest = new HttpRequestMessage(HttpMethod.Post, form.Action)
        {
            Content = new FormUrlEncodedContent(fields)
        };
        submitRequest.Headers.Referrer = casUri;
        submitRequest.Headers.TryAddWithoutValidation("Origin", casUri.GetLeftPart(UriPartial.Authority));
        _logger.Info("CAS", $"step=submit host={form.Action.Host} hiddenFields={fields.Count}");
        var submitted = await SendCoreAsync(submitRequest, cancellationToken).ConfigureAwait(false);
        EnsureLoginTransportStatus(submitted, "Login");
        _logger.Info("CAS", $"step=submitted status={(int)submitted.StatusCode} host={submitted.Uri.Host}");

        if (HtmlScanner.HasVerificationSignal(submitted.Body, out var submittedSignal))
        {
            // 登录后的页面若切换到显式验证码，按验证要求处理；普通失败页仍归为登录失败。
            if (!IsOptionalAuthCodeSignal(submittedSignal))
            {
                return CasLoginStatus.VerificationRequired;
            }
        }

        if (IsCasLoginUri(submitted.Uri) || HtmlScanner.LooksLikeLoginPage(submitted.Body))
        {
            _logger.Info(
                "CAS",
                $"step=loginRejected reason={HtmlScanner.GetLoginErrorCode(submitted.Body) ?? "unknown"}");
            return CasLoginStatus.LoginFailed;
        }

        if (IsTargetUri(submitted.Uri))
        {
            var success = HasTargetCookies();
            _logger.Info("CAS", $"step=target cookies={(success ? 1 : 0)}");
            return success ? CasLoginStatus.Success : CasLoginStatus.LoginFailed;
        }

        return CasLoginStatus.LoginFailed;
    }

    private static bool IsOptionalAuthCodeSignal(string? signal) =>
        signal is not null &&
        (signal.Equals("authcode", StringComparison.OrdinalIgnoreCase) ||
         signal.Equals("code", StringComparison.OrdinalIgnoreCase));

    /// <summary>读取官方验证码开关；端点不可用时返回 null，不用静态字段猜测验证码要求。</summary>
    private async Task<bool?> GetKaptchaStatusAsync(Uri casUri, CancellationToken cancellationToken)
    {
        try
        {
            var uri = new Uri(
                casUri.GetLeftPart(UriPartial.Authority) + "/cas/v2/getKaptchaStatus",
                UriKind.Absolute);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
            var response = await SendCoreAsync(request, cancellationToken).ConfigureAwait(false);
            if ((int)response.StatusCode is < 200 or >= 300)
            {
                _logger.Info("CAS", $"step=kaptchaStatus status={(int)response.StatusCode}");
                return null;
            }

            var body = response.Body.Trim();
            if (bool.TryParse(body.Trim('"'), out var boolean))
            {
                _logger.Info("CAS", $"step=kaptchaStatus required={(boolean ? 1 : 0)}");
                return boolean;
            }

            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.True || root.ValueKind == JsonValueKind.False)
            {
                var result = root.GetBoolean();
                _logger.Info("CAS", $"step=kaptchaStatus required={(result ? 1 : 0)}");
                return result;
            }

            if (root.ValueKind == JsonValueKind.Object &&
                TryGetBool(root, "data", out var dataValue))
            {
                _logger.Info("CAS", $"step=kaptchaStatus required={(dataValue ? 1 : 0)}");
                return dataValue;
            }

            _logger.Info("CAS", "step=kaptchaStatus unknown");
            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or FormatException)
        {
            _logger.Info("CAS", $"step=kaptchaStatus error={exception.GetType().Name}");
            return null;
        }
    }

    private static bool TryGetBool(JsonElement element, string name, out bool value)
    {
        value = false;
        if (!element.TryGetProperty(name, out var property))
        {
            return false;
        }

        if (property.ValueKind == JsonValueKind.True || property.ValueKind == JsonValueKind.False)
        {
            value = property.GetBoolean();
            return true;
        }

        return property.ValueKind == JsonValueKind.String &&
               bool.TryParse(property.GetString(), out value);
    }

    private static void EnsureLoginTransportStatus(AdapterHttpResponse response, string phase)
    {
        if ((int)response.StatusCode >= 500)
        {
            throw new AdapterFailureException(
                SourceFailureReason.Maintenance,
                phase,
                "学校网站暂时无法访问。");
        }

        if (!IsSuccess(response.StatusCode))
        {
            throw new AdapterFailureException(
                SourceFailureReason.SchemaChanged,
                phase,
                "统一身份认证返回了非预期状态。");
        }
    }

    private async Task<AdapterHttpResponse> SendCoreAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (request.RequestUri is null)
        {
            throw new AdapterFailureException(
                SourceFailureReason.SchemaChanged,
                "Http",
                "请求缺少地址。");
        }

        var method = request.Method;
        var uri = request.RequestUri;
        byte[]? body = null;
        if (request.Content is not null)
        {
            body = await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        }

        var contentHeaders = request.Content?.Headers;
        for (var redirect = 0; redirect <= MaxRedirects; redirect++)
        {
            using var attempt = new HttpRequestMessage(method, uri);
            if (body is not null)
            {
                var content = new ByteArrayContent(body);
                if (contentHeaders is not null)
                {
                    foreach (var header in contentHeaders)
                    {
                        content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                    }
                }

                attempt.Content = content;
            }

            foreach (var header in request.Headers)
            {
                attempt.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            AddCookies(attempt);

            using var response = await _httpClient
                .SendAsync(attempt, HttpCompletionOption.ResponseContentRead, cancellationToken)
                .ConfigureAwait(false);
            CaptureCookies(uri, response);

            if (!IsRedirect(response.StatusCode))
            {
                var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                return new AdapterHttpResponse(uri, response.StatusCode, text);
            }

            var location = response.Headers.Location;
            if (location is null)
            {
                throw new AdapterFailureException(
                    SourceFailureReason.SchemaChanged,
                    "Http",
                    "跳转响应缺少目标地址。");
            }

            var next = location.IsAbsoluteUri ? location : new Uri(uri, location);
            if (!next.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
                !next.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                throw new AdapterFailureException(
                    SourceFailureReason.SchemaChanged,
                    "Http",
                    "跳转目标不是网页地址。");
            }

            if (response.StatusCode is HttpStatusCode.SeeOther ||
                ((response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found) &&
                 method == HttpMethod.Post))
            {
                method = HttpMethod.Get;
                body = null;
                contentHeaders = null;
            }

            uri = next;
        }

        throw new AdapterFailureException(
            SourceFailureReason.SchemaChanged,
            "Http",
            "跳转次数超过限制。");
    }

    private void AddCookies(HttpRequestMessage request)
    {
        if (request.RequestUri is null)
        {
            return;
        }

        var header = _cookies.GetCookieHeader(request.RequestUri);
        if (!string.IsNullOrEmpty(header))
        {
            request.Headers.TryAddWithoutValidation("Cookie", header);
        }
    }

    private void CaptureCookies(Uri requestUri, HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var values))
        {
            return;
        }

        foreach (var value in values)
        {
            try
            {
                _cookies.SetCookies(requestUri, value);
            }
            catch (CookieException)
            {
                // 单个格式异常的 Cookie 不应中止整次只读同步。
            }
        }
    }

    private bool HasTargetCookies() => _cookies.GetCookies(_serviceUri).Count > 0;

    private bool IsTargetUri(Uri uri)
        => uri.Host.Equals(_serviceUri.Host, StringComparison.OrdinalIgnoreCase);

    private static bool IsRedirect(HttpStatusCode statusCode)
        => statusCode is HttpStatusCode.MovedPermanently
            or HttpStatusCode.Redirect
            or HttpStatusCode.RedirectMethod
            or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect;

    private static bool IsSuccess(HttpStatusCode statusCode) => (int)statusCode is >= 200 and < 300;

    private static string EncryptPassword(string password, string pubKeyJson)
    {
        using var document = JsonDocument.Parse(pubKeyJson);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException("PubKey root is not an object.");
        }

        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
        {
            root = data;
        }

        if (!TryGetString(root, "modulus", out var modulusHex) ||
            !TryGetString(root, "exponent", out var exponentHex))
        {
            throw new FormatException("PubKey is missing modulus or exponent.");
        }

        var modulus = ToUnsignedBigInteger(HexToBytes(modulusHex));
        var exponent = ToUnsignedBigInteger(HexToBytes(exponentHex));
        var modulusDigits = (modulus.GetBitLength() + 15) / 16;
        var chunkSize = checked((int)(2 * (modulusDigits - 1)));
        if (chunkSize <= 0)
        {
            throw new FormatException("RSA modulus is too small.");
        }

        // 浙大 CAS 的 login.js 先执行 split("").reverse()，再按 UTF-16
        // 字符码分块做裸 RSA（无 PKCS#1 填充），输出小写十六进制块。
        // 直接使用标准 RSA/PKCS#1 + Base64 会被服务端拒绝。
        var reversedPassword = new string(password.Reverse().ToArray());
        // security.js 的 chunkSize 是“字符数”：每个 16 位数字由两个字符组成
        // （低字节 = 第一个字符，高字节 = 第二个字符），不是一字符一数字。
        var charsPerBlock = chunkSize;
        var blocks = new List<string>();
        for (var offset = 0; offset < reversedPassword.Length; offset += charsPerBlock)
        {
            var bytes = new byte[charsPerBlock];
            for (var digitIndex = 0; digitIndex < charsPerBlock / 2; digitIndex++)
            {
                var lowIndex = offset + digitIndex * 2;
                var highIndex = lowIndex + 1;
                var low = lowIndex < reversedPassword.Length ? reversedPassword[lowIndex] : '\0';
                var high = highIndex < reversedPassword.Length ? reversedPassword[highIndex] : '\0';
                bytes[digitIndex * 2] = (byte)(low & 0xFF);
                bytes[digitIndex * 2 + 1] = (byte)(high & 0xFF);
            }

            var block = new BigInteger(bytes, isUnsigned: true, isBigEndian: false);
            var encrypted = BigInteger.ModPow(block, exponent, modulus);
            var blockHex = encrypted.ToString("x", System.Globalization.CultureInfo.InvariantCulture);
            blocks.Add(blockHex.PadLeft(checked((int)(modulusDigits * 4)), '0'));
        }

        return string.Join(' ', blocks);
    }

    private static BigInteger ToUnsignedBigInteger(byte[] bigEndianBytes)
    {
        var trimmed = bigEndianBytes.SkipWhile(value => value == 0).ToArray();
        if (trimmed.Length == 0)
        {
            return BigInteger.Zero;
        }

        return new BigInteger(trimmed, isUnsigned: true, isBigEndian: true);
    }

    private static bool TryGetString(JsonElement element, string name, out string value)
    {
        if (element.TryGetProperty(name, out var property) &&
            property.ValueKind == JsonValueKind.String)
        {
            value = property.GetString() ?? string.Empty;
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static byte[] HexToBytes(string hex)
    {
        var trimmed = hex.Trim();
        if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[2..];
        }

        var builder = new StringBuilder(trimmed.Length);
        foreach (var character in trimmed)
        {
            if (!char.IsWhiteSpace(character))
            {
                builder.Append(character);
            }
        }

        var normalized = builder.ToString();
        if (normalized.Length == 0 || !normalized.All(Uri.IsHexDigit))
        {
            throw new FormatException("Invalid hexadecimal key material.");
        }

        // 学校公钥的 exponent 常见为 "10001"（奇数长度），左侧补 0。
        if (normalized.Length % 2 != 0)
        {
            normalized = "0" + normalized;
        }

        return Convert.FromHexString(normalized);
    }
}
