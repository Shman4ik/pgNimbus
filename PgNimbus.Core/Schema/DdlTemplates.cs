using PgNimbus.Core.Query;

namespace PgNimbus.Core.Schema;

/// <summary>
/// Starter DDL the UI drops into a new query tab. Deliberately templates, not
/// dialogs: pgNimbus is a SQL-first client, and a "New table…" form that can
/// only express the column types a combo box lists is a worse tool than the
/// statement itself, sitting in the editor where it can be edited and run.
/// Pure and unit-tested — the only catalog input is a name, quoted through
/// <see cref="SqlIdentifier"/> rather than concatenated raw.
/// </summary>
public static class DdlTemplates
{
    /// <summary>
    /// A CREATE TABLE skeleton for <paramref name="schema"/> — an identity
    /// primary key, one NOT NULL text column, and a <c>created_at</c> default,
    /// which is what most new tables start as anyway. The placeholder name is
    /// <c>new_table</c>; the caret work (selecting it) is the view's business.
    /// </summary>
    public static string NewTable(string schema) =>
        $"""
        CREATE TABLE {SqlIdentifier.QuoteIfNeeded(schema)}.new_table (
            id          bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            name        text NOT NULL,
            created_at  timestamptz NOT NULL DEFAULT now()
        );
        """;
}
