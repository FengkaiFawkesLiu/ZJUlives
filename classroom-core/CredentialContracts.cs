using ZJUSS.Core.Security;
namespace ZJUSS.Core.Access;
public interface ICredentialProvider
{
    bool IsUnlocked { get; }
    string? Account { get; }

    /// <summary>凭据保存成功时触发；只表示保存完成，不代表任何网站已经重试或联网。</summary>
    event EventHandler? CredentialsSaved;

    /// <summary>仅在解锁后返回凭据副本；调用方不得持久化，也不得写入日志。</summary>
    SchoolCredentials? GetCredentials();
}


