namespace PgNimbus.Core;

/// <summary>
/// Marks the SQL pgNimbus sends on its own behalf (catalog reads for the schema
/// tree and completion, monitoring, permissions) so it can be told apart from
/// what a user ran. pg_stat_statements keeps a statement's leading comment in the
/// text it stores, so the slow-query shortlist can leave these out: before the
/// mark, the first rows a user saw there were the app's own completion-catalog
/// reads (found in the 0.14.0 release pass).
///
/// Only SQL the user did not write or ask for gets it. Their queries, browse
/// pages, EXPLAINs, imports and schema actions are theirs and stay unmarked.
/// </summary>
public static class InternalSql
{
    /// <summary>The comment that opens every internal statement.</summary>
    public const string Marker = "/* pgNimbus */";

    /// <summary>Prefixes <paramref name="sql"/> with <see cref="Marker"/>.</summary>
    public static string Tag(string sql) => Marker + " " + sql;

    /// <summary>True when <paramref name="text"/> (as pg_stat_statements stored it) is one of ours.</summary>
    public static bool IsTagged(string text) => text.StartsWith(Marker, StringComparison.Ordinal);
}
