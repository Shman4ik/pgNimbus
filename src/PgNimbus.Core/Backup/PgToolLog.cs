namespace PgNimbus.Core.Backup;

/// <summary>
/// What a pg_dump or pg_restore run wrote to standard error: every line for the
/// log the window shows (the most recent <see cref="MaxLines"/>; a verbose run
/// over a big schema writes one per object), and the errors among them, each
/// with the detail and hint lines that follow it. Thread-safe: the process's
/// output arrives on the thread pool while the window reads it.
/// </summary>
public sealed class PgToolLog
{
    /// <summary>How many lines the log keeps.</summary>
    public const int MaxLines = 5000;

    private readonly Queue<string> _lines = new();
    private readonly List<string> _errors = [];
    private readonly object _gate = new();
    private int _dropped;
    private bool _lastWasError;

    /// <summary>Takes one line of standard error.</summary>
    public void Add(string line)
    {
        lock (_gate)
        {
            _lines.Enqueue(line);
            if (_lines.Count > MaxLines)
            {
                _lines.Dequeue();
                _dropped++;
            }

            var message = Message(line, out var kind);
            switch (kind)
            {
                case LineKind.Error:
                    _errors.Add(message);
                    _lastWasError = true;
                    break;
                case LineKind.Detail when _lastWasError && _errors.Count > 0:
                    _errors[^1] = _errors[^1] + "\n" + message;
                    break;
                case LineKind.Detail:
                    break;
                default:
                    _lastWasError = false;
                    break;
            }
        }
    }

    /// <summary>The errors the run reported, oldest first, without the program's name.</summary>
    public IReadOnlyList<string> Errors
    {
        get
        {
            lock (_gate)
            {
                return [.. _errors];
            }
        }
    }

    /// <summary>The log as text, with a note when the oldest lines were dropped.</summary>
    public string Text
    {
        get
        {
            lock (_gate)
            {
                var lines = _dropped > 0
                    ? new[] { Localization.Strings.Backup_LogDropped(_dropped) }.Concat(_lines)
                    : _lines;
                return string.Join('\n', lines);
            }
        }
    }

    private enum LineKind
    {
        Info,
        Error,
        Detail,
    }

    /// <summary>
    /// Sorts a line: <c>pg_dump: error: …</c> (and <c>pg_restore: error:</c>,
    /// <c>FATAL:</c>) starts an error; <c>…: detail:</c>, <c>…: hint:</c>,
    /// <c>DETAIL:</c>, <c>HINT:</c> and pg_restore's <c>Command was:</c> belong
    /// to the error before them; the rest is progress.
    /// </summary>
    private static string Message(string line, out LineKind kind)
    {
        var text = line.TrimEnd();
        var program = text.IndexOf(": ", StringComparison.Ordinal);
        if (program > 0 && text[..program] is "pg_dump" or "pg_restore" or "pg_dumpall")
        {
            var rest = text[(program + 2)..];
            if (rest.StartsWith("error: ", StringComparison.Ordinal))
            {
                kind = LineKind.Error;
                return rest["error: ".Length..];
            }

            if (rest.StartsWith("detail: ", StringComparison.Ordinal) || rest.StartsWith("hint: ", StringComparison.Ordinal))
            {
                kind = LineKind.Detail;
                return rest;
            }

            kind = LineKind.Info;
            return rest;
        }

        if (text.StartsWith("DETAIL:", StringComparison.Ordinal)
            || text.StartsWith("HINT:", StringComparison.Ordinal)
            || text.StartsWith("Command was:", StringComparison.Ordinal)
            || text.StartsWith("LINE ", StringComparison.Ordinal)
            || (text.Length > 0 && char.IsWhiteSpace(line[0])))
        {
            kind = LineKind.Detail;
            return text.Trim();
        }

        kind = LineKind.Info;
        return text;
    }
}

/// <summary>
/// A sentence saying what to do about the errors pg_dump and pg_restore most
/// often stop with. The program's own message is always shown too; this is
/// the line under it.
/// </summary>
public static class PgToolErrorHints
{
    public static string? For(string? error, PgTool tool, bool tunnelled)
    {
        if (string.IsNullOrEmpty(error))
        {
            return null;
        }

        bool Has(string text) => error.Contains(text, StringComparison.OrdinalIgnoreCase);

        if (Has("password authentication failed") || Has("no password supplied"))
        {
            return Localization.Strings.Backup_HintPassword;
        }

        if (tool == PgTool.PgRestore && Has("role ") && Has("does not exist"))
        {
            return Localization.Strings.Backup_HintRoleMissing;
        }

        // A backup of one schema or table keeps its foreign keys, views and
        // column types that point outside it, and a new database has none of that.
        if (tool == PgTool.PgRestore && Has("does not exist"))
        {
            return Localization.Strings.Backup_HintPointsOutside;
        }

        if (tool == PgTool.PgRestore && Has("must be owner of"))
        {
            return Localization.Strings.Backup_HintMustBeOwner;
        }

        if (tool == PgTool.PgRestore && Has("permission denied"))
        {
            return Localization.Strings.Backup_HintRestorePermission;
        }

        if (Has("server version mismatch"))
        {
            return Localization.Strings.Backup_HintToolTooOld;
        }

        if (Has("permission denied"))
        {
            return Localization.Strings.Backup_HintBackupPermission;
        }

        if (Has("root certificate file") || Has("certificate verify failed") || Has("SSL error"))
        {
            return Localization.Strings.Backup_HintCertificate;
        }

        if (Has("could not connect") || Has("Connection refused") || Has("timeout expired") || Has("could not translate host name"))
        {
            return tunnelled
                ? Localization.Strings.Backup_HintUnreachableTunnel(PgToolInstall.ToolName(tool))
                : Localization.Strings.Backup_HintUnreachable(PgToolInstall.ToolName(tool));
        }

        if (Has("No space left on device") || Has("could not write to output file") || Has("could not open output file"))
        {
            return Localization.Strings.Backup_HintCannotWrite;
        }

        if (tool == PgTool.PgDump && Has("does not exist"))
        {
            return Localization.Strings.Backup_HintObjectGone;
        }

        return null;
    }
}
