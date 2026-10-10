namespace PgNimbus.Core.Backup;

/// <summary>Where a restore goes.</summary>
public enum RestoreTarget
{
    /// <summary>A database the restore creates first, so nothing existing is touched.</summary>
    NewDatabase,

    /// <summary>The window's own database: what the backup holds is dropped and recreated.</summary>
    CurrentDatabase,
}

/// <summary>One restore to run.</summary>
/// <param name="DatabaseName">The database restored into: the new one's name, or the window's.</param>
/// <param name="KeepOwners">
/// Keep the owners and permissions the backup names. Off (<c>--no-owner
/// --no-privileges</c>), everything restored belongs to the role restoring it:
/// what a backup from another server needs when its roles don't exist here,
/// where the first <c>ALTER … OWNER TO</c> would stop the restore.
/// </param>
public sealed record RestorePlan(string ArchivePath, RestoreTarget Target, string DatabaseName, bool KeepOwners)
{
    /// <summary>
    /// pg_restore's arguments. Always one transaction that stops at the first
    /// error (<c>--single-transaction --exit-on-error</c>): a restore either
    /// happens whole or not at all, and a database is never left half restored.
    /// Into the current database it is <c>--clean --if-exists</c>: what the
    /// backup holds is dropped first (only that; other objects stay), and a
    /// thing the database doesn't have yet is no error.
    /// </summary>
    public IReadOnlyList<string> PgRestoreArguments(string connectionString)
    {
        var arguments = new List<string>
        {
            "--dbname=" + connectionString,
            "--verbose",
            "--no-password",
            "--exit-on-error",
            "--single-transaction",
        };

        if (Target == RestoreTarget.CurrentDatabase)
        {
            arguments.Add("--clean");
            arguments.Add("--if-exists");
        }

        if (!KeepOwners)
        {
            arguments.Add("--no-owner");
            arguments.Add("--no-privileges");
        }

        arguments.Add(ArchivePath);
        return arguments;
    }

    /// <summary>The longest database name PostgreSQL keeps (NAMEDATALEN - 1 bytes).</summary>
    public const int MaxDatabaseNameBytes = 63;

    /// <summary>Why <paramref name="name"/> can't be a new database's name, or null.</summary>
    public static string? ValidateDatabaseName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "Give the new database a name.";
        }

        if (name != name.Trim())
        {
            return "The name can't start or end with a space.";
        }

        if (System.Text.Encoding.UTF8.GetByteCount(name) > MaxDatabaseNameBytes)
        {
            return $"The name is too long: PostgreSQL keeps {MaxDatabaseNameBytes} bytes.";
        }

        if (name.Any(char.IsControl))
        {
            return "The name can't hold control characters.";
        }

        return null;
    }
}

/// <summary>Where a restore is.</summary>
public enum RestoreStage
{
    Connecting,
    Dropping,
    Creating,
    RestoringData,
    Finishing,
}

/// <summary>One look at a running restore.</summary>
public sealed record RestoreProgress(RestoreStage Stage, string? CurrentItem, double? Fraction, TimeSpan Elapsed);

/// <summary>
/// Turns pg_restore's <c>--verbose</c> lines into progress: one line per
/// object it drops, creates or fills (<c>creating TABLE "public.customers"</c>,
/// <c>processing data for table "sales.orders"</c>), counted against what the
/// archive's listing says is coming.
/// </summary>
public sealed class PgRestoreProgressTracker
{
    private readonly int _total;
    private int _done;

    /// <param name="listing">The archive's listing, which says how many steps are coming.</param>
    /// <param name="clean">Whether the restore drops what it replaces first, which adds a step per object.</param>
    public PgRestoreProgressTracker(PgArchiveListing listing, bool clean)
    {
        var entries = listing.Entries.Count;
        // Rows, sequence values, permissions and the like are not dropped
        // beforehand; every other object is.
        var notDropped = listing.Entries.Count(e => e.Description is "TABLE DATA" or "SEQUENCE SET" or "ACL"
            or "MATERIALIZED VIEW DATA" or "COMMENT" or "SEQUENCE OWNED BY" or "TABLE ATTACH" or "INDEX ATTACH"
            or "DEFAULT ACL" or "STATISTICS DATA");
        _total = Math.Max(1, entries + (clean ? entries - notDropped : 0));
    }

    public RestoreStage Stage { get; private set; } = RestoreStage.Connecting;

    /// <summary>"Creating TABLE public.customers", "Restoring rows of sales.orders".</summary>
    public string? CurrentItem { get; private set; }

    public double Fraction => Math.Clamp((double)_done / _total, 0, 1);

    /// <summary>Takes one line of pg_restore's standard error; true when the progress changed.</summary>
    public bool Observe(string line)
    {
        const string Prefix = "pg_restore: ";
        if (!line.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var rest = line[Prefix.Length..];
        if (rest.StartsWith("processing data for table ", StringComparison.Ordinal))
        {
            _done++;
            Stage = RestoreStage.RestoringData;
            CurrentItem = "Restoring rows of " + Unquote(rest["processing data for table ".Length..]);
            return true;
        }

        if (rest.StartsWith("creating ", StringComparison.Ordinal))
        {
            _done++;
            Stage = Stage == RestoreStage.RestoringData ? RestoreStage.Finishing : RestoreStage.Creating;
            CurrentItem = "Creating " + Unquote(rest["creating ".Length..]);
            return true;
        }

        if (rest.StartsWith("dropping ", StringComparison.Ordinal))
        {
            _done++;
            Stage = RestoreStage.Dropping;
            CurrentItem = "Dropping " + Unquote(rest["dropping ".Length..]);
            return true;
        }

        if (rest.StartsWith("executing ", StringComparison.Ordinal) && !rest.StartsWith("executing SELECT", StringComparison.Ordinal))
        {
            _done++;
            return true;
        }

        return false;
    }

    /// <summary>The restore finished.</summary>
    public void Finish()
    {
        _done = _total;
        Stage = RestoreStage.Finishing;
        CurrentItem = null;
    }

    public RestoreProgress Snapshot(TimeSpan elapsed) => new(Stage, CurrentItem, Fraction, elapsed);

    // `TABLE "public.customers"` → `TABLE public.customers`; the quotes are
    // pg_restore's own, around the whole name.
    private static string Unquote(string text)
    {
        var quote = text.IndexOf('"');
        return quote >= 0 && text.EndsWith('"') && text.Length > quote + 1
            ? text[..quote] + text[(quote + 1)..^1]
            : text;
    }
}
