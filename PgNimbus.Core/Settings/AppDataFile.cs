using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using PgNimbus.Core.Connections;

namespace PgNimbus.Core.Settings;

/// <summary>
/// The one way the app's own files reach the disk. Every store (connection
/// profiles, settings, workspace, history, saved queries, completion usage,
/// window placement, the crash log, the DPAPI credential files) writes through
/// here rather than <see cref="File.WriteAllText(string, string?)"/>, for two
/// reasons found by the 2026-09 security audit (findings 10 and 18):
/// <list type="bullet">
/// <item>
/// <b>Readable by the owner only.</b> A plain <c>File.WriteAllText</c> creates
/// <c>0644</c> under the default umask, and the app data root landed under a
/// <c>0755</c> home on Debian, older Ubuntu and macOS — so every other local
/// user could read the query history (every statement, values included), the
/// workspace SQL, connection hosts, usernames and SSH key paths. Files are now
/// created <c>0600</c> and directories <c>0700</c>; an existing directory inside the
/// app data root is tightened the first time it is touched (<see cref="EnsureDirectory"/>,
/// <see cref="Tighten"/>) and once at startup (<see cref="TightenExisting"/>).
/// Windows is untouched: <c>%AppData%</c> is already per user, and the Unix
/// mode APIs throw there.
/// </item>
/// <item>
/// <b>Atomic replace.</b> The text goes to a temp file in the same directory,
/// is flushed to disk, and is then renamed over the target — one rename, which
/// either happens or doesn't on Windows and POSIX alike. A crash mid-write used
/// to leave a truncated <c>connections.json</c>, which <c>Load</c> read as an
/// empty list, and the connection dialog's autosave on the next keystroke then
/// wrote that empty list back over every saved profile. A file that still
/// can't be parsed (written by an older version, or damaged on disk) is moved
/// aside as <c>&lt;name&gt;.corrupt-&lt;timestamp&gt;</c> by
/// <see cref="ReadJson{T}"/> before the store starts over, so nothing is
/// silently overwritten.
/// </item>
/// </list>
/// A <c>null</c> path means "no app data directory at all" (see
/// <see cref="Connections.AppDataPaths"/>): reads answer "nothing saved" and
/// writes are dropped, so the app runs from memory for the session instead of
/// writing next to other users' files in a shared temp directory.
/// </summary>
public static class AppDataFile
{
    /// <summary>Owner read/write, nobody else: what every file the app writes is created with on Unix.</summary>
    public const UnixFileMode PrivateFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    /// <summary>Owner read/write/search, nobody else: what every directory the app creates gets on Unix.</summary>
    public const UnixFileMode PrivateDirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    // Directories this process has already created or tightened. Tightening an
    // existing directory costs a chmod per store write otherwise, and the answer
    // never changes within one process.
    private static readonly ConcurrentDictionary<string, byte> PrivateDirectories = new(StringComparer.Ordinal);

    /// <summary>
    /// Creates <paramref name="path"/> (and any missing parents) with
    /// <see cref="PrivateDirectoryMode"/>, or, when it already exists and is the
    /// app data root or inside it, tightens it to that mode once per process.
    /// An existing directory anywhere else is left alone: a store handed an
    /// explicit path (a test writing into <c>/tmp</c>) must never chmod a
    /// directory the app does not own, and under root in a container it would
    /// succeed. A failed tighten does not fail the write: the file itself is
    /// created private either way. No-op for a null path.
    /// </summary>
    public static void EnsureDirectory(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);
            return;
        }

        var key = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (Directory.Exists(path))
        {
            if (IsInsideAppDataRoot(key) && PrivateDirectories.TryAdd(key, 0))
            {
                try
                {
                    Tighten(path);
                }
                catch (Exception e) when (IsReadFailure(e))
                {
                    // Startup's TightenExisting reports it; the write goes ahead.
                }
            }

            return;
        }

        Directory.CreateDirectory(path, PrivateDirectoryMode);
        // The create mode is subject to the umask; make the leaf exactly private
        // whatever the umask left of it.
        File.SetUnixFileMode(path, PrivateDirectoryMode);
        PrivateDirectories.TryAdd(key, 0);
    }

    /// <summary>
    /// Writes <paramref name="text"/> (UTF-8, no BOM) to <paramref name="path"/>
    /// by way of a private temp file in the same directory that is flushed and
    /// then renamed over the target. A reader sees the old content or the new,
    /// never a torn file. No-op for a null path.
    /// </summary>
    public static void WriteAllText(string? path, string text) =>
        WriteAtomically(path, stream =>
        {
            using var writer = new StreamWriter(stream, Utf8NoBom, leaveOpen: true);
            writer.Write(text);
        });

    /// <summary>Binary counterpart of <see cref="WriteAllText"/>: the same temp-then-rename replace.</summary>
    public static void WriteAllBytes(string? path, byte[] bytes) =>
        WriteAtomically(path, stream => stream.Write(bytes));

    /// <summary>
    /// Appends <paramref name="text"/> (UTF-8, no BOM), creating the file with
    /// <see cref="PrivateFileMode"/> when it does not exist yet. For the crash
    /// log, whose entries accumulate; a replace would rewrite the whole file per
    /// entry. No-op for a null path.
    /// </summary>
    public static void AppendAllText(string? path, string text)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        EnsureDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
        // Readers allowed: someone may have the log open while another entry lands.
        using var stream = new FileStream(path, CreateOptions(FileMode.Append, FileShare.Read));
        using var writer = new StreamWriter(stream, Utf8NoBom);
        writer.Write(text);
    }

    /// <summary>
    /// Sets an existing file to <see cref="PrivateFileMode"/> or an existing
    /// directory to <see cref="PrivateDirectoryMode"/>. Skips a symbolic link
    /// (chmod would follow it to a target the app does not own), a missing
    /// path, and Windows.
    /// </summary>
    public static void Tighten(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        if (Directory.Exists(path))
        {
            if (new DirectoryInfo(path).LinkTarget is null)
            {
                File.SetUnixFileMode(path, PrivateDirectoryMode);
            }
        }
        else if (File.Exists(path) && new FileInfo(path).LinkTarget is null)
        {
            File.SetUnixFileMode(path, PrivateFileMode);
        }
    }

    /// <summary>
    /// Tightens the app data root, every file directly in it, and the
    /// <c>logs</c> and <c>credentials</c> subdirectories with their files: what
    /// an earlier version left at <c>0644</c>/<c>0755</c>, once per startup.
    /// Every entry is attempted; the first failure is rethrown at the end so the
    /// caller can log it. Cheap: a handful of <c>chmod</c> calls. No-op on
    /// Windows, for a null root, and for a root that does not exist yet.
    /// </summary>
    public static void TightenExisting(string? root)
    {
        if (OperatingSystem.IsWindows() || string.IsNullOrEmpty(root) || !Directory.Exists(root))
        {
            return;
        }

        Exception? first = null;
        TightenQuietly(root, ref first);
        foreach (var file in Directory.EnumerateFiles(root))
        {
            TightenQuietly(file, ref first);
        }

        foreach (var subdirectory in new[] { "logs", "credentials" })
        {
            var directory = Path.Combine(root, subdirectory);
            if (!Directory.Exists(directory))
            {
                continue;
            }

            TightenQuietly(directory, ref first);
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                TightenQuietly(file, ref first);
            }
        }

        if (first is not null)
        {
            throw new IOException($"Could not restrict every app data file to the current user: {first.Message}", first);
        }
    }

    /// <summary>
    /// Reads and deserializes a JSON file through a source-generated context.
    /// Returns null when there is no such file, when the path is null, when the
    /// file cannot be read, and when it cannot be parsed — in which last case
    /// the file is first moved aside with <see cref="BackUpCorrupt"/>, so the
    /// next save does not overwrite the only copy of what it held.
    /// </summary>
    public static T? ReadJson<T>(string? path, JsonTypeInfo<T> typeInfo) where T : class
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return null;
        }

        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (Exception e) when (IsReadFailure(e))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(json, typeInfo);
        }
        catch (JsonException)
        {
            BackUpCorrupt(path);
            return null;
        }
    }

    /// <summary>Serializes through a source-generated context and writes with <see cref="WriteAllText"/>.</summary>
    public static void WriteJson<T>(string? path, T value, JsonTypeInfo<T> typeInfo)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        WriteAllText(path, JsonSerializer.Serialize(value, typeInfo));
    }

    /// <summary>
    /// Moves a file the app could not parse to <c>&lt;path&gt;.corrupt-&lt;UTC timestamp&gt;</c>
    /// next to it (a numbered suffix if that name is taken). Returns the backup
    /// path, or null when the file could not be moved — the caller then carries
    /// on as if the file were empty, exactly as before, since a store must never
    /// fail startup over its own file.
    /// </summary>
    public static string? BackUpCorrupt(string path)
    {
        try
        {
            var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
            var backup = $"{path}.corrupt-{stamp}";
            for (var n = 2; File.Exists(backup); n++)
            {
                backup = $"{path}.corrupt-{stamp}-{n}";
            }

            File.Move(path, backup);
            return backup;
        }
        catch (Exception e) when (IsReadFailure(e))
        {
            return null;
        }
    }

    private static void WriteAtomically(string? path, Action<FileStream> write)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException("A file path is required.", nameof(path));
        EnsureDirectory(directory);

        // Same directory as the target: a rename across file systems is a copy,
        // not an atomic replace. A leading dot keeps the temp file out of a
        // casual `ls` on Unix; the guid keeps two writers apart.
        var temp = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temp, CreateOptions(FileMode.CreateNew, FileShare.None)))
            {
                write(stream);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, fullPath, overwrite: true);
        }
        catch
        {
            try
            {
                File.Delete(temp);
            }
            catch (Exception e) when (IsReadFailure(e))
            {
                // The original error is the one worth reporting.
            }

            throw;
        }
    }

    private static FileStreamOptions CreateOptions(FileMode mode, FileShare share)
    {
        var options = new FileStreamOptions
        {
            Mode = mode,
            Access = FileAccess.Write,
            Share = share,
        };

        // Setting UnixCreateMode throws on Windows.
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = PrivateFileMode;
        }

        return options;
    }

    private static bool IsInsideAppDataRoot(string fullPath)
    {
        if (AppDataPaths.GetRootDirectory() is not { Length: > 0 } root)
        {
            return false;
        }

        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var comparison = OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(fullPath, fullRoot, comparison)
            || fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, comparison);
    }

    private static void TightenQuietly(string path, ref Exception? first)
    {
        try
        {
            Tighten(path);
        }
        catch (Exception e) when (IsReadFailure(e))
        {
            first ??= e;
        }
    }

    // Everything a path that came out of a settings file, or a disk in a bad
    // state, can throw: a NUL in the path is an ArgumentException, a device
    // path a NotSupportedException.
    private static bool IsReadFailure(Exception e) =>
        e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException
            or System.Security.SecurityException;
}
