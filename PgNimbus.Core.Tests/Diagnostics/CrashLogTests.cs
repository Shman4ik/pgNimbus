using PgNimbus.Core.Diagnostics;

namespace PgNimbus.Core.Tests.Diagnostics;

public class CrashLogTests
{
    private static string NewTempDir() =>
        Path.Combine(Path.GetTempPath(), "pgnimbus-crashlog-tests", Guid.NewGuid().ToString("N"));

    [Test]
    public async Task WritesEntryWithContextAndException()
    {
        var dir = NewTempDir();
        try
        {
            var log = new CrashLog(dir);
            Exception caught;
            try
            {
                throw new InvalidOperationException("boom");
            }
            catch (Exception e)
            {
                caught = e;
            }

            var returnedPath = log.LogCritical("Something failed", caught);

            await Assert.That(returnedPath).IsEqualTo(log.FilePath);
            await Assert.That(File.Exists(log.FilePath)).IsTrue();

            var contents = await File.ReadAllTextAsync(log.FilePath);
            await Assert.That(contents).Contains("CRITICAL");
            await Assert.That(contents).Contains("Something failed");
            await Assert.That(contents).Contains("System.InvalidOperationException");
            await Assert.That(contents).Contains("boom");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task CreatesLogDirectoryOnDemand()
    {
        var dir = NewTempDir();
        try
        {
            // The directory does not exist yet — logging must create it.
            await Assert.That(Directory.Exists(dir)).IsFalse();

            var log = new CrashLog(dir);
            log.LogCritical("first error", null);

            await Assert.That(Directory.Exists(dir)).IsTrue();
            await Assert.That(File.Exists(log.FilePath)).IsTrue();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task AppendsRatherThanOverwrites()
    {
        var dir = NewTempDir();
        try
        {
            var log = new CrashLog(dir);
            log.LogCritical("first error", null);
            log.LogCritical("second error", null);

            var contents = await File.ReadAllTextAsync(log.FilePath);
            await Assert.That(contents).Contains("first error");
            await Assert.That(contents).Contains("second error");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task UnwindsEveryBranchOfAnAggregateException()
    {
        var dir = NewTempDir();
        try
        {
            var log = new CrashLog(dir);
            var aggregate = new AggregateException(
                new InvalidOperationException("first branch"),
                new FormatException("second branch"),
                new TimeoutException("third branch"));

            log.LogCritical("faulted tasks", aggregate);

            var contents = await File.ReadAllTextAsync(log.FilePath);
            await Assert.That(contents).Contains("first branch");
            await Assert.That(contents).Contains("second branch");
            await Assert.That(contents).Contains("third branch");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task UnwindsInnerExceptions()
    {
        var dir = NewTempDir();
        try
        {
            var log = new CrashLog(dir);
            var exception = new InvalidOperationException("outer", new FormatException("inner cause"));

            log.LogCritical("wrapped failure", exception);

            var contents = await File.ReadAllTextAsync(log.FilePath);
            await Assert.That(contents).Contains("outer");
            await Assert.That(contents).Contains("System.FormatException");
            await Assert.That(contents).Contains("inner cause");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // Security audit 2026-09, finding 18: a message that quotes a failed
    // CREATE ROLE … PASSWORD '…' must not put the password in the log.
    [Test]
    public async Task RedactsPasswordLiteralsInMessagesAndContext()
    {
        var exception = new InvalidOperationException(
            "outer",
            new FormatException("syntax error in: ALTER ROLE app PASSWORD 'hunter2' VALID UNTIL"));

        var entry = CrashLog.FormatEntry("Running CREATE ROLE x PASSWORD 'swordfish'", exception);

        await Assert.That(entry).DoesNotContain("hunter2");
        await Assert.That(entry).DoesNotContain("swordfish");
        await Assert.That(entry).Contains("PASSWORD '<redacted>'");
        await Assert.That(entry).Contains("outer");
    }

    [Test]
    [Arguments(@"C:\Users\alice\AppData\Roaming\pgNimbus\logs\pgnimbus.log", @"C:\Users\alice", @"~\AppData\Roaming\pgNimbus\logs\pgnimbus.log")]
    [Arguments("/home/alice/.config/pgNimbus/logs/pgnimbus.log", "/home/alice/", "~/.config/pgNimbus/logs/pgnimbus.log")]
    [Arguments("/home/alice", "/home/alice", "~")]
    [Arguments("/home/alicebob/x.log", "/home/alice", "/home/alicebob/x.log")]
    [Arguments("/tmp/pgNimbus/logs/pgnimbus.log", "/home/alice", "/tmp/pgNimbus/logs/pgnimbus.log")]
    public async Task HomeRelativeHidesTheAccountName(string path, string home, string expected)
    {
        await Assert.That(CrashLog.HomeRelative(path, home)).IsEqualTo(expected);
    }
}
