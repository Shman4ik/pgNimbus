using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace PgNimbus.Core.Connections;

/// <summary>
/// Encrypts each connection's password at rest using Windows DPAPI, scoped to
/// the current user account. Only the user who saved a password (on the
/// machine it was saved on) can decrypt it back.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsDpapiCredentialStore : ICredentialStore
{
    private readonly string _directory;

    public WindowsDpapiCredentialStore(string? directory = null)
    {
        _directory = directory ?? Path.Combine(AppDataPaths.GetRootDirectory(), "credentials");
    }

    public void SavePassword(Guid connectionId, string password)
    {
        Directory.CreateDirectory(_directory);
        var plainBytes = Encoding.UTF8.GetBytes(password);
        var protectedBytes = ProtectedData.Protect(plainBytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(GetFilePath(connectionId), protectedBytes);
    }

    public string? LoadPassword(Guid connectionId)
    {
        var path = GetFilePath(connectionId);
        if (!File.Exists(path))
        {
            return null;
        }

        var protectedBytes = File.ReadAllBytes(path);
        var plainBytes = ProtectedData.Unprotect(protectedBytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(plainBytes);
    }

    public void DeletePassword(Guid connectionId)
    {
        var path = GetFilePath(connectionId);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private string GetFilePath(Guid connectionId) => Path.Combine(_directory, $"{connectionId:N}.dpapi");
}
