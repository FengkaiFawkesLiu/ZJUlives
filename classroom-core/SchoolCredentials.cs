using System.Text.Json;
using System.Text.Json.Serialization;
using ZJUSS.Core.Models;

namespace ZJUSS.Core.Security;

public sealed class SchoolCredentials
{
    public SchoolCredentials(string account, string password)
    {
        Account = account;
        Password = password;
    }

    public string Account { get; }

    /// <summary>只在内存中使用；序列化时忽略，避免凭据被写入学习数据、日志或诊断输出。</summary>
    [JsonIgnore]
    public string Password { get; }

    public override string ToString() => $"SchoolCredentials {{ Account = {Account}, Password = *** }}";
}


