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
    private const int MaxEntries = 200;

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

    public void Append(QueryHistoryEntry entry)
    {
        var entries = Load().ToList();
        entries.Insert(0, entry);

        // Trim oldest-first, but never a pinned entry - pinning is the
        // "keep this around" signal, so the cap only evicts unpinned ones.
        for (var i = entries.Count - 1; i >= 0 && entries.Count > MaxEntries; i--)
        {
            if (!entries[i].Pinned)
            {
                entries.RemoveAt(i);
            }
        }

        Save(entries);
    }

    public void Clear() => Save([]);

    /// <summary>Writes <paramref name="entries"/>, each one redacted first.</summary>
    public void Save(IReadOnlyList<QueryHistoryEntry> entries) =>
        Write([.. entries.Select(Redact)]);

    /// <summary>
    /// <paramref name="entry"/> with its secrets taken out, or the same
    /// instance when there are none. The text goes through
    /// <see cref="SecretRedactor"/>, and so does the result line, except for a
    /// statement that held a password: its result line is replaced by
    /// <see cref="WithheldSummary"/>. An error message quotes the part of the
    /// statement it failed on (<c>syntax error at or near "…"</c>, a conninfo
    /// <c>missing "=" after "…"</c>) with no keyword beside it that says it is a
    /// secret, so the redactor cannot find it there. A statement "held a
    /// password" when its redacted text carries the redactor's marker, which
    /// also covers entries redacted before this existed, on their next load.
    /// </summary>
    public static QueryHistoryEntry Redact(QueryHistoryEntry entry)
    {
        var sql = SecretRedactor.Redact(entry.Sql);
        // Sql is null only in a hand-edited file; that entry has nothing to hide.
        var summary = entry.Sql is not null && sql.Contains(SecretRedactor.ValueReplacement, StringComparison.Ordinal)
            ? WithheldSummary
            : SecretRedactor.Redact(entry.Summary);

        return string.Equals(sql, entry.Sql, StringComparison.Ordinal) && string.Equals(summary, entry.Summary, StringComparison.Ordinal)
            ? entry
            : entry with { Sql = sql, Summary = summary };
    }

    private void Write(List<QueryHistoryEntry> entries) =>
        AppDataFile.WriteJson(_filePath, entries, QueryHistoryJsonContext.Default.ListQueryHistoryEntry);
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(List<QueryHistoryEntry>))]
internal sealed partial class QueryHistoryJsonContext : JsonSerializerContext;
