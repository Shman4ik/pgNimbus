using System.Text;

namespace PgNimbus.Core.Connections;

/// <summary>
/// Fallback credential store for non-Windows platforms. Passwords are only
/// base64-encoded, not encrypted - this is a stopgap until real macOS
/// Keychain / Linux libsecret integration lands. The file is restricted to
/// the owning user where the OS supports POSIX permissions.
/// </summary>
public sealed class PlainFileCredentialStore : ICredentialStore
{
    private readonly string _directory;

    public PlainFileCredentialStore(string? directory = null)
    {
        _directory = directory ?? Path.Combine(AppDataPaths.GetRootDirectory(), "credentials");
    }

    public void SavePassword(Guid connectionId, string password)
    {
        Directory.CreateDirectory(_directory);
        var path = GetFilePath(connectionId);
        File.WriteAllText(path, Convert.ToBase64String(Encoding.UTF8.GetBytes(password)));

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    public string? LoadPassword(Guid connectionId)
    {
        var path = GetFilePath(connectionId);
        if (!File.Exists(path))
        {
            return null;
        }

        return Encoding.UTF8.GetString(Convert.FromBase64String(File.ReadAllText(path)));
    }

    public void DeletePassword(Guid connectionId)
    {
        var path = GetFilePath(connectionId);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private string GetFilePath(Guid connectionId) => Path.Combine(_directory, $"{connectionId:N}.cred");
}
