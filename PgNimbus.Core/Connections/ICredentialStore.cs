namespace PgNimbus.Core.Connections;

/// <summary>Stores connection passwords outside of <see cref="ConnectionProfile"/> itself.</summary>
public interface ICredentialStore
{
    /// <summary>Actionable storage status; never includes a password or native error text.</summary>
    string? Warning => null;

    void SavePassword(Guid connectionId, string password);

    string? LoadPassword(Guid connectionId);

    void DeletePassword(Guid connectionId);
}
