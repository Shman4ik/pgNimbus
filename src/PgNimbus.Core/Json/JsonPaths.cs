using System.Text;
using PgNimbus.Core.Query;

namespace PgNimbus.Core.Json;

/// <summary>
/// Spells the way to a value inside a JSON document, for someone who found it in
/// the cell inspector's tree and wants to use it in a query: as a Postgres
/// expression over the column (<c>metadata-&gt;'items'-&gt;0-&gt;&gt;'sku'</c>) or
/// as an SQL/JSON path (<c>$.items[0].sku</c>) for <c>jsonb_path_query</c> and
/// friends.
/// </summary>
public static class JsonPaths
{
    /// <summary>
    /// A Postgres expression that reads the value at <paramref name="path"/> out
    /// of <paramref name="column"/>: <c>-&gt;</c> for each step, and <c>-&gt;&gt;</c>
    /// for the last one when <paramref name="asText"/> (a scalar, which a
    /// comparison wants as text). <paramref name="castToJsonb"/> is for a column
    /// that holds JSON without being <c>json</c>/<c>jsonb</c>, where the operators
    /// don't exist until it is cast. The column is quoted only when it has to be,
    /// and every key goes through <see cref="SqlLiteral.Quote"/>.
    /// </summary>
    public static string ToSql(string column, bool castToJsonb, IReadOnlyList<JsonPathSegment> path, bool asText)
    {
        var sb = new StringBuilder(SqlIdentifier.QuoteIfNeeded(column));
        if (castToJsonb)
        {
            sb.Append("::jsonb");
        }

        for (var i = 0; i < path.Count; i++)
        {
            sb.Append(asText && i == path.Count - 1 ? "->>" : "->");
            var segment = path[i];
            sb.Append(segment.IsElement
                ? segment.Index.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : SqlLiteral.Quote(segment.Key!));
        }

        return sb.ToString();
    }

    /// <summary>
    /// The SQL/JSON path to <paramref name="path"/>: <c>$</c>, then <c>.key</c>
    /// for a key that is a plain ASCII identifier, <c>."any key"</c> (JSON string
    /// escapes, which the jsonpath grammar shares) for any other, and <c>[i]</c>
    /// for an array element.
    /// </summary>
    public static string ToJsonPath(IReadOnlyList<JsonPathSegment> path)
    {
        var sb = new StringBuilder("$");
        foreach (var segment in path)
        {
            if (segment.IsElement)
            {
                sb.Append('[').Append(segment.Index.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(']');
            }
            else if (IsPlainKey(segment.Key!))
            {
                sb.Append('.').Append(segment.Key);
            }
            else
            {
                sb.Append('.').Append(JsonText.Quote(segment.Key!));
            }
        }

        return sb.ToString();
    }

    private static bool IsPlainKey(string key)
    {
        if (key.Length == 0 || !(char.IsAsciiLetter(key[0]) || key[0] == '_'))
        {
            return false;
        }

        foreach (var c in key)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c == '_'))
            {
                return false;
            }
        }

        return true;
    }
}
