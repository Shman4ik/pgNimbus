using System.Globalization;

namespace PgNimbus.Core.Query;

/// <summary>
/// Renders a CLR value as a PostgreSQL literal for *display* — the review
/// script safe mode shows before committing staged changes. Execution never
/// interpolates these strings: the staged statements run parameterized, so a
/// value this formatter renders imperfectly (an exotic composite, say) can at
/// worst mislead the preview, never break or inject into the real statement.
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

    /// <summary>Single-quotes a string, doubling embedded quotes (<c>'</c> → <c>''</c>).</summary>
    public static string Quote(string text) => $"'{text.Replace("'", "''")}'";
}
