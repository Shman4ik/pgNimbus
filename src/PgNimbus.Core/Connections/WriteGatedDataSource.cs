using Npgsql;

namespace PgNimbus.Core.Connections;

/// <summary>
/// The pool every service of one window opens its connections from (ROADMAP T3).
/// Usually one <see cref="NpgsqlDataSource"/> and nothing more, which is why a
/// data source converts to it implicitly. For a production profile it holds two:
/// the read-only pool the window opens with, and a writable one, built the first
/// time the user allows writes for the window.
/// <para>
/// Writes stay the server's to refuse. A session's read-only state is its
/// startup option, which the pool's reset (<c>DISCARD ALL</c>) puts back on
/// every reuse, so a <c>SET</c> could not lift it for long, and a <c>SET</c> on
/// every rental would cost a round trip per statement. Switching pools does
/// neither: <see cref="OpenConnectionAsync"/> rents from the writable pool while
/// writes are allowed and from the read-only one otherwise, whichever service
/// asks (the editor, the grid, an import, a schema or security action).
/// </para>
/// <para>
/// A connection already rented keeps the pool it came from until it is returned:
/// a query still running when writes are locked finishes as it started, and
/// locking clears the writable pool so no idle writable socket is reused. The
/// caller refuses to lock while an explicit transaction holds a connection.
/// </para>
/// </summary>
public sealed class WriteGatedDataSource : IAsyncDisposable, IDisposable
{
    private readonly NpgsqlDataSource _primary;
    private readonly Func<NpgsqlDataSource>? _createWritable;
    private NpgsqlDataSource? _writable;
    private volatile bool _writesAllowed;

    /// <param name="primary">The pool the window opens with.</param>
    /// <param name="createWritable">
    /// Builds the writable pool, or null when this window can never allow writes
    /// for itself (any profile but a production one).
    /// </param>
    public WriteGatedDataSource(NpgsqlDataSource primary, Func<NpgsqlDataSource>? createWritable = null)
    {
        _primary = primary;
        _createWritable = createWritable;
    }

    public static implicit operator WriteGatedDataSource(NpgsqlDataSource dataSource) => new(dataSource);

    /// <summary>True when <see cref="AllowWrites"/> can do anything.</summary>
    public bool CanAllowWrites => _createWritable is not null;

    /// <summary>True while connections come from the writable pool.</summary>
    public bool WritesAllowed => _writesAllowed;

    /// <summary>The read-only pool's connection string (no password).</summary>
    public string ConnectionString => _primary.ConnectionString;

    private NpgsqlDataSource Current => _writesAllowed && _writable is { } writable ? writable : _primary;

    /// <summary>
    /// Rents the next connections from the writable pool, building it the first
    /// time. Opens nothing: the next rental does.
    /// </summary>
    public void AllowWrites()
    {
        if (_createWritable is null)
        {
            throw new InvalidOperationException("This connection cannot allow writes for itself.");
        }

        _writable ??= _createWritable();
        _writesAllowed = true;
    }

    /// <summary>
    /// Rents the next connections from the read-only pool again, and closes the
    /// writable pool's idle connections (those in use close when returned).
    /// </summary>
    public void LockWrites()
    {
        _writesAllowed = false;
        _writable?.Clear();
    }

    public ValueTask<NpgsqlConnection> OpenConnectionAsync(CancellationToken cancellationToken = default) =>
        Current.OpenConnectionAsync(cancellationToken);

    public NpgsqlCommand CreateCommand(string? commandText = null) => Current.CreateCommand(commandText);

    /// <summary>Closes the idle connections of both pools (a connection loss flushes them).</summary>
    public void Clear()
    {
        _primary.Clear();
        _writable?.Clear();
    }

    public void Dispose()
    {
        _primary.Dispose();
        _writable?.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        await _primary.DisposeAsync();
        if (_writable is not null)
        {
            await _writable.DisposeAsync();
        }
    }
}
