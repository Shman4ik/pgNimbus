namespace PgNimbus.Core.Connections;

// Persisted as its number in connections.json: append, never reorder.
public enum SshAuthMethod
{
    Password,
    PrivateKey,

    /// <summary>The keys a running SSH agent holds (Windows OpenSSH service, <c>SSH_AUTH_SOCK</c>).</summary>
    Agent,
}

/// <summary>
/// Optional SSH jump-host config for a connection. When present, the app
/// tunnels through this host instead of connecting to the database
/// directly. Never carries a password/passphrase - same rule as
/// <see cref="ConnectionProfile"/> itself.
/// </summary>
public sealed record SshTunnelOptions(
    string Host,
    int Port,
    string Username,
    SshAuthMethod AuthMethod,
    string? PrivateKeyPath = null)
{
    public const int DefaultPort = 22;
}
