using System.Globalization;

namespace PgNimbus.Core.Import;

/// <summary>
/// Guesses a Postgres column type from a column's string values, for the
/// CREATE TABLE an import generates. Deliberately conservative: a type only
/// wins if every non-null value parses as it, leading-zero integers ("007")
/// stay text, and anything ambiguous falls back to text — a wrong "text" is
/// an inconvenience, a wrong "bigint" is a failed import.
/// </summary>
public static class TypeInferrer
{
    /// <summary>The closed set of types inference can produce (also the safe allow-list for generated DDL).</summary>
    public static readonly IReadOnlyList<string> Types =
        ["text", "bigint", "double precision", "boolean", "date", "timestamptz", "uuid", "jsonb"];

    public static string Infer(IEnumerable<string?> values)
    {
        var sawAny = false;
        bool boolean = true, integer = true, dbl = true, date = true, timestamp = true, uuid = true;

        foreach (var value in values)
        {
            if (value is null || value.Length == 0)
            {
                continue;
            }

            sawAny = true;
            var v = value.Trim();

            // "007" is an identifier-like code, not the number 7 — a leading
            // zero (unless it's "0" itself or a decimal like "0.5") forces text
            // rather than silently dropping digits on import.
            var digits = v.TrimStart('-', '+');
            var leadingZero = digits.Length > 1 && digits[0] == '0' && digits[1] != '.';

            boolean = boolean && (v.Equals("true", StringComparison.OrdinalIgnoreCase) || v.Equals("false", StringComparison.OrdinalIgnoreCase));
            integer = integer && !leadingZero && long.TryParse(v, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _);
            dbl = dbl && !leadingZero && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out _);
            date = date && DateOnly.TryParseExact(v, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
            timestamp = timestamp && (date || DateTimeOffset.TryParse(v, CultureInfo.InvariantCulture, DateTimeStyles.None, out _));
            uuid = uuid && Guid.TryParse(v, out _);

            if (!boolean && !integer && !dbl && !date && !timestamp && !uuid)
            {
                return "text";
            }
        }

        if (!sawAny)
        {
            return "text";
        }

        return boolean ? "boolean"
            : integer ? "bigint"
            : dbl ? "double precision"
            : uuid ? "uuid"
            : date ? "date"
            : timestamp ? "timestamptz"
            : "text";
    }
}
