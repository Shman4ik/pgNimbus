using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using PgNimbus.Core.Connections;
using PgNimbus.Core.Security;
using PgNimbus.Core.Settings;

namespace PgNimbus.Core.Query;

/// <summary>
/// Persists the last <see cref="MaxEntries"/> executions, most recent first,
/// through <see cref="AppDataFile"/>. Every statement's text is kept as run,
/// values included, in a plain file; the one thing taken out is secrets: every
/// write passes each entry through <see cref="SecretRedactor"/>, and so does
/// every read, which rewrites the file when an entry written before the
/// redactor (or before it knew a shape) still held one. That makes the store
/// itself the choke point, not the view model that happens to call it today.
/// The result line is redacted too (<see cref="Redact(QueryHistoryEntry)"/>).
/// </summary>
public sealed class QueryHistoryStore(string? filePath = null)
{
    /// <summary>How many entries are kept; pinned ones on top of that (see <see cref="Trim"/>).</summary>
    public const int MaxEntries = 200;

    // One lock for every store over every file: two windows each hold a store
    // over the same history.json, and a write is a read, a change and a rename.
    private static readonly Lock FileGate = new();

    // The writes queued by the *InBackground methods, run one after another.
    private static readonly Lock QueueGate = new();
    private static Task s_writes = Task.CompletedTask;

    /// <summary>
    /// The result line kept for a statement that held a password, in place of
    /// the one it produced.
    /// </summary>
    public const string WithheldSummary = "result not kept: the statement held a password";

    private readonly string? _filePath = filePath ?? AppDataPaths.Resolve("history.json");

    /// <summary>The file this store reads and writes; null when the app has no data directory (then nothing is kept between sessions).</summary>
    public string? FilePath => _filePath;

    /// <summary>
    /// The history, secrets redacted. When the file held an entry that still
    /// had one (the security audit of 2026-09 found history files from before
    /// the redactor, and shapes it missed), the scrubbed list is written back
    /// at once, so the plaintext does not outlive the first launch that can
    /// read it. A redacted entry reads as clean, so this rewrites only once.
    /// </summary>
    public IReadOnlyList<QueryHistoryEntry> Load()
    {
        lock (FileGate)
        {
            return LoadLocked();
        }
    }

    private List<QueryHistoryEntry> LoadLocked()
    {
        var entries = Read();
        var scrubbed = false;
        for (var i = 0; i < entries.Count; i++)
        {
            var redacted = Redact(entries[i]);
            if (!ReferenceEquals(redacted, entries[i]))
            {
                entries[i] = redacted;
                scrubbed = true;
            }
        }

        if (scrubbed)
        {
            try
            {
                Write(entries);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // The list in memory is clean either way; the next save retries.
            }
        }

        return entries;
    }

    // No file, an unreadable one, or one that does not parse (moved aside
    // first by AppDataFile) all read as an empty history: a store must never
    // block startup over its own file.
    private List<QueryHistoryEntry> Read() =>
        AppDataFile.ReadJson(_filePath, QueryHistoryJsonContext.Default.ListQueryHistoryEntry) ?? [];

    /// <summary>
    /// Adds <paramref name="entry"/> in front of what the file holds (read again,
    /// so what another window added since is kept) and writes the result, trimmed
    /// (<see cref="Trim"/>). Every entry is redacted on the way out.
    /// </summary>
    public void Append(QueryHistoryEntry entry)
    {
        lock (FileGate)
        {
            SaveLocked(Trim([entry, .. Read()]));
        }
    }

    public void Clear() => Save([]);

    /// <summary>Writes <paramref name="entries"/>, each one redacted first.</summary>
    public void Save(IReadOnlyList<QueryHistoryEntry> entries)
    {
        lock (FileGate)
        {
            SaveLocked(entries);
        }
    }

    private void SaveLocked(IReadOnlyList<QueryHistoryEntry> entries) => Write([.. entries.Select(Redact)]);

    /// <summary>
    /// <see cref="Append"/> on the thread pool, after every write queued before it.
    /// The history is written after every run, and it holds each statement whole,
    /// redacted entry by entry: done on the UI thread, a history holding a few
    /// large scripts added that much to every later run. The task never faults;
    /// a write that fails is dropped like a failed save always was.
    /// </summary>
    public Task AppendInBackground(QueryHistoryEntry entry) => Enqueue(() => Append(entry));

    /// <summary><see cref="Save"/> on the thread pool, in order with <see cref="AppendInBackground"/>.</summary>
    public Task SaveInBackground(IReadOnlyList<QueryHistoryEntry> entries) => Enqueue(() => Save(entries));

    private static Task Enqueue(Action write)
    {
        lock (QueueGate)
        {
            return s_writes = s_writes.ContinueWith(
                _ =>
                {
                    try
                    {
                        write();
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                        // History is best-effort: the list on screen stays right, and
                        // the next write carries it to the file.
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default);
        }
    }

    /// <summary>
    /// <paramref name="newestFirst"/> cut to <see cref="MaxEntries"/>, oldest first,
    /// but never a pinned entry: pinning is the "keep this around" signal, so the
    /// cap only evicts unpinned ones. The one rule for the file and the list on screen.
    /// </summary>
    public static List<QueryHistoryEntry> Trim(IEnumerable<QueryHistoryEntry> newestFirst)
    {
        var entries = newestFirst.ToList();
        var excess = entries.Count - MaxEntries;
        if (excess <= 0)
        {
            return entries;
        }

        var kept = new List<QueryHistoryEntry>(MaxEntries);
        for (var i = entries.Count - 1; i >= 0; i--)
        {
            if (excess > 0 && !entries[i].Pinned)
            {
                excess--;
                continue;
            }

            kept.Add(entries[i]);
        }

        kept.Reverse();
        return kept;
    }

    /// <summary>
    /// <paramref name="entry"/> with its secrets taken out and stamped as
    /// redacted, or the same instance when it already carries this redactor's
    /// stamp for exactly its text. The text goes through
    /// <see cref="SecretRedactor"/>, and so does the result line, except for a
    /// statement that held a password: its result line is replaced by
    /// <see cref="WithheldSummary"/>. An error message quotes the part of the
    /// statement it failed on (<c>syntax error at or near "…"</c>, a conninfo
    /// <c>missing "=" after "…"</c>) with no keyword beside it that says it is a
    /// secret, so the redactor cannot find it there. A statement "held a
    /// password" when its redacted text carries the redactor's marker, which
    /// also covers entries redacted before this existed, on their next load.
    /// </summary>
    /// <remarks>
    /// The stamp (<see cref="QueryHistoryEntry.Redacted"/>) is why a load or an
    /// append no longer redacts the whole file again: the history keeps each
    /// statement whole, and with a few large scripts in it that was a tenth of a
    /// second on every window open and every run. It is
    /// <see cref="SecretRedactor.Version"/> plus a SHA-256 of the text and result
    /// line, so an entry whose text changed after it was stamped (a <c>with</c>,
    /// a hand-edited file) no longer matches and is redacted again, and so is
    /// every entry once the redactor's version is raised.
    /// </remarks>
    public static QueryHistoryEntry Redact(QueryHistoryEntry entry)
    {
        if (entry.Redacted is { } stamp && string.Equals(stamp, StampFor(entry), StringComparison.Ordinal))
        {
            return entry;
        }

        var sql = SecretRedactor.Redact(entry.Sql);
        // Sql is null only in a hand-edited file; that entry has nothing to hide.
        var summary = entry.Sql is not null && sql.Contains(SecretRedactor.ValueReplacement, StringComparison.Ordinal)
            ? WithheldSummary
            : SecretRedactor.Redact(entry.Summary);

        var redacted = entry with { Sql = sql, Summary = summary };
        return redacted with { Redacted = StampFor(redacted) };
    }

    // "<redactor version>:<first 16 bytes of SHA-256 over text, NUL, result line>", hex.
    private static string StampFor(QueryHistoryEntry entry)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(MemoryMarshal.AsBytes((entry.Sql ?? "").AsSpan()));
        hash.AppendData([0, 0]);
        hash.AppendData(MemoryMarshal.AsBytes((entry.Summary ?? "").AsSpan()));
        Span<byte> digest = stackalloc byte[32];
        hash.GetHashAndReset(digest);
        return $"{SecretRedactor.Version}:{Convert.ToHexStringLower(digest[..16])}";
    }

    private void Write(List<QueryHistoryEntry> entries) =>
        AppDataFile.WriteJson(_filePath, entries, QueryHistoryJsonContext.Default.ListQueryHistoryEntry);
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(List<QueryHistoryEntry>))]
internal sealed partial class QueryHistoryJsonContext : JsonSerializerContext;
