using System.Globalization;

namespace PgNimbus.Core.Backup;

/// <summary>
/// One step of the install instructions: what to do, and optionally a command
/// to copy or a page to open.
/// </summary>
public sealed record PgToolGuideStep(string Text, string? Command = null, string? LinkUrl = null, string? LinkText = null);

/// <summary>Which Linux packaging the instructions are written for.</summary>
public enum LinuxFamily
{
    Other,
    Debian,
    Ubuntu,
    RedHat,
    Arch,
}

/// <summary>
/// How to install pg_dump and pg_restore on this machine, as steps with the
/// exact commands. Written for the moment a backup is blocked on them, so it
/// names one way per platform (plus one alternative where the first needs
/// something the machine may not have) rather than every way there is, and
/// it always ends in "click Look Again", because pgNimbus then finds the
/// programs where the steps put them.
/// </summary>
public static class PgToolInstallGuide
{
    /// <summary>
    /// The newest PostgreSQL release pgNimbus knows of. The steps install it
    /// unless the server is newer still: a newer pg_dump backs up every older
    /// server, so installing the newest one never has to be done twice.
    /// </summary>
    public const int NewestKnownMajor = 18;

    /// <summary>The major release the steps install for a server of <paramref name="server"/>'s version.</summary>
    public static int MajorToInstall(PgVersion? server) =>
        server is { Major: >= 10 } version ? Math.Max(version.Major, NewestKnownMajor) : NewestKnownMajor;

    /// <summary>The steps for <paramref name="platform"/>.</summary>
    public static IReadOnlyList<PgToolGuideStep> Steps(PgToolPlatform platform, int major, LinuxFamily linux = LinuxFamily.Other, bool hasHomebrew = false)
    {
        var n = major.ToString(CultureInfo.InvariantCulture);
        return platform switch
        {
            PgToolPlatform.Windows =>
            [
                new($"Download the PostgreSQL {n} installer for Windows (Windows x86-64) from EDB, the official source.",
                    LinkUrl: "https://www.enterprisedb.com/downloads/postgres-postgresql-downloads",
                    LinkText: "Open the download page"),
                new("Run it. On the Select Components page, leave only Command Line Tools checked: you don't need the server, pgAdmin or Stack Builder. Keep the suggested folder."),
                new("Or install the same thing from a terminal, and pick only Command Line Tools when the installer asks:",
                    Command: $"winget install -e --id PostgreSQL.PostgreSQL.{n} --interactive"),
                new($"Then click Look Again. pgNimbus finds C:\\Program Files\\PostgreSQL\\{n}\\bin on its own."),
            ],
            PgToolPlatform.MacOS when hasHomebrew =>
            [
                new("Install PostgreSQL's client programs with Homebrew:", Command: "brew install libpq"),
                new("Then click Look Again. pgNimbus finds them in Homebrew's libpq folder on its own; you don't need to add anything to PATH."),
            ],
            PgToolPlatform.MacOS =>
            [
                new("Download Postgres.app and move it to Applications. It includes pg_dump and pg_restore; you don't have to start its server.",
                    LinkUrl: "https://postgresapp.com/downloads.html",
                    LinkText: "Open postgresapp.com"),
                new("Or, if you use Homebrew:", Command: "brew install libpq"),
                new("Then click Look Again. pgNimbus finds them in either place on its own."),
            ],
            _ => LinuxSteps(linux, n),
        };
    }

    private static IReadOnlyList<PgToolGuideStep> LinuxSteps(LinuxFamily family, string n) => family switch
    {
        LinuxFamily.Debian or LinuxFamily.Ubuntu =>
        [
            new("Add PostgreSQL's own package repository. Your distribution's packages are often older than the server, and pg_dump can't be older:",
                Command: "sudo apt install -y postgresql-common\nsudo /usr/share/postgresql-common/pgdg/apt.postgresql.org.sh",
                LinkUrl: family == LinuxFamily.Debian ? "https://www.postgresql.org/download/linux/debian/" : "https://www.postgresql.org/download/linux/ubuntu/",
                LinkText: "Instructions on postgresql.org"),
            new("Install the client programs:", Command: $"sudo apt install postgresql-client-{n}"),
            new($"Then click Look Again. pgNimbus finds /usr/lib/postgresql/{n}/bin on its own."),
        ],
        LinuxFamily.RedHat =>
        [
            new("Add PostgreSQL's own package repository for your distribution, as postgresql.org describes it.",
                LinkUrl: "https://www.postgresql.org/download/linux/redhat/",
                LinkText: "Instructions on postgresql.org"),
            new("Install the client programs:", Command: $"sudo dnf install postgresql{n}"),
            new($"Then click Look Again. pgNimbus finds /usr/pgsql-{n}/bin on its own."),
        ],
        LinuxFamily.Arch =>
        [
            new("Install the client programs:", Command: "sudo pacman -S postgresql-libs"),
            new("Then click Look Again."),
        ],
        _ =>
        [
            new($"Install the PostgreSQL {n} client programs (the package that holds pg_dump and pg_restore) from your distribution, or from postgresql.org.",
                LinkUrl: "https://www.postgresql.org/download/linux/",
                LinkText: "Instructions on postgresql.org"),
            new("Then click Look Again."),
        ],
    };

    /// <summary>
    /// The family the <c>ID</c> and <c>ID_LIKE</c> lines of
    /// <c>/etc/os-release</c> name (<c>ID=pop</c> with <c>ID_LIKE="ubuntu
    /// debian"</c> is Ubuntu's instructions).
    /// </summary>
    public static LinuxFamily ParseOsRelease(string? osRelease)
    {
        if (string.IsNullOrEmpty(osRelease))
        {
            return LinuxFamily.Other;
        }

        var ids = new List<string>();
        foreach (var line in osRelease.Split('\n'))
        {
            var trimmed = line.Trim();
            foreach (var key in new[] { "ID=", "ID_LIKE=" })
            {
                if (trimmed.StartsWith(key, StringComparison.Ordinal))
                {
                    ids.AddRange(trimmed[key.Length..].Trim('"', '\'').Split(' ', StringSplitOptions.RemoveEmptyEntries));
                }
            }
        }

        // The distribution itself first, then what it is like.
        foreach (var id in ids.Select(i => i.ToLowerInvariant()))
        {
            switch (id)
            {
                case "ubuntu":
                    return LinuxFamily.Ubuntu;
                case "debian":
                    return LinuxFamily.Debian;
                case "fedora" or "rhel" or "centos" or "rocky" or "almalinux" or "ol":
                    return LinuxFamily.RedHat;
                case "arch" or "manjaro" or "endeavouros":
                    return LinuxFamily.Arch;
            }
        }

        return LinuxFamily.Other;
    }

    /// <summary>The steps for the machine pgNimbus is running on.</summary>
    public static IReadOnlyList<PgToolGuideStep> ForThisMachine(PgVersion? server)
    {
        var major = MajorToInstall(server);
        if (OperatingSystem.IsWindows())
        {
            return Steps(PgToolPlatform.Windows, major);
        }

        if (OperatingSystem.IsMacOS())
        {
            var hasHomebrew = File.Exists("/opt/homebrew/bin/brew") || File.Exists("/usr/local/bin/brew");
            return Steps(PgToolPlatform.MacOS, major, hasHomebrew: hasHomebrew);
        }

        string? osRelease = null;
        try
        {
            osRelease = File.Exists("/etc/os-release") ? File.ReadAllText("/etc/os-release") : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The generic instructions are still right.
        }

        return Steps(PgToolPlatform.Linux, major, ParseOsRelease(osRelease));
    }
}
