using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace PgNimbus.Core.Connections;

/// <summary>
/// Serializes native store access, keeps a password usable for the session when
/// the OS store refuses it, and migrates the old non-Windows .cred files only
/// after a verified native write.
/// Call from a worker thread: the OS may need to unlock its credential store.
///
/// <para>What stays in memory is deliberately little (security audit 2026-09,
/// finding 18). The session cache holds only passwords whose native write
/// failed, the one case where memory is the only copy; a password the OS
/// store accepted, or one read back from it, is not retained. A delete removes
/// the entry, and a delete the OS store refused is remembered by id alone, so
/// the password can't come back within the session while nothing holds its
/// value. <see cref="Forget"/> drops a session-only password when the window
/// that used it closes.</para>
/// </summary>
public sealed class RecoverableCredentialStore(ICredentialStore persistent, string? legacyDirectory = null) : ICredentialStore
{
    private readonly object _gate = new();

    // Only passwords whose native write failed.
    private readonly Dictionary<Guid, string> _session = [];

    // Ids whose native delete failed: read as "no password" until saved again.
    private readonly HashSet<Guid> _deleteFailed = [];

    private readonly Dictionary<Guid, string> _warnings = [];

    // Legacy files still on disk that could not be migrated or removed.
    private readonly HashSet<Guid> _legacyLeft = [];

    private bool _migrationPassDone;
    private string? _warning;

    // UI callers must not wait behind another window's native store operation.
    public string? Warning => Volatile.Read(ref _warning);

    public void SavePassword(Guid connectionId, string password)
    {
        lock (_gate)
        {
            _deleteFailed.Remove(connectionId);
            try
            {
                StoreVerified(connectionId, password);
                _session.Remove(connectionId);
            }
            catch (Exception ex) when (IsStorageFailure(ex))
            {
                _session[connectionId] = password;
                Warn(connectionId);
            }
        }
    }

    public string? LoadPassword(Guid connectionId)
    {
        lock (_gate)
        {
            if (_session.TryGetValue(connectionId, out var cached)) return cached;
            if (_deleteFailed.Contains(connectionId)) return null;
            string? legacy = null;
            try
            {
                // Read the old file even when the native store is unavailable, so
                // an upgrade does not lock users out. Never write this format again.
                try
                {
                    legacy = ReadLegacy(connectionId);
                }
                catch (Exception ex) when (IsStorageFailure(ex)) { WarnLegacy(connectionId); }

                var password = persistent.LoadPassword(connectionId);
                if (password is not null)
                {
                    // A native entry wins over a stale legacy copy. Do not remove
                    // a different old value without a new explicit save.
                    if (legacy == password) DeleteLegacy(connectionId);
                    else if (legacy is not null) WarnLegacy(connectionId);
                    return password;
                }
                if (legacy is null) return null;
                StoreVerified(connectionId, legacy);
                return legacy;
            }
            catch (Exception ex) when (IsStorageFailure(ex))
            {
                // The legacy file is still there to be read again, so nothing
                // needs to be held in memory for it.
                Warn(connectionId);
                return legacy;
            }
        }
    }

    public void DeletePassword(Guid connectionId)
    {
        lock (_gate)
        {
            _session.Remove(connectionId);
            _warnings.Remove(connectionId);
            UpdateWarning();
            try { DeleteLegacy(connectionId); }
            catch (Exception ex) when (IsStorageFailure(ex)) { WarnLegacy(connectionId); }
            try
            {
                persistent.DeletePassword(connectionId);
                _deleteFailed.Remove(connectionId);
            }
            catch (Exception ex) when (IsStorageFailure(ex))
            {
                // A failed delete must not resurrect the credential within this session.
                _deleteFailed.Add(connectionId);
                _warnings[connectionId] = "A saved password could not be deleted. Unlock your OS credential store and retry, or remove the pgNimbus entry in your system password manager.";
                UpdateWarning();
            }
        }
    }

    /// <inheritdoc />
    public void Forget(Guid connectionId)
    {
        lock (_gate)
        {
            _session.Remove(connectionId);
        }
    }

    /// <summary>
    /// Moves every legacy <c>.cred</c> file into the OS store in one pass, the
    /// first time it is called in the process; later calls do nothing. Before
    /// this, a file moved only when its profile was opened, so the profiles
    /// nobody opened after upgrading kept a base64 password on disk for good.
    /// A file is removed once the OS store holds the same value (written and
    /// read back). Files that can't be moved — the store is unavailable, the
    /// file is unreadable, or the store already holds a different password for
    /// that id — stay, and are reported once through <see cref="Warning"/>.
    /// </summary>
    public void MigrateLegacyFiles()
    {
        lock (_gate)
        {
            if (_migrationPassDone || legacyDirectory is null) return;
            _migrationPassDone = true;

            string[] files;
            try
            {
                if (!Directory.Exists(legacyDirectory)) return;
                files = Directory.GetFiles(legacyDirectory, "*.cred");
            }
            catch (Exception ex) when (IsStorageFailure(ex))
            {
                return;
            }

            var storeRefused = false;
            foreach (var file in files)
            {
                if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(file), "N", out var id)) continue;
                if (storeRefused)
                {
                    WarnLegacy(id);
                    continue;
                }

                string? legacy;
                try
                {
                    legacy = ReadLegacy(id);
                }
                catch (Exception ex) when (IsStorageFailure(ex))
                {
                    // This file is unreadable; the store was not asked.
                    WarnLegacy(id);
                    continue;
                }

                if (legacy is null) continue;
                try
                {
                    var native = persistent.LoadPassword(id);
                    if (native is null) StoreVerified(id, legacy);
                    else if (native == legacy) DeleteLegacy(id);
                    else WarnLegacy(id);
                }
                catch (Exception ex) when (IsStorageFailure(ex))
                {
                    // The OS store refused: unavailable, locked, or a Secret
                    // Service call that waited out its 15 s. Every later file
                    // would wait on it again, with the lock held and the first
                    // connect queued behind this pass, so the rest are left and
                    // reported rather than tried (review of the 2026-09 audit
                    // fixes).
                    storeRefused = true;
                    WarnLegacy(id);
                }
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

    private string? ReadLegacy(Guid id) =>
        LegacyPath(id) is { } path && File.Exists(path)
            ? new UTF8Encoding(false, true).GetString(Convert.FromBase64String(File.ReadAllText(path)))
            : null;

    private void DeleteLegacy(Guid id)
    {
        if (LegacyPath(id) is { } path) File.Delete(path);
        if (_legacyLeft.Remove(id)) UpdateWarning();
    }

    private void Warn(Guid id)
    {
        _warnings[id] = "Password storage is unavailable or migration could not finish. You can connect using the password in this dialog, but changes may last only for this session. Check your OS credential store (Keychain on macOS; Secret Service and libsecret-1 on Linux), then re-enter the password. Existing legacy credential files are kept until migration is verified.";
        UpdateWarning();
    }

    private void WarnLegacy(Guid id)
    {
        _legacyLeft.Add(id);
        UpdateWarning();
    }

    private void UpdateWarning()
    {
        var messages = _warnings.Values.Distinct().ToList();
        if (_legacyLeft.Count > 0)
        {
            messages.Add(_legacyLeft.Count == 1
                ? "An old unencrypted credential file could not be moved into your OS credential store and is still on disk. Open the connection it belongs to and save its password again to remove it."
                : string.Create(CultureInfo.InvariantCulture, $"{_legacyLeft.Count} old unencrypted credential files could not be moved into your OS credential store and are still on disk. Open the connections they belong to and save their passwords again to remove them."));
        }

        Volatile.Write(ref _warning, messages.Count == 0 ? null : string.Join("\n", messages));
    }

    private static bool IsStorageFailure(Exception ex) => ex is CredentialStoreException or IOException
        or UnauthorizedAccessException or CryptographicException or FormatException or DecoderFallbackException
        or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException;
}

/// <summary>Deliberately excludes native messages, which may contain sensitive data.</summary>
public sealed class CredentialStoreException() : Exception("The OS credential store is unavailable.");
