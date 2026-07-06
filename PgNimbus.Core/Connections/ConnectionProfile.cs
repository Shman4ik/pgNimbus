using Npgsql;

namespace PgNimbus.Core.Connections;

public enum SslMode
{
    Disable,
    Allow,
    Prefer,
    Require,
    VerifyCa,
    VerifyFull,
}

internal static class SslModeExtensions
{
    public static Npgsql.SslMode ToNpgsql(this SslMode mode) => mode switch
    {
        SslMode.Disable => Npgsql.SslMode.Disable,
        SslMode.Allow => Npgsql.SslMode.Allow,
        SslMode.Prefer => Npgsql.SslMode.Prefer,
        SslMode.Require => Npgsql.SslMode.Require,
        SslMode.VerifyCa => Npgsql.SslMode.VerifyCA,
        SslMode.VerifyFull => Npgsql.SslMode.VerifyFull,
        _ => Npgsql.SslMode.Prefer,
    };
}

/// <summary>
/// A saved connection target. Never carries a password — the password is
/// supplied at connect time from wherever the caller retrieves it.
/// </summary>
public sealed record ConnectionProfile(
    Guid Id,
    string Name,
    string Host,
    int Port,
    string Database,
    string Username,
    SslMode SslMode,
    string? AccentColor = null,
    SshTunnelOptions? SshTunnel = null)
{
    public const int DefaultPort = 5432;

    // Callers resolve the password via ICredentialStore (DPAPI on Windows, a
    // permission-restricted file fallback elsewhere) and pass it in here -
    // it never lives on this record itself.
    //
    // When tunneling through SSH, pass the tunnel's local endpoint as
    // `endpointOverride` - Npgsql then connects to 127.0.0.1:<local port>
    // instead of the real host, while the rest of the profile (database,
    // username, SSL mode) still applies.
    public string BuildConnectionString(string? password, (string Host, int Port)? endpointOverride = null)
    {
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = endpointOverride?.Host ?? Host,
            Port = endpointOverride?.Port ?? Port,
            Database = Database,
            Username = Username,
            Password = password,
            SslMode = SslMode.ToNpgsql(),
            Timeout = 8,
            CommandTimeout = 0,
            IncludeErrorDetail = true,
            ApplicationName = "pgNimbus",
        };

        return builder.ConnectionString;
    }
}
