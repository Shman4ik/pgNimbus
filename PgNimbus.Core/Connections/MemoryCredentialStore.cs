namespace PgNimbus.Core.Connections;

/// <summary>Ephemeral credentials for isolated previews/tests; never touches the OS store.</summary>
public sealed class MemoryCredentialStore : ICredentialStore
{
    private readonly Dictionary<Guid, string> _passwords = [];
    public void SavePassword(Guid connectionId, string password) => _passwords[connectionId] = password;
    public string? LoadPassword(Guid connectionId) => _passwords.GetValueOrDefault(connectionId);
    public void DeletePassword(Guid connectionId) => _passwords.Remove(connectionId);
}
