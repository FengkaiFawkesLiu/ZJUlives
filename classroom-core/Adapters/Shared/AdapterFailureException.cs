using ZJUSS.Core.Models;

namespace ZJUSS.Core.Adapters.Shared;

/// <summary>适配器内部使用的一次性失败；消息只包含阶段名，不包含网页或凭据内容。</summary>
internal sealed class AdapterFailureException : Exception
{
    public AdapterFailureException(SourceFailureReason reason, string phase, string message)
        : base(message)
    {
        Reason = reason;
        Phase = phase;
    }

    public AdapterFailureException(
        SourceFailureReason reason,
        string phase,
        string message,
        Exception innerException)
        : base(message, innerException)
    {
        Reason = reason;
        Phase = phase;
    }

    public SourceFailureReason Reason { get; }

    public string Phase { get; }
}
