using System.Net;
using System.Text.RegularExpressions;

namespace ZJUSS.Core.Adapters.Shared;

internal sealed record HtmlInput(string Name, string Type, string Value)
{
    public bool IsHidden => Type.Equals("hidden", StringComparison.OrdinalIgnoreCase);
}

internal sealed record HtmlLoginForm(
    Uri Action,
    string Method,
    IReadOnlyList<HtmlInput> Inputs,
    string? Execution);

/// <summary>只依赖正则的小型 HTML 读取器；不执行脚本、不引入第三方解析包。</summary>
internal static class HtmlScanner
{
    private const string AttributePattern =
        """(?<name>[A-Za-z_:][-A-Za-z0-9_:.]*)\s*=\s*(?:(?<q>["'])(?<value>.*?)\k<q>|(?<value>[^\s"'=<>`]+))""";

    // 属性部分允许引号内出现 '>'，避免 title="a>b" 截断标签。
    private const string AttributeChunk = """(?:[^>"']|"[^"]*"|'[^']*')*""";

    private static readonly Regex InputTagRegex = new(
        $"""<input\b(?<attrs>{AttributeChunk})>""",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex FormRegex = new(
        $"""<form\b(?<attrs>{AttributeChunk})>(?<body>.*?)</form>""",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex AttributeRegex = new(
        AttributePattern,
        RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex ScriptStyleRegex = new(
        """<(script|style)\b[^>]*>.*?</\1>""",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex TagRegex = new(
        """<[^>]+>""",
        RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex WhitespaceRegex = new(
        """\s+""",
        RegexOptions.Compiled);

    private static readonly string[] VerificationInputNames =
    [
        "authcode",
        "captcha",
        "verifycode",
        "vcode",
        "code",
        "otp",
        "smscode"
    ];

    private static readonly string[] VerificationTextSignals =
    [
        "验证码",
        "二次验证",
        "双因素",
        "动态码",
        "短信验证",
        "captcha",
        "two-factor",
        "2fa",
        "verification code"
    ];

    public static IReadOnlyList<HtmlInput> ParseInputs(string html)
    {
        var inputs = new List<HtmlInput>();
        foreach (Match match in InputTagRegex.Matches(html))
        {
            var attributes = ParseAttributes(match.Groups["attrs"].Value);
            var name = GetAttribute(attributes, "name");
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var type = GetAttribute(attributes, "type") ?? "text";
            var value = GetAttribute(attributes, "value") ?? string.Empty;
            inputs.Add(new HtmlInput(name.Trim(), type.Trim(), value));
        }

        return inputs;
    }

    public static HtmlLoginForm? ParseLoginForm(string html, Uri baseUri)
    {
        HtmlLoginForm? firstForm = null;
        foreach (Match match in FormRegex.Matches(html))
        {
            var attributes = ParseAttributes(match.Groups["attrs"].Value);
            var inputs = ParseInputs(match.Groups["body"].Value);
            var hasUsername = inputs.Any(input =>
                input.Name.Equals("username", StringComparison.OrdinalIgnoreCase) ||
                input.Name.Equals("account", StringComparison.OrdinalIgnoreCase));
            var hasPassword = inputs.Any(input =>
                input.Name.Equals("password", StringComparison.OrdinalIgnoreCase));
            var hasExecution = inputs.Any(input =>
                input.Name.Equals("execution", StringComparison.OrdinalIgnoreCase));

            var method = GetAttribute(attributes, "method")?.Trim() ?? "post";
            var actionText = GetAttribute(attributes, "action");
            Uri action;
            if (string.IsNullOrWhiteSpace(actionText) ||
                !Uri.TryCreate(baseUri, actionText.Trim(), out action!))
            {
                action = baseUri;
            }

            var execution = inputs
                .FirstOrDefault(input => input.Name.Equals("execution", StringComparison.OrdinalIgnoreCase))
                ?.Value;

            var form = new HtmlLoginForm(action, method, inputs, execution);
            if (hasUsername && hasPassword)
            {
                return form;
            }

            if (firstForm is null && (hasPassword || hasExecution))
            {
                firstForm = form;
            }
        }

        return firstForm;
    }

    public static bool LooksLikeLoginPage(string html)
    {
        var inputs = ParseInputs(html);
        var hasUsername = inputs.Any(input =>
            input.Name.Equals("username", StringComparison.OrdinalIgnoreCase) ||
            input.Name.Equals("account", StringComparison.OrdinalIgnoreCase));
        var hasPassword = inputs.Any(input =>
            input.Name.Equals("password", StringComparison.OrdinalIgnoreCase));
        var hasExecution = inputs.Any(input =>
            input.Name.Equals("execution", StringComparison.OrdinalIgnoreCase));
        return (hasUsername && hasPassword) || (hasPassword && hasExecution);
    }

    /// <summary>只根据登录表单中的可见输入或页面文字判断验证要求，不尝试作答或绕过。</summary>
    public static bool HasVerificationSignal(string html, out string? signal)
    {
        var inputs = ParseInputs(html);
        foreach (var input in inputs)
        {
            if (!VerificationInputNames.Contains(input.Name, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            // CAS 登录页可能带有空的隐藏 authcode 字段，这不是验证要求。
            if (input.IsHidden &&
                string.IsNullOrWhiteSpace(input.Value) &&
                !input.Name.Equals("captcha", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            signal = input.Name;
            return true;
        }

        var text = GetVisibleText(html);
        foreach (var keyword in VerificationTextSignals)
        {
            if (text.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                signal = keyword;
                return true;
            }
        }

        signal = null;
        return false;
    }

    /// <summary>只返回登录页错误类别，不返回页面正文或账号信息。</summary>
    public static string? GetLoginErrorCode(string html)
    {
        var match = Regex.Match(
            html,
            """<[^>]*id=["']errormsg["'][^>]*>(?<text>.*?)</""",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (!match.Success)
        {
            return null;
        }

        var text = GetVisibleText(match.Groups["text"].Value);
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (text.Contains("验证码", StringComparison.OrdinalIgnoreCase))
        {
            return "verification";
        }

        if (text.Contains("锁定", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("限制", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("冻结", StringComparison.OrdinalIgnoreCase))
        {
            return "locked";
        }

        if (text.Contains("密码", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("账号", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("用户名", StringComparison.OrdinalIgnoreCase))
        {
            return "credentials";
        }

        if (text.Contains("维护", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("繁忙", StringComparison.OrdinalIgnoreCase))
        {
            return "maintenance";
        }

        return "other";
    }

    public static bool HasExplicitCaptchaPrompt(string html)
    {
        var text = GetVisibleText(html);
        return text.Contains("请输入验证码", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("验证码后继续", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("请输入动态码", StringComparison.OrdinalIgnoreCase);
    }

    public static string GetVisibleText(string html)
    {
        var withoutScripts = ScriptStyleRegex.Replace(html, " ");
        var withoutTags = TagRegex.Replace(withoutScripts, " ");
        var decoded = WebUtility.HtmlDecode(withoutTags);
        return WhitespaceRegex.Replace(decoded, " ").Trim();
    }

    public static string? GetAttribute(IReadOnlyDictionary<string, string> attributes, string name)
        => attributes.TryGetValue(name, out var value) ? value : null;

    public static Dictionary<string, string> ParseAttributes(string attributeText)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in AttributeRegex.Matches(attributeText))
        {
            var name = match.Groups["name"].Value;
            var value = match.Groups["value"].Value;
            result[name] = WebUtility.HtmlDecode(value);
        }

        return result;
    }
}
