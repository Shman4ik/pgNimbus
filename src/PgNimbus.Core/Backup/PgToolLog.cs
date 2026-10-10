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
                    ? new[] { $"({_dropped} earlier lines not kept)" }.Concat(_lines)
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
            return "The server refused the password pgNimbus connected this window with. If it changed since, reconnect and try again.";
        }

        if (tool == PgTool.PgRestore && Has("role ") && Has("does not exist"))
        {
            return "The backup names a role this server doesn't have. Turn off Keep owners and permissions to make you the owner of everything, or create the role first.";
        }

        // A backup of one schema or table keeps its foreign keys, views and
        // column types that point outside it, and a new database has none of that.
        if (tool == PgTool.PgRestore && Has("does not exist"))
        {
            return "The backup refers to something it doesn't contain, such as a table in another schema. A schema or table backup needs what it points to, so restore it into a database that has it, like the one it came from.";
        }

        if (tool == PgTool.PgRestore && Has("must be owner of"))
        {
            return "Something in the backup belongs to a role you can't act for. Turn off Keep owners and permissions, or restore as that role.";
        }

        if (tool == PgTool.PgRestore && Has("permission denied"))
        {
            return "Your role can't create everything the backup holds here. Restore into a new database, which you own, or use a role that can.";
        }

        if (Has("server version mismatch"))
        {
            return "This pg_dump is older than the server. Install a newer one; Settings, on the Data tab, shows which one pgNimbus uses.";
        }

        if (Has("permission denied"))
        {
            return "Your role can't read everything this covers. Use a role that can, or back up a schema or table it can read.";
        }

        if (Has("root certificate file") || Has("certificate verify failed") || Has("SSL error"))
        {
            return "The server's certificate couldn't be checked. Check the connection's SSL settings and root certificate.";
        }

        if (Has("could not connect") || Has("Connection refused") || Has("timeout expired") || Has("could not translate host name"))
        {
            return tunnelled
                ? $"{PgToolInstall.ToolName(tool)} couldn't reach the server through this window's SSH tunnel. Keep the window connected while it runs."
                : $"{PgToolInstall.ToolName(tool)} couldn't reach the server. Check that it's up and that this computer can reach it.";
        }

        if (Has("No space left on device") || Has("could not write to output file") || Has("could not open output file"))
        {
            return "The file couldn't be written. Check that the folder exists, that you can write to it, and that the disk has room.";
        }

        if (tool == PgTool.PgDump && Has("does not exist"))
        {
            return "Something this covers was dropped or renamed since the window loaded it. Refresh the schema and try again.";
        }

        return null;
    }
}
