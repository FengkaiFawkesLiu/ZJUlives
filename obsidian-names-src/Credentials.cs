using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Shike;

public sealed record SchoolCredential(string Username, string Password);

/// <summary>Only encrypted bytes go to disk; no credentials are included in Store.Backup.</summary>
public sealed class CredentialVault
{
    readonly string path;
    static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Shike.ZjuCredentials.v1.CurrentUser");
    readonly byte[] purposeEntropy;
    public CredentialVault(string root, bool pintia = false) { path = Path.Combine(root, pintia ? "pintia-credentials.dpapi" : "school-credentials.dpapi"); purposeEntropy = pintia ? Encoding.UTF8.GetBytes("Shike.PintiaCredentials.v1.CurrentUser") : Entropy; }
    public bool Exists => File.Exists(path);
    public void Save(SchoolCredential credential)
    {
        if (string.IsNullOrWhiteSpace(credential.Username) || string.IsNullOrEmpty(credential.Password)) throw new InvalidDataException("账号或密码不能为空。");
        byte[] plain = JsonSerializer.SerializeToUtf8Bytes(credential);
        try
        {
            byte[] encrypted = ProtectedData.Protect(plain, purposeEntropy, DataProtectionScope.CurrentUser);
            byte[] check = ProtectedData.Unprotect(encrypted, purposeEntropy, DataProtectionScope.CurrentUser);
            try { if (!CryptographicOperations.FixedTimeEquals(plain, check)) throw new CryptographicException("本机加密验证失败，未保存凭据。"); }
            finally { CryptographicOperations.ZeroMemory(check); }
            var temp = path + ".tmp";
            try
            {
                using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None)) { stream.Write(encrypted); stream.Flush(true); }
                File.Move(temp, path, true);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    public SchoolCredential? Read()
    {
        if (!Exists) return null;
        byte[] plain = ProtectedData.Unprotect(File.ReadAllBytes(path), purposeEntropy, DataProtectionScope.CurrentUser);
        try
        {
            var result = JsonSerializer.Deserialize<SchoolCredential>(plain);
            if (result == null || string.IsNullOrEmpty(result.Username) || string.IsNullOrEmpty(result.Password)) throw new InvalidDataException("已保存凭据不完整，请重新登录。");
            return result;
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    public void Forget() { if (File.Exists(path)) File.Delete(path); if (File.Exists(path + ".tmp")) File.Delete(path + ".tmp"); }
    public static void Probe()
    {
        byte[] plain = RandomNumberGenerator.GetBytes(32), check = Array.Empty<byte>();
        try
        {
            var encrypted = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
            check = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
            if (!CryptographicOperations.FixedTimeEquals(plain, check)) throw new CryptographicException("加密解密校验失败。");
        }
        finally { CryptographicOperations.ZeroMemory(plain); CryptographicOperations.ZeroMemory(check); }
    }
}
