using PgNimbus.Core.Query;

namespace PgNimbus.Core.Backup;

/// <summary>What a backup covers.</summary>
public enum BackupScopeKind
{
    Database,
    Schema,
    Table,
}

/// <summary>
/// The part of the database a backup saves: all of it, one schema (the schema
/// tree's "Back Up Schema…") or one table ("Back Up Table…").
/// </summary>
public sealed record BackupScope(BackupScopeKind Kind, string? Schema = null, string? Table = null, bool IsPartitioned = false)
{
    public static BackupScope Database { get; } = new(BackupScopeKind.Database);

    public static BackupScope ForSchema(string schema) => new(BackupScopeKind.Schema, schema);

    public static BackupScope ForTable(string schema, string table, bool isPartitioned) =>
        new(BackupScopeKind.Table, schema, table, isPartitioned);

    /// <summary>"shop", "schema sales", "table sales.orders": what the window's title names.</summary>
    public string Describe(string database) => Kind switch
    {
        BackupScopeKind.Schema => $"schema {Schema}",
        BackupScopeKind.Table => $"table {Schema}.{Table}",
        _ => database,
    };

    /// <summary>
    /// The suggested file name, without the extension: the database, the schema
    /// or the table, and the time, so a second backup never lands on the first
    /// (<c>shop_sales.orders_2026-10-10_1432</c>). Characters no file system
    /// takes are replaced.
    /// </summary>
    public string SuggestedFileName(string database, DateTime now)
    {
        var name = Kind switch
        {
            BackupScopeKind.Schema => $"{database}_{Schema}",
            BackupScopeKind.Table => $"{database}_{Schema}.{Table}",
            _ => database,
        };
        return $"{SafeFileName(name)}_{now:yyyy-MM-dd_HHmm}";
    }

    private static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(['<', '>', ':', '"', '/', '\\', '|', '?', '*']).ToHashSet();
        var chars = name.Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray();
        var safe = new string(chars).Trim().TrimEnd('.');
        return safe.Length == 0 ? "backup" : safe.Length > 120 ? safe[..120] : safe;
    }
}

/// <summary>What a backup saves.</summary>
public enum BackupContent
{
    /// <summary>The structure and every row.</summary>
    Everything,

    /// <summary>The structure alone: tables, views, functions, types, permissions, no rows.</summary>
    StructureOnly,
}

/// <summary>The kind of file a backup writes, which follows the file name the user chose.</summary>
public enum BackupFormat
{
    /// <summary>pg_dump's custom archive (<c>.dump</c>): compressed, and what pgNimbus restores.</summary>
    Archive,

    /// <summary>A plain SQL script (<c>.sql</c>): readable and diffable, restored with psql.</summary>
    SqlScript,
}

/// <summary>One backup to run.</summary>
public sealed record BackupPlan(BackupScope Scope, BackupContent Content, string OutputPath)
{
    /// <summary>A <c>.sql</c> file is a script; any other name gets the archive.</summary>
    public BackupFormat Format => FormatFor(OutputPath);

    public static BackupFormat FormatFor(string path) =>
        string.Equals(Path.GetExtension(path), ".sql", StringComparison.OrdinalIgnoreCase)
            ? BackupFormat.SqlScript
            : BackupFormat.Archive;

    /// <summary>
    /// Where pg_dump writes while it runs. The chosen file is replaced only once
    /// the dump has finished, so a failed or stopped backup never destroys an
    /// older backup the user saved over.
    /// </summary>
    public string PartialPath => OutputPath + ".partial";

    /// <summary>
    /// pg_dump's arguments, the connection string last (pg_dump reads a
    /// positional argument containing <c>=</c> as one).
    /// </summary>
    /// <param name="tool">The pg_dump that will run, which decides how a partitioned table is named.</param>
    /// <param name="partitions">
    /// A partitioned table's partitions, for a pg_dump older than 16, which has no
    /// <c>--table-and-children</c> and would otherwise save the parent's
    /// definition and none of its rows.
    /// </param>
    public IReadOnlyList<string> PgDumpArguments(PgVersion tool, string connectionString, IReadOnlyList<BackupTable>? partitions = null)
    {
        var arguments = new List<string>
        {
            Format == BackupFormat.SqlScript ? "--format=plain" : "--format=custom",
            "--file=" + PartialPath,
            "--verbose",
            "--no-password",
        };

        if (Content == BackupContent.StructureOnly)
        {
            arguments.Add("--schema-only");
        }

        switch (Scope.Kind)
        {
            case BackupScopeKind.Schema:
                arguments.Add("--schema=" + ExactPattern(Scope.Schema!));
                break;
            case BackupScopeKind.Table when Scope.IsPartitioned && tool.Major >= 16:
                arguments.Add("--table-and-children=" + ExactPattern(Scope.Schema!, Scope.Table!));
                break;
            case BackupScopeKind.Table:
                arguments.Add("--table=" + ExactPattern(Scope.Schema!, Scope.Table!));
                if (Scope.IsPartitioned)
                {
                    foreach (var partition in partitions ?? [])
                    {
                        arguments.Add("--table=" + ExactPattern(partition.Schema, partition.Name));
                    }
                }

                break;
        }

        arguments.Add(connectionString);
        return arguments;
    }

    /// <summary>
    /// A pg_dump pattern that matches exactly this name. pg_dump reads <c>-n</c>
    /// and <c>-t</c> as psql patterns, where <c>*</c>, <c>?</c> and <c>.</c> are
    /// special and unquoted letters fold to lower case; inside double quotes
    /// every character is taken literally and <c>""</c> is a quote, which is
    /// exactly identifier quoting. So <c>Odd "Schema" *?</c> becomes
    /// <c>"Odd ""Schema"" *?"</c> and matches that schema alone.
    /// </summary>
    public static string ExactPattern(string schema, string? table = null) =>
        table is null
            ? SqlIdentifier.Quote(schema)
            : SqlIdentifier.Quote(schema) + "." + SqlIdentifier.Quote(table);
}

/// <summary>A table whose rows a backup saves, with its on-disk size for the progress bar.</summary>
public sealed record BackupTable(string Schema, string Name, long Bytes)
{
    /// <summary>The name as pg_dump's progress lines write it: <c>schema.table</c>, unquoted.</summary>
    public string DumpName => Schema + "." + Name;
}
