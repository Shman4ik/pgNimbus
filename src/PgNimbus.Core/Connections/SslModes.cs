using PgNimbus.Core.Localization;

namespace PgNimbus.Core.Connections;

/// <summary>What the SSL mode picker shows for one <see cref="SslMode"/>.</summary>
/// <param name="LabelKey">The mode's name as people write it ("Verify full", not "VerifyFull"). A key, so a picker follows a change of language.</param>
/// <param name="DescriptionKey">One line saying what the mode protects against, and what it does not.</param>
/// <param name="Recommended">The mode the picker marks as the one to use.</param>
public sealed record SslModeInfo(SslMode Mode, StringKey LabelKey, StringKey DescriptionKey, bool Recommended = false)
{
    /// <summary>The mode's name in the language in use.</summary>
    public string Label => Strings.Get(LabelKey);

    /// <summary>The one line in the language in use.</summary>
    public string Description => Strings.Get(DescriptionKey);
}

/// <summary>
/// The SSL mode picker's text. The bare enum names used to be all it showed, and
/// "Require" reads as the safe choice when it checks nothing about the server:
/// anyone who can intercept the connection can present any certificate. Each
/// mode now says in one line what it does, and Verify full is marked as the one
/// that actually proves who is on the other end.
/// </summary>
public static class SslModes
{
    // Built on each read, in the language in force then.
    private static SslModeInfo[] Infos() =>
    [
        new(SslMode.Disable, StringKey.Connection_SslDisable, StringKey.Connection_SslDisableHint),
        new(SslMode.Allow, StringKey.Connection_SslAllow, StringKey.Connection_SslAllowHint),
        new(SslMode.Prefer, StringKey.Connection_SslPrefer, StringKey.Connection_SslPreferHint),
        new(SslMode.Require, StringKey.Connection_SslRequire, StringKey.Connection_SslRequireHint),
        new(SslMode.VerifyCa, StringKey.Connection_SslVerifyCa, StringKey.Connection_SslVerifyCaHint),
        new(SslMode.VerifyFull, StringKey.Connection_SslVerifyFull, StringKey.Connection_SslVerifyFullHint,
            Recommended: true),
    ];

    /// <summary>Every mode, in the enum's order.</summary>
    public static IReadOnlyList<SslModeInfo> All => Infos();

    /// <summary>
    /// The mode a new profile starts at for <paramref name="host"/>: Require,
    /// except Prefer for this machine (<see cref="IsLoopback"/>), where there is
    /// no network path to attack and the usual server (a local Docker Postgres)
    /// has TLS off, so Require failed on the first connect anyone tries
    /// (review of the 2026-09 audit fixes).
    /// </summary>
    public static SslMode DefaultFor(string host) => IsLoopback(host) ? SslMode.Prefer : SslMode.Require;

    /// <summary>
    /// <c>localhost</c>, a name under <c>.localhost</c>, a loopback address
    /// (<c>127.0.0.0/8</c>, <c>::1</c>, bracketed or not) or a Unix-domain socket
    /// directory. A host list (<c>a,b</c>) is never loopback.
    /// </summary>
    public static bool IsLoopback(string host)
    {
        var h = host.Trim();
        if (h.Length == 0 || h.Contains(','))
        {
            return false;
        }

        if (h.StartsWith('/') || h.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || h.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return System.Net.IPAddress.TryParse(h.TrimStart('[').TrimEnd(']'), out var address)
            && System.Net.IPAddress.IsLoopback(address);
    }

    public static SslModeInfo Describe(SslMode mode) =>
        Array.Find(Infos(), info => info.Mode == mode)
        ?? throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown SSL mode.");

    /// <summary>
    /// What to add to a connect failure when a mode that insists on TLS met a
    /// server that has none, which is what a local Docker Postgres is by
    /// default. New profiles start at Require, so this is the first error a
    /// person pointing one at localhost sees, and Npgsql's own wording ("No SSL
    /// enabled connection from this host is configured") does not say which
    /// setting to change. Null for any other failure.
    /// </summary>
    public static string? ServerWithoutTlsHint(Exception failure, SslMode mode)
    {
        if (mode is not (SslMode.Require or SslMode.VerifyCa or SslMode.VerifyFull))
        {
            return null;
        }

        for (var e = failure; e is not null; e = e.InnerException)
        {
            if (e.Message.Contains("No SSL enabled connection", StringComparison.OrdinalIgnoreCase))
            {
                return Strings.Connection_ServerWithoutTlsHint;
            }
        }

        return null;
    }
}
