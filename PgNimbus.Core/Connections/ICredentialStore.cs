namespace PgNimbus.Core.Connections;

/// <summary>Stores connection passwords outside of <see cref="ConnectionProfile"/> itself.</summary>
public interface ICredentialStore
{
    void SavePassword(Guid connectionId, string password);

    string? LoadPassword(Guid connectionId);

    void DeletePassword(Guid connectionId);
}
