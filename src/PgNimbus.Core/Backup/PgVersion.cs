using System.Globalization;
using System.Text.RegularExpressions;

namespace PgNimbus.Core.Backup;

/// <summary>
/// A PostgreSQL release as pg_dump's compatibility rule reads it. Since 10 the
/// major version is one number (17.4 is major 17, minor 4); before that it was
/// two (9.6.24 is major 9.6, minor 24), which is why <see cref="Minor"/> takes
/// part in <see cref="MajorKey"/> only below 10.
/// </summary>
public readonly record struct PgVersion(int Major, int Minor) : IComparable<PgVersion>
{
    // "pg_dump (PostgreSQL) 18.6", "… 17.2 (Debian 17.2-1.pgdg120+1)",
    // "… 9.6.24", "… 18beta1", "… 19devel". The first number after the program's
    // name is the version; a suffix such as "beta1" or "devel" is ignored.
    private static readonly Regex VersionLine = new(
        @"\(PostgreSQL\)\s+(?<major>\d+)(?:\.(?<minor>\d+))?",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
        TimeSpan.FromSeconds(1));

    /// <summary>
    /// The major release as one comparable number: 1700 for 17, 906 for 9.6. Two
    /// versions with the same key are the same major release.
    /// </summary>
    public int MajorKey => Major >= 10 ? Major * 100 : (Major * 100) + Minor;

    /// <summary>The major release as PostgreSQL writes it: <c>17</c>, <c>9.6</c>.</summary>
    public string MajorLabel => Major >= 10
        ? Major.ToString(CultureInfo.InvariantCulture)
        : $"{Major.ToString(CultureInfo.InvariantCulture)}.{Minor.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>The release as a person reads it: <c>17.4</c>, <c>9.6</c>.</summary>
    public override string ToString() =>
        $"{Major.ToString(CultureInfo.InvariantCulture)}.{Minor.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>
    /// Whether a pg_dump of this version can dump a server of <paramref name="server"/>'s
    /// version: it has to be the server's major release or newer. pg_dump refuses
    /// a newer server outright ("aborting because of server version mismatch"),
    /// and a newer pg_dump reads every older server back to 9.2.
    /// </summary>
    public bool CanDump(PgVersion server) => MajorKey >= server.MajorKey;

    public int CompareTo(PgVersion other) =>
        Major != other.Major ? Major.CompareTo(other.Major) : Minor.CompareTo(other.Minor);

    public static bool operator <(PgVersion left, PgVersion right) => left.CompareTo(right) < 0;

    public static bool operator >(PgVersion left, PgVersion right) => left.CompareTo(right) > 0;

    public static bool operator <=(PgVersion left, PgVersion right) => left.CompareTo(right) <= 0;

    public static bool operator >=(PgVersion left, PgVersion right) => left.CompareTo(right) >= 0;

    /// <summary>
    /// Reads what <c>pg_dump --version</c> (or <c>pg_restore</c>, <c>psql</c>)
    /// prints. False for anything else, so a program that merely shares the name
    /// is not taken for one of PostgreSQL's.
    /// </summary>
    public static bool TryParseToolOutput(string? output, out PgVersion version)
    {
        version = default;
        if (string.IsNullOrEmpty(output) || output.Length > 4096)
        {
            return false;
        }

        var match = VersionLine.Match(output);
        if (!match.Success
            || !int.TryParse(match.Groups["major"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var major))
        {
            return false;
        }

        var minor = 0;
        if (match.Groups["minor"].Success
            && !int.TryParse(match.Groups["minor"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out minor))
        {
            return false;
        }

        version = new PgVersion(major, minor);
        return true;
    }

    /// <summary>
    /// The server's version as Npgsql reports it (<c>NpgsqlConnection.PostgreSqlVersion</c>):
    /// 17.4 → 17.4, 9.6.24 → 9.6.
    /// </summary>
    public static PgVersion FromServer(Version version) => new(version.Major, Math.Max(version.Minor, 0));

    /// <summary>
    /// A version as an archive's header writes it ("Dumped from database version:
    /// 17.11 (Debian 17.11-1.pgdg13+2)"): the leading <c>major[.minor]</c>.
    /// </summary>
    public static bool TryParse(string? text, out PgVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var span = text.AsSpan().Trim();
        var end = 0;
        while (end < span.Length && (char.IsAsciiDigit(span[end]) || span[end] == '.'))
        {
            end++;
        }

        var parts = span[..end].ToString().Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0
            || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major))
        {
            return false;
        }

        var minor = 0;
        if (parts.Length > 1 && !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out minor))
        {
            return false;
        }

        version = new PgVersion(major, minor);
        return true;
    }
}
