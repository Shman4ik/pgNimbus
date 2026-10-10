using PgNimbus.Core.Backup;

namespace PgNimbus.Core.Tests.Backup;

/// <summary>
/// A child's environment decides where it connects and what language it
/// answers in, so every run starts from the same one: the profile's settings
/// and nothing a shell profile left behind.
/// </summary>
public class PgToolProcessTests
{
    [Test]
    public async Task Inherited_libpq_settings_are_dropped_and_the_password_goes_in_the_environment()
    {
        var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["PGOPTIONS"] = "-c statement_timeout=5s",
            ["PGSERVICE"] = "prod",
            ["PGHOST"] = "elsewhere",
            ["pgpassword"] = "old",
            ["PGPASSFILE"] = "/home/me/.pgpass",
            ["PGSSLROOTCERT"] = "/home/me/ca.pem",
            ["LANGUAGE"] = "de",
            ["LC_ALL"] = "de_DE.UTF-8",
            ["PATH"] = "/usr/bin",
            ["HOME"] = "/home/me",
        };

        PgToolProcess.PrepareEnvironment(environment, ["/opt/pgadmin/python"], "new secret");

        await Assert.That(environment.ContainsKey("PGOPTIONS")).IsFalse();
        await Assert.That(environment.ContainsKey("PGSERVICE")).IsFalse();
        await Assert.That(environment.ContainsKey("PGHOST")).IsFalse();
        await Assert.That(environment["PGPASSWORD"]).IsEqualTo("new secret");
        await Assert.That(environment["PGPASSFILE"]).IsEqualTo("/home/me/.pgpass");
        await Assert.That(environment["PGSSLROOTCERT"]).IsEqualTo("/home/me/ca.pem");
        await Assert.That(environment["LC_ALL"]).IsEqualTo("C");
        await Assert.That(environment.ContainsKey("LANGUAGE")).IsFalse();
        await Assert.That(environment["PATH"]).IsEqualTo("/opt/pgadmin/python" + Path.PathSeparator + "/usr/bin");
        await Assert.That(environment["HOME"]).IsEqualTo("/home/me");
    }

    [Test]
    public async Task No_password_leaves_none_behind()
    {
        var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { ["PGPASSWORD"] = "stale" };

        PgToolProcess.PrepareEnvironment(environment, [], null);

        await Assert.That(environment.ContainsKey("PGPASSWORD")).IsFalse();
    }

    [Test]
    public async Task The_command_line_preview_quotes_only_what_needs_it()
    {
        var line = PgToolProcess.CommandLine(new PgToolInvocation(
            "/usr/bin/pg_dump",
            ["--format=custom", "--file=/backups/shop.dump", "--schema=\"sales\"", "host=db port=5432"],
            [],
            "never shown"));

        var expected = OperatingSystem.IsWindows()
            ? "/usr/bin/pg_dump --format=custom --file=/backups/shop.dump \"--schema=\\\"sales\\\"\" \"host=db port=5432\""
            : "/usr/bin/pg_dump --format=custom --file=/backups/shop.dump '--schema=\"sales\"' 'host=db port=5432'";
        await Assert.That(line).IsEqualTo(expected);
        await Assert.That(line).DoesNotContain("never shown");
    }
}
