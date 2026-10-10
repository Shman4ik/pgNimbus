using System.Text;
using System.Text.RegularExpressions;

namespace PgNimbus.Core.Backup;

/// <summary>What a file chosen for a restore is.</summary>
public enum PgArchiveFormat
{
    /// <summary>The file isn't there.</summary>
    Missing,

    /// <summary>pg_dump's custom archive (<c>-Fc</c>), what a pgNimbus backup writes.</summary>
    Custom,

    /// <summary>pg_dump's tar archive (<c>-Ft</c>).</summary>
    Tar,

    /// <summary>pg_dump's directory archive (<c>-Fd</c>): a folder with a <c>toc.dat</c>.</summary>
    Directory,

    /// <summary>A plain SQL script (pg_dump's default format), which only psql restores.</summary>
    PlainSql,

    /// <summary>Anything else.</summary>
    Unknown,
}

/// <summary>One object in an archive, as <c>pg_restore --list</c> names it.</summary>
/// <param name="Description">The kind: <c>TABLE</c>, <c>TABLE DATA</c>, <c>FK CONSTRAINT</c>, <c>ACL</c> …</param>
/// <param name="Owner">The role that owned it when it was saved, or empty for an entry with no owner.</param>
public sealed record PgArchiveEntry(int Id, string Description, string Owner);

/// <summary>
/// What <c>pg_restore --list</c> says an archive holds: the header (the
/// database it was saved from, both versions, when) and one entry per object.
/// The restore window shows the header so the file can be recognised before
/// anything runs, and checks the owners against the server's roles.
/// </summary>
public sealed record PgArchiveListing(
    string? DatabaseName,
    string? CreatedAt,
    string? Format,
    PgVersion? DumpedFrom,
    PgVersion? DumpedBy,
    IReadOnlyList<PgArchiveEntry> Entries)
{
    // "3457; 0 16390 TABLE DATA public customers app": the dump id, the
    // catalog and object oids, then the kind, schema, name and owner.
    private static readonly Regex EntryLine = new(
        @"^(?<id>\d+);\s+\d+\s+\d+\s+(?<rest>.*)$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
        TimeSpan.FromSeconds(1));

    /// <summary>
    /// Every kind of entry pg_dump writes, longest first, so <c>TABLE DATA</c>
    /// is read as itself and not as a <c>TABLE</c> named "DATA". The kind is
    /// the only part of an entry line that can be told apart reliably: schema
    /// and object names are printed raw and may hold spaces.
    /// </summary>
    private static readonly string[] Descriptions =
    [
        "PUBLICATION TABLES IN SCHEMA", "TEXT SEARCH CONFIGURATION", "TEXT SEARCH DICTIONARY",
        "TEXT SEARCH TEMPLATE", "TEXT SEARCH PARSER", "MATERIALIZED VIEW DATA", "FOREIGN DATA WRAPPER",
        "DATABASE PROPERTIES", "PROCEDURAL LANGUAGE", "SUBSCRIPTION TABLE", "PUBLICATION TABLE",
        "SEQUENCE OWNED BY", "MATERIALIZED VIEW", "CHECK CONSTRAINT", "OPERATOR FAMILY", "OPERATOR CLASS",
        "STATISTICS DATA", "SECURITY LABEL", "FOREIGN SERVER", "EVENT TRIGGER", "LARGE OBJECTS",
        "ACCESS METHOD", "BLOB METADATA", "FOREIGN TABLE", "FK CONSTRAINT", "INDEX ATTACH", "LARGE OBJECT",
        "ROW SECURITY", "SEQUENCE SET", "TABLE ATTACH", "USER MAPPING", "SUBSCRIPTION", "DEFAULT ACL",
        "PUBLICATION", "SHELL TYPE", "STATISTICS", "TABLE DATA", "CONVERSION", "CONSTRAINT", "SEARCHPATH",
        "STDSTRINGS", "COLLATION", "AGGREGATE", "PROCEDURE", "EXTENSION", "TRANSFORM", "OPERATOR",
        "SEQUENCE", "FUNCTION", "ENCODING", "DATABASE", "TRIGGER", "DEFAULT", "COMMENT", "POLICY",
        "SCHEMA", "DOMAIN", "SERVER", "BLOBS", "INDEX", "TABLE", "RULE", "VIEW", "TYPE", "CAST", "BLOB", "ACL",
    ];

    /// <summary>Tables the archive creates.</summary>
    public int TableCount => Entries.Count(e => e.Description is "TABLE" or "FOREIGN TABLE");

    /// <summary>Tables whose rows it holds.</summary>
    public int TableDataCount => Entries.Count(e => e.Description == "TABLE DATA");

    /// <summary>False for a structure-only backup.</summary>
    public bool HasRows => TableDataCount > 0 || Entries.Any(e => e.Description is "BLOBS" or "LARGE OBJECTS" or "BLOB");

    /// <summary>The roles that owned what the archive holds, each once, in name order.</summary>
    public IReadOnlyList<string> Owners =>
        [.. Entries.Select(e => e.Owner).Where(o => o.Length > 0).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

    /// <summary>
    /// Reads <c>pg_restore --list</c> output. Lines it doesn't recognise are
    /// skipped rather than failing the listing: the header's wording has
    /// changed between versions, and the entries alone still say what a restore
    /// would do.
    /// </summary>
    public static PgArchiveListing Parse(string text)
    {
        string? database = null, created = null, format = null;
        PgVersion? from = null, by = null;
        var entries = new List<PgArchiveEntry>();

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith(';'))
            {
                var header = line.TrimStart(';').Trim();
                if (Value(header, "dbname:") is { } name)
                {
                    database = name;
                }
                else if (Value(header, "Archive created at") is { } at)
                {
                    created = at;
                }
                else if (Value(header, "Format:") is { } f)
                {
                    format = f;
                }
                else if (Value(header, "Dumped from database version:") is { } server && PgVersion.TryParse(server, out var s))
                {
                    from = s;
                }
                else if (Value(header, "Dumped by pg_dump version:") is { } tool && PgVersion.TryParse(tool, out var t))
                {
                    by = t;
                }

                continue;
            }

            var match = EntryLine.Match(line);
            if (!match.Success || !int.TryParse(match.Groups["id"].Value, out var id))
            {
                continue;
            }

            var rest = match.Groups["rest"].Value;
            var description = Descriptions.FirstOrDefault(d =>
                rest.StartsWith(d, StringComparison.Ordinal) && (rest.Length == d.Length || rest[d.Length] == ' '));
            if (description is null)
            {
                continue;
            }

            // The owner is the last word; an entry with none (a comment on an
            // extension, the encoding) ends in a space.
            var owner = rest.EndsWith(' ') || rest.Length == description.Length
                ? ""
                : rest[(rest.LastIndexOf(' ') + 1)..];
            entries.Add(new PgArchiveEntry(id, description, owner));
        }

        return new PgArchiveListing(database, created, format, from, by, entries);
    }

    private static string? Value(string header, string key) =>
        header.StartsWith(key, StringComparison.Ordinal) ? header[key.Length..].Trim() : null;
}

/// <summary>Tells archive formats apart by their first bytes.</summary>
public static class PgArchive
{
    /// <summary>
    /// What <paramref name="path"/> is. A custom archive starts with
    /// <c>PGDMP</c>; a tar archive has <c>ustar</c> at byte 257 (and pg_dump's
    /// holds a <c>toc.dat</c>); a directory archive is a folder with a
    /// <c>toc.dat</c>. A file of text is taken for a SQL script: pg_dump's
    /// plain output starts with <c>--</c> comments.
    /// </summary>
    public static PgArchiveFormat Detect(string path)
    {
        if (Directory.Exists(path))
        {
            return File.Exists(Path.Combine(path, "toc.dat")) ? PgArchiveFormat.Directory : PgArchiveFormat.Unknown;
        }

        if (!File.Exists(path))
        {
            return PgArchiveFormat.Missing;
        }

        var head = new byte[512];
        int read;
        try
        {
            using var stream = File.OpenRead(path);
            read = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return PgArchiveFormat.Unknown;
        }

        return Detect(head.AsSpan(0, read));
    }

    /// <summary>The same, from a file's first bytes (up to 512).</summary>
    public static PgArchiveFormat Detect(ReadOnlySpan<byte> head)
    {
        if (head.StartsWith("PGDMP"u8))
        {
            return PgArchiveFormat.Custom;
        }

        if (head.Length >= 262 && head.Slice(257, 5).SequenceEqual("ustar"u8))
        {
            return PgArchiveFormat.Tar;
        }

        if (head.Length == 0)
        {
            return PgArchiveFormat.Unknown;
        }

        // Text: no NUL bytes, and (past a UTF-8 BOM) a comment, a statement or a
        // psql meta-command at the start, as every plain dump has.
        if (head.IndexOf((byte)0) >= 0)
        {
            return PgArchiveFormat.Unknown;
        }

        var text = Encoding.UTF8.GetString(head).TrimStart('﻿').TrimStart();
        return text.StartsWith("--", StringComparison.Ordinal)
               || text.StartsWith('\\')
               || text.StartsWith("SET ", StringComparison.OrdinalIgnoreCase)
               || text.StartsWith("CREATE ", StringComparison.OrdinalIgnoreCase)
               || text.StartsWith("BEGIN", StringComparison.OrdinalIgnoreCase)
               || text.StartsWith("/*", StringComparison.Ordinal)
            ? PgArchiveFormat.PlainSql
            : PgArchiveFormat.Unknown;
    }
}
