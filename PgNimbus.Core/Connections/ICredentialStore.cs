namespace PgNimbus.Core.Connections;

/// <summary>Stores connection passwords outside of <see cref="ConnectionProfile"/> itself.</summary>
public interface ICredentialStore
{
    /// <summary>Actionable storage status; never includes a password or native error text.</summary>
    string? Warning => null;

    void SavePassword(Guid connectionId, string password);

    string? LoadPassword(Guid connectionId);

    void DeletePassword(Guid connectionId);

    /// <summary>
    /// Drops anything held in memory for <paramref name="connectionId"/> without
    /// touching what is stored; called when the window that used it closes.
    /// </summary>
    void Forget(Guid connectionId)
    {
    }

    /// <summary>
    /// Moves passwords left in an older on-disk format into this store, once per
    /// process. Nothing to do for a store that never had one.
    /// </summary>
    void MigrateLegacyFiles()
    {
    }
}
