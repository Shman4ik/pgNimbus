namespace PgNimbus.Core.Backup;

/// <summary>The PostgreSQL client programs pgNimbus runs.</summary>
public enum PgTool
{
    PgDump,
    PgRestore,
}

/// <summary>
/// One directory holding a working <c>pg_dump</c> and <c>pg_restore</c>.
/// </summary>
/// <param name="Directory">Where the two programs are.</param>
/// <param name="Version">What <c>pg_dump --version</c> reported.</param>
/// <param name="Source">
/// Where the install came from, for the status line ("pgAdmin 4", "Homebrew",
/// "PATH"), or null for a plain PostgreSQL install.
/// </param>
/// <param name="ExtraPath">
/// Directories to put in front of the child's <c>PATH</c>. pgAdmin 4 on Windows
/// keeps <c>pg_dump.exe</c> in <c>runtime\</c> and one library it needs
/// (<c>gssapi64.dll</c>) in <c>python\</c>; without that folder on the path the
/// program does not start at all (STATUS_DLL_NOT_FOUND).
/// </param>
public sealed record PgToolInstall(string Directory, PgVersion Version, string? Source, IReadOnlyList<string> ExtraPath)
{
    /// <summary>The full path of <paramref name="tool"/> in this install.</summary>
    public string PathOf(PgTool tool) => Path.Combine(Directory, ExecutableName(tool));

    /// <summary>
    /// "pg_dump 18.6 · C:\Program Files\PostgreSQL\18\bin", with the source when
    /// there is one. The status line under the backup and restore forms.
    /// </summary>
    public string Describe(PgTool tool) =>
        Source is null
            ? $"{ToolName(tool)} {Version} · {Directory}"
            : $"{ToolName(tool)} {Version} from {Source} · {Directory}";

    /// <summary>The program's file name on this platform.</summary>
    public static string ExecutableName(PgTool tool) =>
        OperatingSystem.IsWindows() ? ToolName(tool) + ".exe" : ToolName(tool);

    /// <summary>The program's name as PostgreSQL's documentation writes it.</summary>
    public static string ToolName(PgTool tool) => tool switch
    {
        PgTool.PgDump => "pg_dump",
        PgTool.PgRestore => "pg_restore",
        _ => throw new ArgumentOutOfRangeException(nameof(tool)),
    };
}

/// <summary>
/// A directory that holds <c>pg_dump</c> but where it could not be used, kept so
/// the window can say what it found instead of claiming nothing is installed.
/// </summary>
public sealed record PgToolProblem(string Directory, string Reason);

/// <summary>
/// What a search found: every working install, newest first, and the
/// directories whose programs did not run.
/// </summary>
public sealed record PgToolScan(IReadOnlyList<PgToolInstall> Installs, IReadOnlyList<PgToolProblem> Problems, string? ConfiguredDirectory)
{
    public static PgToolScan Empty { get; } = new([], [], null);

    /// <summary>The newest install found, which reads every archive an older one wrote.</summary>
    public PgToolInstall? Newest => Installs.Count == 0 ? null : Installs[0];

    /// <summary>
    /// The install to dump a server of <paramref name="server"/>'s version with: the
    /// newest one at least as new as the server's major release, or null.
    /// </summary>
    public PgToolInstall? For(PgVersion server) => Installs.FirstOrDefault(install => install.Version.CanDump(server));
}
