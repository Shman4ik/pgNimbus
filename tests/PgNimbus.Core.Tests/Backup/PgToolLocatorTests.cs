using PgNimbus.Core.Backup;

namespace PgNimbus.Core.Tests.Backup;

/// <summary>
/// The search is what makes "it just finds pg_dump" true, and each platform
/// hides the programs somewhere else, so each is described as a fake machine
/// here (a Windows one on a Linux runner too: the search joins paths with the
/// described platform's separator). The probe is faked as well, so nothing is
/// run; <see cref="PgToolLiveTests"/> runs the real one.
/// </summary>
public class PgToolLocatorTests
{
    /// <summary>A machine made of a set of directories and files.</summary>
    private sealed class FakeMachine(PgToolPlatform platform)
    {
        public Dictionary<string, string> Variables { get; } = new(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> Directories { get; } = new(StringComparer.OrdinalIgnoreCase);

        private char Separator => platform == PgToolPlatform.Windows ? '\\' : '/';

        /// <summary>Puts pg_dump and pg_restore in <paramref name="directory"/>.</summary>
        public FakeMachine WithTools(string directory)
        {
            var suffix = platform == PgToolPlatform.Windows ? ".exe" : "";
            AddDirectory(directory);
            Files.Add(directory + Separator + "pg_dump" + suffix);
            Files.Add(directory + Separator + "pg_restore" + suffix);
            return this;
        }

        public FakeMachine AddDirectory(string directory)
        {
            var path = directory;
            while (path.Length > 0 && Directories.Add(path))
            {
                var cut = path.LastIndexOf(Separator);
                if (cut <= 0)
                {
                    break;
                }

                path = path[..cut];
            }

            return this;
        }

        public PgToolHost Host => new(
            platform,
            name => Variables.GetValueOrDefault(name),
            parent => Directories.Where(d => d.StartsWith(parent.TrimEnd(Separator) + Separator, StringComparison.OrdinalIgnoreCase)
                                             && d.LastIndexOf(Separator) == parent.TrimEnd(Separator).Length),
            Files.Contains,
            Directories.Contains);
    }

    private static Func<PgToolCandidate, CancellationToken, Task<PgToolProbeResult>> Versions(params (string Directory, PgVersion? Version)[] versions) =>
        (candidate, _) =>
        {
            foreach (var (directory, version) in versions)
            {
                if (string.Equals(directory, candidate.Directory, StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult(new PgToolProbeResult(version, version is null ? "it doesn't start: a library it needs is missing" : null));
                }
            }

            throw new InvalidOperationException($"No version for {candidate.Directory}");
        };

    private static FakeMachine Windows()
    {
        var machine = new FakeMachine(PgToolPlatform.Windows);
        machine.Variables["ProgramFiles"] = @"C:\Program Files";
        machine.Variables["ProgramW6432"] = @"C:\Program Files";
        machine.Variables["ProgramFiles(x86)"] = @"C:\Program Files (x86)";
        machine.Variables["LOCALAPPDATA"] = @"C:\Users\me\AppData\Local";
        machine.Variables["USERPROFILE"] = @"C:\Users\me";
        return machine;
    }

    [Test]
    public async Task On_Windows_PostgreSQL_installs_come_first_and_newest_first_with_pgAdmin_last()
    {
        var machine = Windows()
            .WithTools(@"C:\Program Files\PostgreSQL\15\bin")
            .WithTools(@"C:\Program Files\PostgreSQL\17\bin")
            .WithTools(@"C:\Program Files\pgAdmin 4\runtime")
            .AddDirectory(@"C:\Program Files\pgAdmin 4\python");
        machine.Variables["PATH"] = @"C:\Windows\system32;C:\Program Files\PostgreSQL\17\bin\;relative\bin";

        var candidates = PgToolLocator.Candidates(machine.Host, configuredDirectory: null);
        var directories = candidates.Select(c => c.Directory).ToList();

        await Assert.That(directories.IndexOf(@"C:\Program Files\PostgreSQL\17\bin")).IsLessThan(directories.IndexOf(@"C:\Program Files\PostgreSQL\15\bin"));
        await Assert.That(directories.IndexOf(@"C:\Program Files\PostgreSQL\15\bin")).IsLessThan(directories.IndexOf(@"C:\Program Files\pgAdmin 4\runtime"));
        // The PATH entry naming 17's folder again is the same folder.
        await Assert.That(directories.Count(d => d.StartsWith(@"C:\Program Files\PostgreSQL\17", StringComparison.Ordinal))).IsEqualTo(1);
        // A relative PATH entry is never a place to run programs from.
        await Assert.That(directories).DoesNotContain(@"relative\bin");

        var pgAdmin = candidates.Single(c => c.Directory == @"C:\Program Files\pgAdmin 4\runtime");
        await Assert.That(pgAdmin.Source).IsEqualTo("pgAdmin 4");
        await Assert.That(pgAdmin.ExtraPath).IsEquivalentTo([@"C:\Program Files\pgAdmin 4\python"]);
    }

    [Test]
    public async Task The_newest_working_install_wins_and_a_tie_keeps_the_plain_install()
    {
        var machine = Windows()
            .WithTools(@"C:\Program Files\PostgreSQL\15\bin")
            .WithTools(@"C:\Program Files\PostgreSQL\18\bin")
            .WithTools(@"C:\Program Files\pgAdmin 4\runtime");

        var scan = await PgToolLocator.ScanAsync(
            machine.Host,
            configuredDirectory: null,
            Versions(
                (@"C:\Program Files\PostgreSQL\15\bin", new PgVersion(15, 3)),
                (@"C:\Program Files\PostgreSQL\18\bin", new PgVersion(18, 6)),
                (@"C:\Program Files\pgAdmin 4\runtime", new PgVersion(18, 6))),
            CancellationToken.None);

        await Assert.That(scan.Newest!.Directory).IsEqualTo(@"C:\Program Files\PostgreSQL\18\bin");
        await Assert.That(scan.For(new PgVersion(17, 4))!.Directory).IsEqualTo(@"C:\Program Files\PostgreSQL\18\bin");
        await Assert.That(scan.Installs.Select(i => i.Version.Major)).IsEquivalentTo([18, 18, 15], CollectionOrdering.Matching);
    }

    [Test]
    public async Task A_server_newer_than_every_install_gets_none()
    {
        var machine = Windows().WithTools(@"C:\Program Files\PostgreSQL\15\bin");
        var scan = await PgToolLocator.ScanAsync(
            machine.Host, null, Versions((@"C:\Program Files\PostgreSQL\15\bin", new PgVersion(15, 3))), CancellationToken.None);

        await Assert.That(scan.For(new PgVersion(17, 0))).IsNull();
        await Assert.That(scan.Newest!.Version).IsEqualTo(new PgVersion(15, 3));
    }

    [Test]
    public async Task A_program_that_does_not_start_is_reported_not_offered()
    {
        var machine = Windows().WithTools(@"C:\Program Files\pgAdmin 4\runtime");
        var scan = await PgToolLocator.ScanAsync(
            machine.Host, null, Versions((@"C:\Program Files\pgAdmin 4\runtime", null)), CancellationToken.None);

        await Assert.That(scan.Installs).IsEmpty();
        await Assert.That(scan.Problems.Single().Directory).IsEqualTo(@"C:\Program Files\pgAdmin 4\runtime");
    }

    [Test]
    public async Task A_folder_with_pg_dump_but_no_pg_restore_is_reported()
    {
        var machine = Windows().AddDirectory(@"C:\tools\pg");
        machine.Files.Add(@"C:\tools\pg\pg_dump.exe");
        machine.Variables["PATH"] = @"C:\tools\pg";

        var scan = await PgToolLocator.ScanAsync(machine.Host, null, Versions(), CancellationToken.None);

        await Assert.That(scan.Problems.Single().Reason).Contains("pg_restore");
    }

    [Test]
    public async Task A_configured_folder_is_the_only_place_looked_in()
    {
        var machine = Windows()
            .WithTools(@"C:\Program Files\PostgreSQL\18\bin")
            .WithTools(@"D:\pg\16\bin");

        var scan = await PgToolLocator.ScanAsync(
            machine.Host, @"D:\pg\16\bin\", Versions((@"D:\pg\16\bin", new PgVersion(16, 2))), CancellationToken.None);

        await Assert.That(scan.Installs.Single().Directory).IsEqualTo(@"D:\pg\16\bin");
        await Assert.That(scan.ConfiguredDirectory).IsEqualTo(@"D:\pg\16\bin");

        // A configured folder without the programs says so, rather than nothing.
        var empty = await PgToolLocator.ScanAsync(machine.Host, @"D:\nothing", Versions(), CancellationToken.None);
        await Assert.That(empty.Problems.Single().Reason).Contains("no pg_dump");
    }

    [Test]
    public async Task A_configured_pgAdmin_runtime_folder_gets_its_library_folder_too()
    {
        var machine = Windows()
            .WithTools(@"C:\Program Files\pgAdmin 4\runtime")
            .AddDirectory(@"C:\Program Files\pgAdmin 4\python");

        var candidate = PgToolLocator.Candidates(machine.Host, @"C:\Program Files\pgAdmin 4\runtime").Single();

        await Assert.That(candidate.ExtraPath).IsEquivalentTo([@"C:\Program Files\pgAdmin 4\python"]);
    }

    [Test]
    public async Task On_macOS_Homebrew_and_PostgresApp_are_found_without_PATH()
    {
        var machine = new FakeMachine(PgToolPlatform.MacOS)
            .WithTools("/opt/homebrew/opt/libpq/bin")
            .WithTools("/Applications/Postgres.app/Contents/Versions/17/bin")
            .WithTools("/Applications/Postgres.app/Contents/Versions/16/bin");
        // An app started from the Finder sees this PATH, which has neither.
        machine.Variables["PATH"] = "/usr/bin:/bin:/usr/sbin:/sbin";
        machine.Variables["HOME"] = "/Users/me";

        var candidates = PgToolLocator.Candidates(machine.Host, null);
        var homebrew = candidates.Single(c => c.Directory == "/opt/homebrew/opt/libpq/bin");
        var directories = candidates.Select(c => c.Directory).ToList();

        await Assert.That(homebrew.Source).IsEqualTo("Homebrew");
        await Assert.That(directories.IndexOf("/Applications/Postgres.app/Contents/Versions/17/bin"))
            .IsLessThan(directories.IndexOf("/Applications/Postgres.app/Contents/Versions/16/bin"));
    }

    [Test]
    public async Task On_Linux_the_versioned_Debian_folders_come_before_the_wrapper_in_usr_bin()
    {
        var machine = new FakeMachine(PgToolPlatform.Linux)
            .WithTools("/usr/lib/postgresql/9.6/bin")
            .WithTools("/usr/lib/postgresql/18/bin")
            .WithTools("/usr/bin");
        machine.Variables["PATH"] = "/usr/local/bin:/usr/bin:/bin";

        var directories = PgToolLocator.Candidates(machine.Host, null).Select(c => c.Directory).ToList();

        await Assert.That(directories[0]).IsEqualTo("/usr/lib/postgresql/18/bin");
        await Assert.That(directories[1]).IsEqualTo("/usr/lib/postgresql/9.6/bin");
        await Assert.That(directories.IndexOf("/usr/bin")).IsGreaterThan(1);
        await Assert.That(directories.Count(d => d == "/usr/bin")).IsEqualTo(1);
    }

    [Test]
    public async Task A_failed_version_check_is_described_in_words()
    {
        await Assert.That(PgToolLocator.DescribeFailure(new PgToolOutput(-1073741515, "", ""))).Contains("library it needs is missing");
        await Assert.That(PgToolLocator.DescribeFailure(new PgToolOutput(-1, "", ""))).Contains("didn't answer");
        await Assert.That(PgToolLocator.DescribeFailure(new PgToolOutput(2, "", "boom\nmore"))).IsEqualTo("it exited with code 2: boom");
    }
}
