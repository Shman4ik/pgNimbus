using System.Security.Cryptography;
using System.Text;

namespace PgNimbus.Core.Connections;

/// <summary>
/// Serializes native store access, preserves usable session credentials on failure,
/// and migrates the old non-Windows .cred files only after a verified native write.
/// Call from a worker thread: the OS may need to unlock its credential store.
/// </summary>
public sealed class RecoverableCredentialStore(ICredentialStore persistent, string? legacyDirectory = null) : ICredentialStore
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, string?> _session = [];
    private readonly Dictionary<Guid, string> _warnings = [];
    private string? _warning;
    // UI callers must not wait behind another window's native store operation.
    public string? Warning => Volatile.Read(ref _warning);

    public void SavePassword(Guid connectionId, string password)
    {
        lock (_gate)
        {
            _session[connectionId] = password;
            try
            {
                StoreVerified(connectionId, password);
            }
            catch (Exception ex) when (IsStorageFailure(ex))
            {
                Warn(connectionId);
            }
        }
    }

    public string? LoadPassword(Guid connectionId)
    {
        lock (_gate)
        {
            if (_session.TryGetValue(connectionId, out var cached)) return cached;
            string? legacy = null;
            try
            {
                // Read the old file even when the native store is unavailable, so
                // an upgrade does not lock users out. Never write this format again.
                var path = LegacyPath(connectionId);
                try
                {
                    if (path is not null && File.Exists(path))
                        legacy = new UTF8Encoding(false, true).GetString(Convert.FromBase64String(File.ReadAllText(path)));
                }
                catch (Exception ex) when (IsStorageFailure(ex)) { WarnLegacy(connectionId); }

                var password = persistent.LoadPassword(connectionId);
                if (password is not null)
                {
                    // A native entry wins over a stale legacy copy. Do not remove
                    // a different old value without a new explicit save.
                    if (legacy == password) DeleteLegacy(connectionId);
                    else if (legacy is not null) WarnLegacy(connectionId);
                    return _session[connectionId] = password;
                }
                if (legacy is null) return null;
                StoreVerified(connectionId, legacy);
                return _session[connectionId] = legacy;
            }
            catch (Exception ex) when (IsStorageFailure(ex))
            {
                Warn(connectionId);
                if (legacy is not null) _session[connectionId] = legacy;
                return legacy;
            }
        }
    }

    public void DeletePassword(Guid connectionId)
    {
        lock (_gate)
        {
            // A failed delete must not resurrect a credential within this session.
            _session[connectionId] = null;
            _warnings.Remove(connectionId);
            UpdateWarning();
            try { DeleteLegacy(connectionId); }
            catch (Exception ex) when (IsStorageFailure(ex)) { WarnLegacy(connectionId); }
            try { persistent.DeletePassword(connectionId); }
            catch (Exception ex) when (IsStorageFailure(ex))
            {
                _warnings[connectionId] = "A saved password could not be deleted. Unlock your OS credential store and retry, or remove the pgNimbus entry in your system password manager.";
                UpdateWarning();
            }
        }
    }

    private void StoreVerified(Guid id, string password)
    {
        persistent.SavePassword(id, password);
        if (!string.Equals(persistent.LoadPassword(id), password, StringComparison.Ordinal))
            throw new CredentialStoreException();
        DeleteLegacy(id);
        _warnings.Remove(id);
        UpdateWarning();
    }

    private string? LegacyPath(Guid id) => legacyDirectory is null ? null : Path.Combine(legacyDirectory, $"{id:N}.cred");
    private void DeleteLegacy(Guid id)
    {
        if (LegacyPath(id) is { } path) File.Delete(path);
    }
    private void Warn(Guid id)
    {
        _warnings[id] = "Password storage is unavailable or migration could not finish. You can connect using the password in this dialog, but changes may last only for this session. Check your OS credential store (Keychain on macOS; Secret Service and libsecret-1 on Linux), then re-enter the password. Existing legacy credential files are kept until migration is verified.";
        UpdateWarning();
    }
    private void WarnLegacy(Guid id)
    {
        _warnings[id] = "An old unencrypted credential file remains. Save this connection again to verify the OS-stored password and remove the legacy file.";
        UpdateWarning();
    }
    private void UpdateWarning() => Volatile.Write(ref _warning,
        _warnings.Count == 0 ? null : string.Join("\n", _warnings.Values.Distinct()));
    private static bool IsStorageFailure(Exception ex) => ex is CredentialStoreException or IOException
        or UnauthorizedAccessException or CryptographicException or FormatException or DecoderFallbackException
        or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException;
}

/// <summary>Deliberately excludes native messages, which may contain sensitive data.</summary>
public sealed class CredentialStoreException() : Exception("The OS credential store is unavailable.");
