using System.Text;
using PgNimbus.Core.Query;

namespace PgNimbus.Core.Schema;

/// <summary>The five actions on a relation's Maintenance submenu and their palette rows.</summary>
public enum TableMaintenanceAction
{
    Analyze,
    Vacuum,
    Truncate,
    Rename,
    Drop,
}

/// <summary>
/// The statements behind a relation's Maintenance submenu, and which relation
/// kinds each one is offered for. Core-pure: nothing here runs anything.
/// <para>
/// Every statement is the plain form PostgreSQL runs by default: VACUUM without
/// FULL, TRUNCATE and DROP without CASCADE (so a relation another object depends
/// on is refused by the server, with its own message), no RESTART IDENTITY.
/// Names are always quoted through <see cref="SqlIdentifier.Quote"/>, and none
/// of these carries a comment. They are statements the user asked for, so they
/// are not tagged with <see cref="InternalSql.Marker"/>.
/// </para>
/// </summary>
public static class TableMaintenance
{
    /// <summary>PostgreSQL's NAMEDATALEN − 1: a longer name is cut silently by the server, with only a NOTICE.</summary>
    public const int MaxIdentifierBytes = 63;

    /// <summary>
    /// Whether <paramref name="action"/> is valid for a relation of <paramref name="kind"/>.
    /// ANALYZE and VACUUM skip a plain view with a warning (it has no storage),
    /// TRUNCATE refuses anything but a table, and RENAME and DROP have a form
    /// for every kind (ALTER/DROP VIEW, MATERIALIZED VIEW).
    /// </summary>
    public static bool Applies(TableMaintenanceAction action, RelationKind kind) => action switch
    {
        TableMaintenanceAction.Analyze or TableMaintenanceAction.Vacuum =>
            kind is RelationKind.Table or RelationKind.PartitionedTable or RelationKind.MaterializedView,
        TableMaintenanceAction.Truncate => kind is RelationKind.Table or RelationKind.PartitionedTable,
        TableMaintenanceAction.Rename or TableMaintenanceAction.Drop => true,
        _ => false,
    };

    /// <summary>True for the actions that change data or the catalog (and so are refused on a read-only connection).</summary>
    public static bool Writes(TableMaintenanceAction action) =>
        action is TableMaintenanceAction.Truncate or TableMaintenanceAction.Rename or TableMaintenanceAction.Drop;

    /// <summary>The keyword ALTER and DROP take for <paramref name="kind"/>.</summary>
    public static string ObjectKeyword(RelationKind kind) => kind switch
    {
        RelationKind.View => "VIEW",
        RelationKind.MaterializedView => "MATERIALIZED VIEW",
        _ => "TABLE",
    };

    /// <summary>The noun the UI uses for <paramref name="kind"/>: "table", "view", "materialized view".</summary>
    public static string Noun(RelationKind kind) => kind switch
    {
        RelationKind.View => "view",
        RelationKind.MaterializedView => "materialized view",
        _ => "table",
    };

    /// <summary><c>"schema"."name"</c>, both parts quoted.</summary>
    public static string QualifiedName(string schema, string name) =>
        $"{SqlIdentifier.Quote(schema)}.{SqlIdentifier.Quote(name)}";

    public static string Analyze(string schema, string name) => $"ANALYZE {QualifiedName(schema, name)}";

    /// <summary>Plain VACUUM. It cannot run inside a transaction block, so the caller sends it alone, in autocommit.</summary>
    public static string Vacuum(string schema, string name) => $"VACUUM {QualifiedName(schema, name)}";

    /// <summary>TRUNCATE in its default RESTRICT form: a table another table's foreign key references is refused.</summary>
    public static string Truncate(string schema, string name) => $"TRUNCATE TABLE {QualifiedName(schema, name)}";

    public static string Rename(string schema, string name, RelationKind kind, string newName) =>
        $"ALTER {ObjectKeyword(kind)} {QualifiedName(schema, name)} RENAME TO {SqlIdentifier.Quote(newName)}";

    /// <summary>DROP in its default RESTRICT form: a relation a view or a foreign key depends on is refused.</summary>
    public static string Drop(string schema, string name, RelationKind kind) =>
        $"DROP {ObjectKeyword(kind)} {QualifiedName(schema, name)}";

    /// <summary>
    /// The statement for <paramref name="action"/>; <paramref name="newName"/> is
    /// required for Rename and ignored otherwise.
    /// </summary>
    public static string For(TableMaintenanceAction action, string schema, string name, RelationKind kind, string? newName = null) => action switch
    {
        TableMaintenanceAction.Analyze => Analyze(schema, name),
        TableMaintenanceAction.Vacuum => Vacuum(schema, name),
        TableMaintenanceAction.Truncate => Truncate(schema, name),
        TableMaintenanceAction.Rename => Rename(schema, name, kind, newName ?? throw new ArgumentNullException(nameof(newName))),
        TableMaintenanceAction.Drop => Drop(schema, name, kind),
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, null),
    };

    /// <summary>
    /// Why <paramref name="newName"/> can't be used, or null when it can. Only
    /// what the server would otherwise get wrong silently: an empty name, or one
    /// longer than <see cref="MaxIdentifierBytes"/>, which PostgreSQL cuts short
    /// with a NOTICE nobody sees. A taken name is the server's to refuse.
    /// </summary>
    public static string? CheckNewName(string newName)
    {
        if (string.IsNullOrWhiteSpace(newName))
        {
            return "The new name is empty.";
        }

        if (newName.Contains('\0'))
        {
            return "A name can't contain a NUL character.";
        }

        return Encoding.UTF8.GetByteCount(newName) > MaxIdentifierBytes
            ? $"The new name is longer than {MaxIdentifierBytes} bytes, and PostgreSQL would cut it short."
            : null;
    }
}
