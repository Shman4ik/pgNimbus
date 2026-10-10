using System.Globalization;
using System.Text;
using Npgsql;
using SslMode = PgNimbus.Core.Connections.SslMode;
using PgNimbus.Core.Connections;

namespace PgNimbus.Core.Backup;

/// <summary>
/// Where pg_dump and pg_restore connect: the same server, database, role and
/// TLS settings as the window's own sessions, written as a libpq connection
/// string. The password is carried beside it, never inside it, so the string
/// can be shown and copied as the command's preview.
/// </summary>
/// <param name="Host">
/// The server's own name: what TLS checks the certificate against and sends as
/// SNI. Through an SSH tunnel it is still the real host, and the socket goes to
/// <see cref="HostAddress"/> instead, which is libpq's own split between the
/// two (<c>host</c> and <c>hostaddr</c>). Npgsql has no such split, which is why
/// the app's pool needs a TLS callback for the same thing.
/// </param>
/// <param name="HostAddress">The address to open the socket to, when it differs from <see cref="Host"/> (the tunnel's <c>127.0.0.1</c>).</param>
/// <param name="Port">The port the socket goes to: the tunnel's local port when tunnelled.</param>
public sealed record PgToolConnection(
    string Host,
    int Port,
    string Database,
    string? Username,
    string? Password,
    SslMode SslMode,
    string? RootCertificatePath,
    string? HostAddress = null)
{
    /// <summary>Seconds a tool waits for the server to answer before giving up, like the app's own sessions.</summary>
    public const int ConnectTimeoutSeconds = 10;

    /// <summary>
    /// The connection a profile's window holds. <paramref name="tunnelEndpoint"/>
    /// is the local end of the window's SSH tunnel, which the tool reaches
    /// while the window stays open.
    /// </summary>
    public static PgToolConnection From(ConnectionProfile profile, string? password, (string Host, int Port)? tunnelEndpoint) =>
        new(
            profile.Host,
            tunnelEndpoint?.Port ?? profile.Port,
            profile.Database,
            string.IsNullOrEmpty(profile.Username) ? null : profile.Username,
            string.IsNullOrEmpty(password) ? null : password,
            profile.SslMode,
            profile.UsesRootCertificate ? profile.RootCertificatePath : null,
            tunnelEndpoint?.Host);

    /// <summary>
    /// The connection an Npgsql connection string names, for a window opened
    /// from <c>PGNIMBUS_CONN</c>, which has no profile behind it.
    /// </summary>
    public static PgToolConnection FromConnectionString(string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        var sslMode = builder.SslMode switch
        {
            Npgsql.SslMode.Disable => SslMode.Disable,
            Npgsql.SslMode.Allow => SslMode.Allow,
            Npgsql.SslMode.Require => SslMode.Require,
            Npgsql.SslMode.VerifyCA => SslMode.VerifyCa,
            Npgsql.SslMode.VerifyFull => SslMode.VerifyFull,
            _ => SslMode.Prefer,
        };
        return new PgToolConnection(
            builder.Host ?? "localhost",
            builder.Port,
            builder.Database ?? builder.Username ?? "",
            builder.Username,
            builder.Password,
            sslMode,
            sslMode is SslMode.VerifyCa or SslMode.VerifyFull ? builder.RootCertificate : null);
    }

    /// <summary>The same connection to another database on the server (a restore into a new one).</summary>
    public PgToolConnection ForDatabase(string database) => this with { Database = database };

    /// <summary>True when the socket goes through the window's SSH tunnel.</summary>
    public bool IsTunnelled => HostAddress is not null;

    /// <summary>
    /// The libpq connection string, without the password. Every setting the
    /// profile has is spelled out, so the inherited environment can't change
    /// where the tool connects: <c>gssencmode=disable</c> because the app's own
    /// sessions never use GSSAPI encryption, and a Kerberos ticket on a
    /// domain-joined machine would otherwise send libpq down that path first.
    /// </summary>
    /// <param name="trustedRootsPath">
    /// A PEM file of the CAs this machine trusts, for a verifying mode with no
    /// root certificate of its own. The app's sessions check the server
    /// against the OS trust store in that case; libpq knows no OS store (it
    /// reads <c>~/.postgresql/root.crt</c>, and fails when that is missing), so
    /// the caller exports one (<see cref="TrustedRoots"/>).
    /// </param>
    public string ToConnectionString(string? trustedRootsPath = null)
    {
        var parts = new List<(string Key, string Value)> { ("host", Host) };
        if (HostAddress is not null)
        {
            parts.Add(("hostaddr", HostAddress));
        }

        parts.Add(("port", Port.ToString(CultureInfo.InvariantCulture)));
        parts.Add(("dbname", Database));
        if (Username is not null)
        {
            parts.Add(("user", Username));
        }

        parts.Add(("sslmode", SslModeKeyword(SslMode)));
        if (SslMode is SslMode.VerifyCa or SslMode.VerifyFull)
        {
            var root = !string.IsNullOrWhiteSpace(RootCertificatePath) ? RootCertificatePath : trustedRootsPath;
            if (!string.IsNullOrWhiteSpace(root))
            {
                parts.Add(("sslrootcert", root));
            }
        }

        parts.Add(("gssencmode", "disable"));
        parts.Add(("connect_timeout", ConnectTimeoutSeconds.ToString(CultureInfo.InvariantCulture)));
        return string.Join(' ', parts.Select(p => p.Key + "=" + QuoteValue(p.Value)));
    }

    /// <summary>
    /// Whether a run needs <see cref="TrustedRoots"/>: a verifying mode with no
    /// certificate file of its own.
    /// </summary>
    public bool NeedsTrustedRoots =>
        SslMode is SslMode.VerifyCa or SslMode.VerifyFull && string.IsNullOrWhiteSpace(RootCertificatePath);

    /// <summary>libpq's spelling of a mode.</summary>
    public static string SslModeKeyword(SslMode mode) => mode switch
    {
        SslMode.Disable => "disable",
        SslMode.Allow => "allow",
        SslMode.Prefer => "prefer",
        SslMode.Require => "require",
        SslMode.VerifyCa => "verify-ca",
        SslMode.VerifyFull => "verify-full",
        _ => "prefer",
    };

    /// <summary>
    /// A value as a libpq connection string needs it: bare when it is a plain
    /// word, otherwise in single quotes with <c>\</c> and <c>'</c> escaped by a
    /// backslash. libpq splits on whitespace and reads <c>=</c>, so a path with
    /// a space (<c>C:\Program Files\…</c>) or a database named <c>a b</c> has to
    /// be quoted to stay one value.
    /// </summary>
    public static string QuoteValue(string value)
    {
        if (value.Length > 0 && value.All(c => !char.IsWhiteSpace(c) && c is not '\'' and not '\\' and not '='))
        {
            return value;
        }

        var builder = new StringBuilder(value.Length + 2).Append('\'');
        foreach (var c in value)
        {
            if (c is '\'' or '\\')
            {
                builder.Append('\\');
            }

            builder.Append(c);
        }

        return builder.Append('\'').ToString();
    }
}
