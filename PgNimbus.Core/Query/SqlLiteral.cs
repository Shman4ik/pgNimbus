using System.Globalization;

namespace PgNimbus.Core.Query;

/// <summary>
/// Renders a CLR value as a PostgreSQL string literal, <c>'…'</c> with the
/// quote doubled and nothing else escaped.
///
/// <para>This text is <em>executed</em>, not only shown. Safe mode's review
/// script is display (the staged statements run parameterized), but browse
/// filters (<see cref="RowFilterSql"/>, including filter-by-cell), the FK hop's
/// seed and a role's <c>VALID UNTIL</c> and <c>COMMENT</c> (run by
/// <c>SecurityEditor</c>) all go to the server as this text, because browse
/// mode's WHERE lands in the editor as SQL the user reads and edits, and a
/// parameter would not survive that round trip.</para>
///
/// <para>Doubling the quote is the whole escape only under
/// <c>standard_conforming_strings = on</c>: off, a backslash escapes as well,
/// and a stored value <c>x\' OR … --</c> would close the literal early. That
/// is why every session the app opens forces the setting on as a startup
/// option (<see cref="Connections.ConnectionProfile.StandardStringsSessionOption"/>),
/// which overrides a database's or a role's default and survives the pool's
/// reset. It does not reach the server when a pooler drops startup options
/// (the PgBouncer case of the audit's finding 17), so text holding a backslash
/// is written as <c>E'…'</c> with the backslash doubled too: an escape string
/// reads the same under either setting, and a stored <c>x\' OR … --</c> stays
/// inside it (review of the 2026-09 audit fixes). Text without a backslash
/// keeps the plain form, which is the same under both.</para>
/// </summary>
public static class SqlLiteral
{
    public static string Format(object? value) => value switch
    {
        null => "NULL",
        bool b => b ? "true" : "false",
        sbyte or byte or short or ushort or int or uint or long or ulong =>
            ((IFormattable)value).ToString(null, CultureInfo.InvariantCulture),
        float f => f.ToString("R", CultureInfo.InvariantCulture),
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        decimal m => m.ToString(CultureInfo.InvariantCulture),
        string s => Quote(s),
        DateTime dt => Quote(dt.ToString("yyyy-MM-dd HH:mm:ss.FFFFFF", CultureInfo.InvariantCulture)),
        DateTimeOffset dto => Quote(dto.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFzzz", CultureInfo.InvariantCulture)),
        DateOnly d => Quote(d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
        TimeOnly t => Quote(t.ToString("HH:mm:ss.FFFFFF", CultureInfo.InvariantCulture)),
        _ => Quote(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty),
    };

    /// <summary>
    /// Single-quotes a string, doubling embedded quotes (<c>'</c> → <c>''</c>);
    /// a string holding a backslash becomes <c>E'…'</c> with backslashes doubled
    /// as well, so it means the same whatever <c>standard_conforming_strings</c> is.
    /// </summary>
    public static string Quote(string text) => text.Contains('\\')
        ? $"E'{text.Replace("\\", "\\\\").Replace("'", "''")}'"
        : $"'{text.Replace("'", "''")}'";
}
