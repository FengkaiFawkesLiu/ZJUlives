namespace ZJUSS.Core.Logging;

public enum LogLevel
{
    Info,
    Warn,
    Error
}

/// <summary>只写本机的脱敏日志；任何实现都不得上传或发送通知。</summary>
public interface IAppLogger
{
    void Info(string module, string message);
    void Warn(string module, string message);
    void Error(string module, string category, Exception? exception = null);
}

public sealed class NullAppLogger : IAppLogger
{
    public static readonly NullAppLogger Instance = new();

    public void Info(string module, string message)
    {
    }

    public void Warn(string module, string message)
    {
    }

    public void Error(string module, string category, Exception? exception = null)
    {
    }
}
