using System.Globalization;

namespace PgNimbus.Core.Backup;

/// <summary>The three platforms pgNimbus ships for; what a search looks for differs on each.</summary>
public enum PgToolPlatform
{
    Windows,
    MacOS,
    Linux,
}

/// <summary>
/// The parts of the machine a search reads, so a test can describe a machine
/// with pgAdmin or Homebrew on it without either being installed (and a
/// Windows machine on a Linux runner: paths are joined with the described
/// platform's separator, not the one the test runs on).
/// </summary>
public sealed record PgToolHost(
    PgToolPlatform Platform,
    Func<string, string?> GetEnvironmentVariable,
    Func<string, IEnumerable<string>> ListDirectories,
    Func<string, bool> FileExists,
    Func<string, bool> DirectoryExists)
{
    /// <summary>This machine.</summary>
    public static PgToolHost Current { get; } = new(
        OperatingSystem.IsWindows() ? PgToolPlatform.Windows
            : OperatingSystem.IsMacOS() ? PgToolPlatform.MacOS
            : PgToolPlatform.Linux,
        Environment.GetEnvironmentVariable,
        ListSubdirectories,
        File.Exists,
        Directory.Exists);

    /// <summary>The directory separator paths are written with on this platform.</summary>
    public char Separator => Platform == PgToolPlatform.Windows ? '\\' : '/';

    /// <summary>Joins path parts with <see cref="Separator"/>.</summary>
    public string Combine(params string[] parts)
    {
        var result = "";
        foreach (var part in parts)
        {
            if (part.Length == 0)
            {
                continue;
            }

            result = result.Length == 0
                ? part
                : result.TrimEnd('/', '\\') + Separator + part.TrimStart('/', '\\');
        }

        return result;
    }

    /// <summary>The last segment of <paramref name="path"/>.</summary>
    public string FileName(string path)
    {
        var trimmed = path.TrimEnd('/', '\\');
        var cut = Platform == PgToolPlatform.Windows ? trimmed.LastIndexOfAny(['\\', '/']) : trimmed.LastIndexOf('/');
        return cut < 0 ? trimmed : trimmed[(cut + 1)..];
    }

    /// <summary>Everything before the last segment of <paramref name="path"/>, or null at a root.</summary>
    public string? Parent(string path)
    {
        var trimmed = path.TrimEnd('/', '\\');
        var cut = Platform == PgToolPlatform.Windows ? trimmed.LastIndexOfAny(['\\', '/']) : trimmed.LastIndexOf('/');
        return cut <= 0 ? null : trimmed[..cut];
    }

    /// <summary>
    /// Whether <paramref name="path"/> names a place on its own (<c>C:\x</c>,
    /// <c>\\server\share</c>, <c>/usr/bin</c>), rather than relative to wherever
    /// the app happened to start.
    /// </summary>
    public bool IsAbsolute(string path) => Platform == PgToolPlatform.Windows
        ? (path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] is '\\' or '/')
          || path.StartsWith(@"\\", StringComparison.Ordinal)
        : path.StartsWith('/');

    private static IEnumerable<string> ListSubdirectories(string path)
    {
        try
        {
            return Directory.Exists(path) ? Directory.GetDirectories(path) : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}

/// <summary>A directory a search will look in, and what to call it if the programs are there.</summary>
public sealed record PgToolCandidate(string Directory, string? Source, IReadOnlyList<string> ExtraPath);

/// <summary>What checking one directory found: a version, or why it did not count.</summary>
public sealed record PgToolProbeResult(PgVersion? Version, string? Problem);

/// <summary>
/// Finds PostgreSQL's <c>pg_dump</c> and <c>pg_restore</c> on this machine. A
/// person who installed PostgreSQL, pgAdmin, Postgres.app or Homebrew's libpq
/// should never have to point pgNimbus at them, and an app started from the
/// Finder on macOS does not see the PATH a terminal has, so the well-known
/// places are searched by name rather than through PATH alone. Every
/// directory found is checked by running <c>pg_dump --version</c>: a program
/// that is there but does not start (pgAdmin's copy without its library
/// folder on the path) is reported, not offered.
/// </summary>
public static class PgToolLocator
{
    /// <summary>How long one <c>--version</c> may take before the program counts as not working.</summary>
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The directories to look in, best first. With
    /// <paramref name="configuredDirectory"/> set, only that one: a folder the
    /// user chose wins over anything a search would find, and a search that
    /// quietly used another one would make the setting meaningless.
    /// </summary>
    public static IReadOnlyList<PgToolCandidate> Candidates(PgToolHost host, string? configuredDirectory)
    {
        if (!string.IsNullOrWhiteSpace(configuredDirectory))
        {
            var configured = Normalize(configuredDirectory);
            return [new PgToolCandidate(configured, null, ExtraPathFor(host, configured))];
        }

        var candidates = new List<PgToolCandidate>();
        var seen = new HashSet<string>(host.Platform == PgToolPlatform.Linux ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);

        void Add(string directory, string? source, IReadOnlyList<string> extraPath)
        {
            var normalized = Normalize(directory);
            if (normalized.Length > 0 && seen.Add(normalized))
            {
                candidates.Add(new PgToolCandidate(normalized, source, extraPath));
            }
        }

        void AddMatching(string pattern, string? source)
        {
            foreach (var directory in Expand(host, pattern))
            {
                Add(directory, source, []);
            }
        }

        switch (host.Platform)
        {
            case PgToolPlatform.Windows:
                var programFiles = Values(host, "ProgramFiles", "ProgramW6432", "ProgramFiles(x86)");
                foreach (var root in programFiles)
                {
                    AddMatching(host.Combine(root, "PostgreSQL", "*", "bin"), null);
                }

                foreach (var scoop in Values(host, "SCOOP").Concat(Values(host, "USERPROFILE").Select(p => host.Combine(p, "scoop"))))
                {
                    AddMatching(host.Combine(scoop, "apps", "postgresql*", "current", "bin"), "Scoop");
                }

                AddPathEntries(host, Add);

                // pgAdmin 4 ships the newest client programs it supports. The
                // current layout keeps them in runtime\, older versions in a
                // versioned folder (v6\runtime); a per-user install lives under
                // %LOCALAPPDATA%\Programs. Last, so a plain PostgreSQL install of
                // the same version wins: pgAdmin's copy needs its python\ folder
                // on the path to start at all.
                var pgAdminRoots = programFiles.Select(root => host.Combine(root, "pgAdmin 4"))
                    .Concat(Values(host, "LOCALAPPDATA").Select(local => host.Combine(local, "Programs", "pgAdmin 4")));
                foreach (var pgAdmin in pgAdminRoots)
                {
                    var runtime = host.Combine(pgAdmin, "runtime");
                    Add(runtime, "pgAdmin 4", ExtraPathFor(host, runtime));
                    foreach (var versioned in Expand(host, host.Combine(pgAdmin, "v*", "runtime")))
                    {
                        Add(versioned, "pgAdmin 4", ExtraPathFor(host, versioned));
                    }
                }

                break;

            case PgToolPlatform.MacOS:
                foreach (var brew in new[] { "/opt/homebrew", "/usr/local" })
                {
                    Add($"{brew}/opt/libpq/bin", "Homebrew", []);
                    AddMatching($"{brew}/opt/postgresql@*/bin", "Homebrew");
                    Add($"{brew}/opt/postgresql/bin", "Homebrew", []);
                }

                AddMatching("/Applications/Postgres.app/Contents/Versions/*/bin", "Postgres.app");
                foreach (var home in Values(host, "HOME"))
                {
                    AddMatching($"{home}/Applications/Postgres.app/Contents/Versions/*/bin", "Postgres.app");
                }

                AddMatching("/Library/PostgreSQL/*/bin", null);
                AddMatching("/opt/local/lib/postgresql*/bin", "MacPorts");
                Add("/opt/homebrew/bin", "Homebrew", []);
                Add("/usr/local/bin", null, []);
                AddPathEntries(host, Add);
                Add("/Applications/pgAdmin 4.app/Contents/SharedSupport", "pgAdmin 4", []);
                break;

            default:
                AddMatching("/usr/lib/postgresql/*/bin", null);
                AddMatching("/usr/pgsql-*/bin", null);
                Add("/usr/local/pgsql/bin", null, []);
                Add("/usr/bin", null, []);
                Add("/usr/local/bin", null, []);
                Add("/home/linuxbrew/.linuxbrew/opt/libpq/bin", "Homebrew", []);
                foreach (var home in Values(host, "HOME"))
                {
                    Add($"{home}/.linuxbrew/opt/libpq/bin", "Homebrew", []);
                }

                AddPathEntries(host, Add);
                break;
        }

        return candidates;
    }

    /// <summary>Searches this machine (see <see cref="Candidates"/>).</summary>
    public static Task<PgToolScan> ScanAsync(string? configuredDirectory, CancellationToken cancellationToken) =>
        ScanAsync(PgToolHost.Current, configuredDirectory, ProbeAsync, cancellationToken);

    /// <summary>
    /// Checks every candidate with <paramref name="probe"/> and returns the
    /// installs that work, newest first (a tie keeps the search's order, so a
    /// plain PostgreSQL install beats pgAdmin's copy of the same version).
    /// </summary>
    public static async Task<PgToolScan> ScanAsync(
        PgToolHost host,
        string? configuredDirectory,
        Func<PgToolCandidate, CancellationToken, Task<PgToolProbeResult>> probe,
        CancellationToken cancellationToken)
    {
        var configured = string.IsNullOrWhiteSpace(configuredDirectory) ? null : Normalize(configuredDirectory);
        var installs = new List<(PgToolInstall Install, int Order)>();
        var problems = new List<PgToolProblem>();

        foreach (var candidate in Candidates(host, configured))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!host.FileExists(host.Combine(candidate.Directory, ExecutableName(host, PgTool.PgDump))))
            {
                // A folder the user picked that has no pg_dump is worth saying;
                // every PATH entry without one is not.
                if (configured is not null)
                {
                    problems.Add(new PgToolProblem(candidate.Directory, "there's no pg_dump in this folder"));
                }

                continue;
            }

            if (!host.FileExists(host.Combine(candidate.Directory, ExecutableName(host, PgTool.PgRestore))))
            {
                problems.Add(new PgToolProblem(candidate.Directory, "pg_dump is there, but pg_restore isn't"));
                continue;
            }

            var result = await probe(candidate, cancellationToken);
            if (result.Version is { } version)
            {
                installs.Add((new PgToolInstall(candidate.Directory, version, candidate.Source, candidate.ExtraPath), installs.Count));
            }
            else
            {
                problems.Add(new PgToolProblem(candidate.Directory, result.Problem ?? "it didn't report a PostgreSQL version"));
            }
        }

        var sorted = installs
            .OrderByDescending(i => i.Install.Version)
            .ThenBy(i => i.Order)
            .Select(i => i.Install)
            .ToList();
        return new PgToolScan(sorted, problems, configured);
    }

    /// <summary>Runs <c>pg_dump --version</c> in <paramref name="candidate"/>'s directory.</summary>
    public static async Task<PgToolProbeResult> ProbeAsync(PgToolCandidate candidate, CancellationToken cancellationToken)
    {
        var invocation = new PgToolInvocation(
            Path.Combine(candidate.Directory, PgToolInstall.ExecutableName(PgTool.PgDump)),
            ["--version"],
            candidate.ExtraPath);
        PgToolOutput output;
        try
        {
            output = await PgToolProcess.CaptureAsync(invocation, ProbeTimeout, cancellationToken);
        }
        catch (PgToolStartException ex)
        {
            return new PgToolProbeResult(null, $"it can't be started ({ex.InnerException?.Message ?? ex.Message})");
        }

        if (output.ExitCode == 0 && PgVersion.TryParseToolOutput(output.StandardOutput, out var version))
        {
            return new PgToolProbeResult(version, null);
        }

        return new PgToolProbeResult(null, DescribeFailure(output));
    }

    /// <summary>Why a <c>--version</c> run did not count, in words for the status line.</summary>
    public static string DescribeFailure(PgToolOutput output) => output.ExitCode switch
    {
        -1 => $"it didn't answer within {ProbeTimeout.TotalSeconds.ToString(CultureInfo.InvariantCulture)} seconds",
        // STATUS_DLL_NOT_FOUND: Windows could not load a library the program needs.
        -1073741515 => "it doesn't start: a library it needs is missing",
        // STATUS_INVALID_IMAGE_FORMAT: built for another processor.
        -1073741701 => "it doesn't start: it's built for a different processor",
        0 => "it didn't report a PostgreSQL version",
        var code => FirstLine(output.StandardError) is { } line
            ? $"it exited with code {code.ToString(CultureInfo.InvariantCulture)}: {line}"
            : $"it exited with code {code.ToString(CultureInfo.InvariantCulture)}",
    };

    private static string? FirstLine(string text)
    {
        var line = text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return line is null ? null : line.Length > 200 ? line[..200] + "…" : line;
    }

    private static string ExecutableName(PgToolHost host, PgTool tool) =>
        host.Platform == PgToolPlatform.Windows ? PgToolInstall.ToolName(tool) + ".exe" : PgToolInstall.ToolName(tool);

    /// <summary>
    /// The directories a folder's programs need on the path: pgAdmin 4's
    /// <c>python</c> folder beside its <c>runtime</c> folder on Windows (or one
    /// level up, for the versioned <c>v6\runtime</c> layout), nothing anywhere
    /// else. Worked out for a configured folder too, so pointing Settings at
    /// pgAdmin's runtime folder works as well as the search finding it.
    /// </summary>
    internal static IReadOnlyList<string> ExtraPathFor(PgToolHost host, string directory)
    {
        if (host.Platform != PgToolPlatform.Windows
            || !string.Equals(host.FileName(directory), "runtime", StringComparison.OrdinalIgnoreCase)
            || host.Parent(directory) is not { } parent)
        {
            return [];
        }

        var candidates = new List<string> { host.Combine(parent, "python") };
        if (host.Parent(parent) is { } grandparent)
        {
            candidates.Add(host.Combine(grandparent, "python"));
        }

        return candidates.FirstOrDefault(host.DirectoryExists) is { } python ? [python] : [];
    }

    private static void AddPathEntries(PgToolHost host, Action<string, string?, IReadOnlyList<string>> add)
    {
        var path = host.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        var separator = host.Platform == PgToolPlatform.Windows ? ';' : ':';
        foreach (var raw in path.Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // A relative PATH entry ("." or "bin") means "wherever the app was
            // started", which is not a place to run programs from.
            var entry = raw.Trim('"');
            if (host.IsAbsolute(entry))
            {
                add(entry, "PATH", ExtraPathFor(host, entry));
            }
        }
    }

    private static List<string> Values(PgToolHost host, params string[] variables) =>
        variables
            .Select(host.GetEnvironmentVariable)
            .OfType<string>()
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// The directories matching <paramref name="pattern"/>, whose one segment
    /// with a <c>*</c> matches any subdirectory name with that prefix and
    /// suffix (<c>…/PostgreSQL/*/bin</c>, <c>…/postgresql@*/bin</c>). Highest
    /// version-looking name first, so <c>18</c> is tried before <c>9.6</c>.
    /// </summary>
    internal static IEnumerable<string> Expand(PgToolHost host, string pattern)
    {
        var separators = host.Platform == PgToolPlatform.Windows ? new[] { '\\', '/' } : ['/'];
        var segments = pattern.Split(separators);
        var wildcard = Array.FindIndex(segments, s => s.Contains('*'));
        if (wildcard < 0)
        {
            return [pattern];
        }

        var parent = string.Join(host.Separator, segments[..wildcard]);
        if (parent.Length == 0)
        {
            parent = host.Separator.ToString();
        }

        var star = segments[wildcard].IndexOf('*');
        var prefix = segments[wildcard][..star];
        var suffix = segments[wildcard][(star + 1)..];
        var rest = segments[(wildcard + 1)..];
        var comparison = host.Platform == PgToolPlatform.Linux ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        return host.ListDirectories(parent)
            .Select(directory => (Directory: directory, Name: host.FileName(directory)))
            .Where(d => d.Name.Length >= prefix.Length + suffix.Length
                        && d.Name.StartsWith(prefix, comparison)
                        && d.Name.EndsWith(suffix, comparison))
            .OrderByDescending(d => VersionKey(d.Name[prefix.Length..(d.Name.Length - suffix.Length)]))
            .Select(d => rest.Length == 0 ? d.Directory : host.Combine([d.Directory, .. rest]));
    }

    private static double VersionKey(string name) =>
        double.TryParse(name, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : -1;

    private static string Normalize(string directory)
    {
        var trimmed = directory.Trim().Trim('"');
        var withoutSeparator = trimmed.TrimEnd('/', '\\');
        // Keep a root ("/" or "C:\") as it is.
        return withoutSeparator.Length == 0 || withoutSeparator.EndsWith(':') ? trimmed : withoutSeparator;
    }
}
