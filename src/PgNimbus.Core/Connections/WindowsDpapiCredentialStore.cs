using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using PgNimbus.Core.Settings;

namespace PgNimbus.Core.Connections;

/// <summary>
/// Encrypts each connection's password at rest using Windows DPAPI, scoped to
/// the current user account. Only the user who saved a password (on the
/// machine it was saved on) can decrypt it back. Each file is replaced
/// atomically through <see cref="AppDataFile"/>; with no app data directory at
/// all (<paramref name="directory"/> and <see cref="AppDataPaths.Resolve"/>
/// both null) a save reports storage as unavailable, which the wrapping
/// <see cref="RecoverableCredentialStore"/> turns into "kept for this session".
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsDpapiCredentialStore(string? directory = null) : ICredentialStore
{
    private readonly string? _directory = directory ?? AppDataPaths.Resolve("credentials");

    public void SavePassword(Guid connectionId, string password)
    {
        if (_directory is null)
        {
            throw new IOException("There is no application data directory to keep the password in.");
        }

        AppDataFile.EnsureDirectory(_directory);
        var plainBytes = Encoding.UTF8.GetBytes(password);
        var protectedBytes = ProtectedData.Protect(plainBytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
        AppDataFile.WriteAllBytes(GetFilePath(connectionId), protectedBytes);
    }

    public string? LoadPassword(Guid connectionId)
    {
        var path = GetFilePath(connectionId);
        if (path is null || !File.Exists(path))
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
        if (path is not null && File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private string? GetFilePath(Guid connectionId) =>
        _directory is null ? null : Path.Combine(_directory, $"{connectionId:N}.dpapi");
}
