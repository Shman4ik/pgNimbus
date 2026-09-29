using System.Net.Security;
using System.Text.Json.Serialization;
using Npgsql;

namespace PgNimbus.Core.Connections;

/// <summary>
/// Persisted in connections.json as its number, so members are only ever
/// appended: renumbering would silently change every saved profile's mode.
/// The zero value is <see cref="Disable"/>, which is why the profile's
/// parameter carries <see cref="Require"/> as its default instead.
/// </summary>
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
/// <param name="ReadOnly">
/// Every session starts with <c>default_transaction_read_only</c> on, so the
/// server itself refuses INSERT, UPDATE, DELETE, COPY FROM and DDL, whichever
/// path sends them: the editor, the grid, an import, a schema action. It guards
/// against mistakes and is not a permission: a session can still
/// <c>SET default_transaction_read_only = off</c>. A role without write
/// privileges is the way to make writes impossible.
/// </param>
/// <param name="SslMode">
/// Defaults to <see cref="SslMode.Require"/> rather than the enum's zero value
/// (<see cref="SslMode.Disable"/>): the source-generated reader fills a field
/// missing from a hand-edited connections.json with this default, and a
/// profile that silently went plaintext-only is the worse failure.
/// </param>
/// <param name="RootCertificatePath">
/// A CA certificate file (PEM or DER) to trust for this connection instead of
/// the OS store: the provider bundles that RDS, Cloud SQL and Supabase publish,
/// which no OS trusts. Only the verifying modes read it. A path, not a secret.
/// </param>
public sealed record ConnectionProfile(
    Guid Id,
    string Name,
    string Host,
    int Port,
    string Database,
    string Username,
    SslMode SslMode = SslMode.Require,
    string? AccentColor = null,
    SshTunnelOptions? SshTunnel = null,
    bool ReadOnly = false,
    string? RootCertificatePath = null)
{
    public const int DefaultPort = 5432;

    /// <summary>
    /// The startup option a <see cref="ReadOnly"/> profile connects with. As a
    /// startup option it is the session's default, so the pool's reset between
    /// uses (<c>DISCARD ALL</c>) restores it rather than clearing it.
    /// </summary>
    public const string ReadOnlySessionOption = "-c default_transaction_read_only=on";

    /// <summary>
    /// One-line "who and where" for the connection list —
    /// <c>postgres@db.example.com/analytics</c>, with the port shown only when
    /// it isn't 5432 (the default is noise on every row). Enough to tell two
    /// profiles on the same host apart without selecting either.
    /// <see cref="JsonIgnoreAttribute"/> because it is derived: the source-
    /// generated serializer would otherwise write it into connections.json,
    /// where it would go stale the moment a field it is built from changes.
    /// </summary>
    [JsonIgnore]
    public string Endpoint => Port == DefaultPort
        ? $"{Username}@{Host}/{Database}"
        : $"{Username}@{Host}:{Port}/{Database}";

    // Callers resolve the password via ICredentialStore (DPAPI on Windows, a
    // Keychain on macOS, Secret Service on Linux) and pass it in here -
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

        if (ReadOnly)
        {
            builder.Options = ReadOnlySessionOption;
        }

        if (UsesRootCertificate)
        {
            builder.RootCertificate = RootCertificatePath;
        }

        return builder.ConnectionString;
    }

    /// <summary>
    /// True when <see cref="RootCertificatePath"/> is set and the mode reads it.
    /// Npgsql ignores a root certificate under Prefer and Require (they check
    /// nothing), so it is not written there: a connection string naming a CA
    /// would read as if the server were being checked against it.
    /// </summary>
    [JsonIgnore]
    public bool UsesRootCertificate =>
        !string.IsNullOrWhiteSpace(RootCertificatePath) && SslMode is SslMode.VerifyCa or SslMode.VerifyFull;

    /// <summary>
    /// The pool a connect goes through. Through an SSH tunnel the socket goes to
    /// <c>127.0.0.1:&lt;port&gt;</c>, and Npgsql would check the server
    /// certificate's name against that address, so VerifyFull could never pass
    /// there. The callback puts the profile's real host back as the TLS target
    /// (the SNI sent and the name the certificate must carry); every other
    /// setting is exactly <see cref="BuildConnectionString"/>'s.
    /// </summary>
    public NpgsqlDataSource CreateDataSource(string? password, (string Host, int Port)? endpointOverride = null, bool pooling = true)
    {
        var connectionString = BuildConnectionString(password, endpointOverride);
        if (!pooling)
        {
            connectionString = new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString;
        }

        var builder = new NpgsqlDataSourceBuilder(connectionString);
        if (endpointOverride is not null)
        {
            builder.UseSslClientAuthenticationOptionsCallback(ApplyTunnelTargetHost);
        }

        return builder.Build();
    }

    /// <summary>
    /// The TLS half of <see cref="CreateDataSource"/> for a tunnelled connection:
    /// names the profile's own host as the handshake's target.
    /// </summary>
    public void ApplyTunnelTargetHost(SslClientAuthenticationOptions options) => options.TargetHost = Host;
}
