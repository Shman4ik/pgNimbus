using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace PgNimbus.Core.Backup;

/// <summary>
/// One run of a PostgreSQL client program: the executable, its arguments, and
/// the password it needs, which never goes into <see cref="Arguments"/>. Any
/// user on the machine can read a process's command line (<c>ps</c>, Task
/// Manager), while its environment is readable only by the same user and root.
/// </summary>
public sealed record PgToolInvocation(string FileName, IReadOnlyList<string> Arguments, IReadOnlyList<string> ExtraPath, string? Password = null);

/// <summary>What a program printed, for the short runs (<c>--version</c>, <c>--list</c>).</summary>
public sealed record PgToolOutput(int ExitCode, string StandardOutput, string StandardError);

/// <summary>
/// Starts PostgreSQL's client programs. Everything a run depends on is set
/// here rather than inherited from whoever started pgNimbus.
/// </summary>
public static class PgToolProcess
{
    /// <summary>
    /// The libpq environment variables a run keeps from the user's environment:
    /// where the password file and the client certificate are. Npgsql reads the
    /// same ones, so the child connects the way the app's own sessions do. Every
    /// other <c>PG*</c> variable is dropped: <c>PGOPTIONS</c> with a
    /// <c>statement_timeout</c> would cut a long dump short, <c>PGSERVICE</c> or
    /// <c>PGTARGETSESSIONATTRS</c> would change what it connects to, and none of
    /// them is part of the profile the user picked.
    /// </summary>
    internal static readonly IReadOnlySet<string> KeptLibpqVariables = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "PGPASSFILE",
        "PGSSLCERT",
        "PGSSLKEY",
        "PGSSLROOTCERT",
        "PGSSLCRL",
        "PGSSLCRLDIR",
    };

    /// <summary>
    /// Rewrites a child's environment for a run: drops the inherited libpq
    /// variables (<see cref="KeptLibpqVariables"/> aside), sets the password,
    /// puts <paramref name="extraPath"/> in front of <c>PATH</c>, and asks for
    /// untranslated messages. The progress and error readers match pg_dump's
    /// English text; a German or Russian Windows would otherwise get its
    /// messages in that language through libintl, and the progress bar would
    /// never move. <c>LC_ALL=C</c> is what gettext honours over the system
    /// locale; <c>LANGUAGE</c> is removed because it would otherwise outrank
    /// <c>LC_MESSAGES</c> on some platforms.
    /// </summary>
    internal static void PrepareEnvironment(IDictionary<string, string?> environment, IReadOnlyList<string> extraPath, string? password)
    {
        foreach (var key in environment.Keys.ToList())
        {
            if (key.StartsWith("PG", StringComparison.OrdinalIgnoreCase) && !KeptLibpqVariables.Contains(key))
            {
                environment.Remove(key);
            }
        }

        if (!string.IsNullOrEmpty(password))
        {
            environment["PGPASSWORD"] = password;
        }

        environment.Remove("LANGUAGE");
        environment["LC_ALL"] = "C";

        if (extraPath.Count > 0)
        {
            var pathKey = environment.Keys.FirstOrDefault(k => string.Equals(k, "PATH", StringComparison.OrdinalIgnoreCase)) ?? "PATH";
            environment.TryGetValue(pathKey, out var current);
            var parts = new List<string>(extraPath);
            if (!string.IsNullOrEmpty(current))
            {
                parts.Add(current);
            }

            environment[pathKey] = string.Join(Path.PathSeparator, parts);
        }
    }

    private static ProcessStartInfo StartInfo(PgToolInvocation invocation)
    {
        var info = new ProcessStartInfo(invocation.FileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(invocation.FileName) ?? Environment.CurrentDirectory,
        };
        foreach (var argument in invocation.Arguments)
        {
            info.ArgumentList.Add(argument);
        }

        PrepareEnvironment(info.Environment, invocation.ExtraPath, invocation.Password);
        return info;
    }

    /// <summary>
    /// Runs <paramref name="invocation"/> to the end, handing each line it writes
    /// to standard error and standard output to the callbacks as it arrives
    /// (on a thread-pool thread). Cancelling kills the program and everything it
    /// started, and the task then throws <see cref="OperationCanceledException"/>.
    /// Standard input is closed at once, so a program that asks for a password
    /// fails instead of waiting for one nobody will type.
    /// </summary>
    /// <exception cref="PgToolStartException">The program could not be started.</exception>
    public static async Task<int> RunAsync(
        PgToolInvocation invocation,
        Action<string>? onStandardError,
        Action<string>? onStandardOutput,
        CancellationToken cancellationToken)
    {
        using var process = new Process { StartInfo = StartInfo(invocation), EnableRaisingEvents = true };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is { } line)
            {
                onStandardError?.Invoke(line);
            }
        };
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is { } line)
            {
                onStandardOutput?.Invoke(line);
            }
        };

        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            throw new PgToolStartException(invocation.FileName, ex);
        }

        process.StandardInput.Close();
        process.BeginErrorReadLine();
        process.BeginOutputReadLine();

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            // The streams close with the process; waiting here (without the
            // token) is what lets the last lines reach the callbacks and keeps
            // the caller from deleting a file the program still has open.
            await process.WaitForExitAsync(CancellationToken.None);
            throw;
        }

        return process.ExitCode;
    }

    /// <summary>
    /// Runs a short command and returns everything it printed. Gives up (and
    /// kills the program) after <paramref name="timeout"/>, reporting exit code
    /// -1, which is how a probe treats a program that hangs instead of answering
    /// <c>--version</c>.
    /// </summary>
    public static async Task<PgToolOutput> CaptureAsync(PgToolInvocation invocation, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var output = new StringBuilder();
        var error = new StringBuilder();
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            var exitCode = await RunAsync(
                invocation,
                line => { lock (error) { error.AppendLine(line); } },
                line => { lock (output) { output.AppendLine(line); } },
                timeoutSource.Token);
            return new PgToolOutput(exitCode, output.ToString(), error.ToString());
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new PgToolOutput(-1, output.ToString(), error.ToString());
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Already exited.
        }
        catch (Win32Exception)
        {
            // Exiting as we asked; WaitForExit below settles it.
        }
    }

    /// <summary>
    /// The command line as a person would type it, for the preview under the
    /// form: each argument quoted only when it needs to be, the way the
    /// platform's shell reads it (double quotes on Windows, single quotes
    /// elsewhere). The password is not part of it, because it is never an
    /// argument.
    /// </summary>
    public static string CommandLine(PgToolInvocation invocation) =>
        string.Join(' ', new[] { invocation.FileName }.Concat(invocation.Arguments).Select(QuoteForDisplay));

    internal static string QuoteForDisplay(string argument)
    {
        if (argument.Length > 0 && argument.All(c => char.IsAsciiLetterOrDigit(c) || "-_=./:,+@%\\".Contains(c)))
        {
            return argument;
        }

        return OperatingSystem.IsWindows()
            ? "\"" + argument.Replace("\"", "\\\"", StringComparison.Ordinal) + "\""
            : "'" + argument.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    }
}

/// <summary>A PostgreSQL client program that could not be started at all.</summary>
public sealed class PgToolStartException(string fileName, Exception inner)
    : Exception($"Could not start {Path.GetFileName(fileName)}: {inner.Message}", inner)
{
    public string FileName { get; } = fileName;
}
