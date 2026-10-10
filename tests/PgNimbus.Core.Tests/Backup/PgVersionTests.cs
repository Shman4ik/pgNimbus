using PgNimbus.Core.Backup;

namespace PgNimbus.Core.Tests.Backup;

/// <summary>
/// The version rule decides which pg_dump a backup runs with, and a wrong
/// answer is either a refused dump ("server version mismatch") or an archive
/// the restore can't read, so the parsing is held to every shape the
/// programs and archives actually print.
/// </summary>
public class PgVersionTests
{
    [Test]
    [Arguments("pg_dump (PostgreSQL) 18.6", 18, 6)]
    [Arguments("pg_dump (PostgreSQL) 17.2 (Debian 17.2-1.pgdg120+1)", 17, 2)]
    [Arguments("pg_restore (PostgreSQL) 16.10 (Homebrew)", 16, 10)]
    [Arguments("pg_dump (PostgreSQL) 9.6.24", 9, 6)]
    [Arguments("pg_dump (PostgreSQL) 18beta1", 18, 0)]
    [Arguments("pg_dump (PostgreSQL) 19devel\r\n", 19, 0)]
    public async Task Reads_what_the_programs_print(string output, int major, int minor)
    {
        await Assert.That(PgVersion.TryParseToolOutput(output, out var version)).IsTrue();
        await Assert.That(version).IsEqualTo(new PgVersion(major, minor));
    }

    [Test]
    [Arguments("")]
    [Arguments("pg_dump: command not found")]
    [Arguments("mysqldump  Ver 8.0.36 for Linux")]
    [Arguments("pg_dump 17.2")]
    public async Task Anything_else_is_not_a_version(string output)
    {
        await Assert.That(PgVersion.TryParseToolOutput(output, out _)).IsFalse();
    }

    [Test]
    public async Task A_pg_dump_backs_up_its_own_major_release_and_older_ones()
    {
        var tool = new PgVersion(17, 0);
        await Assert.That(tool.CanDump(new PgVersion(17, 9))).IsTrue();
        await Assert.That(tool.CanDump(new PgVersion(13, 4))).IsTrue();
        await Assert.That(tool.CanDump(new PgVersion(9, 6))).IsTrue();
        await Assert.That(tool.CanDump(new PgVersion(18, 0))).IsFalse();

        // Before 10 the major release was two numbers: 9.5 can't dump 9.6.
        await Assert.That(new PgVersion(9, 5).CanDump(new PgVersion(9, 6))).IsFalse();
        await Assert.That(new PgVersion(9, 6).CanDump(new PgVersion(9, 6))).IsTrue();
    }

    [Test]
    public async Task Major_labels_are_written_the_way_PostgreSQL_writes_them()
    {
        await Assert.That(new PgVersion(17, 4).MajorLabel).IsEqualTo("17");
        await Assert.That(new PgVersion(9, 6).MajorLabel).IsEqualTo("9.6");
        await Assert.That(PgVersion.FromServer(new Version(9, 6, 24))).IsEqualTo(new PgVersion(9, 6));
        await Assert.That(PgVersion.FromServer(new Version(17, 4))).IsEqualTo(new PgVersion(17, 4));
    }

    [Test]
    public async Task An_archive_header_version_is_read_up_to_its_first_word()
    {
        await Assert.That(PgVersion.TryParse("17.11 (Debian 17.11-1.pgdg13+2)", out var server)).IsTrue();
        await Assert.That(server).IsEqualTo(new PgVersion(17, 11));
        await Assert.That(PgVersion.TryParse("18.6", out var tool)).IsTrue();
        await Assert.That(tool).IsEqualTo(new PgVersion(18, 6));
        await Assert.That(PgVersion.TryParse("unknown", out _)).IsFalse();
    }
}
