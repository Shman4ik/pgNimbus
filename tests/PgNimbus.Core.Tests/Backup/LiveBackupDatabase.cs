using System.Diagnostics;
using Npgsql;
using PgNimbus.Core.Backup;

namespace PgNimbus.Core.Tests.Backup;

/// <summary>
/// What the live backup tests share: a database of their own (a whole-database
/// dump of the shared <c>postgres</c> one would race every other live test's
/// scratch tables), and the pg_dump to run.
/// <para>
/// Gated on <c>PGNIMBUS_TEST_CONN</c> like every live test. The programs come
/// from <c>PGNIMBUS_TEST_PG_TOOLS</c> when it is set, and then they are
/// required: CI sets it, so a broken install fails the build rather than
/// skipping the round trip. Without it the machine is searched, and a machine
/// with no pg_dump new enough skips.
/// </para>
/// </summary>
internal static class LiveBackupDatabase
{
    public const string Name = "pgnimbus_backup_test";

    public const string OddSchema = "Odd \"Schema\" *?";

    public static string? AdminConnectionString { get; } = Environment.GetEnvironmentVariable("PGNIMBUS_TEST_CONN");

    public static string? ToolsDirectory { get; } = Environment.GetEnvironmentVariable("PGNIMBUS_TEST_PG_TOOLS");

    public static void SkipIfNoServer()
    {
        if (string.IsNullOrEmpty(AdminConnectionString))
        {
            Skip.Test("PGNIMBUS_TEST_CONN not set: no Postgres to back up.");
        }
    }

    /// <summary>The connection string for <paramref name="database"/> on the test server.</summary>
    public static string ConnectionStringFor(string database) =>
        new NpgsqlConnectionStringBuilder(AdminConnectionString) { Database = database, Pooling = false }.ConnectionString;

    /// <summary>The pg_dump/pg_restore to use against <paramref name="server"/>, or a skip.</summary>
    public static async Task<PgToolInstall> RequireToolsAsync(PgVersion server)
    {
        var scan = await PgToolLocator.ScanAsync(ToolsDirectory, CancellationToken.None);
        if (scan.For(server) is { } install)
        {
            return install;
        }

        var found = string.Join("; ", scan.Installs.Select(i => i.Describe(PgTool.PgDump)).Concat(scan.Problems.Select(p => $"{p.Directory}: {p.Reason}")));
        if (!string.IsNullOrEmpty(ToolsDirectory))
        {
            throw new InvalidOperationException($"PGNIMBUS_TEST_PG_TOOLS={ToolsDirectory} has no pg_dump for PostgreSQL {server}. Found: {found}");
        }

        Skip.Test($"No pg_dump for PostgreSQL {server} on this machine. Found: {found}");
        throw new UnreachableException();
    }

    /// <summary>
    /// Drops and recreates <paramref name="database"/>, then runs
    /// <paramref name="seed"/> in it.
    /// </summary>
    public static async Task CreateAsync(string database, string seed)
    {
        await DropAsync(database);
        await using (var admin = new NpgsqlConnection(ConnectionStringFor("postgres")))
        {
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{database}\"", admin);
            await create.ExecuteNonQueryAsync();
        }

        await using var connection = new NpgsqlConnection(ConnectionStringFor(database));
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(seed, connection);
        await command.ExecuteNonQueryAsync();
    }

    public static async Task DropAsync(string database)
    {
        await using var admin = new NpgsqlConnection(ConnectionStringFor("postgres"));
        await admin.OpenAsync();
        var force = admin.PostgreSqlVersion.Major >= 13 ? " WITH (FORCE)" : "";
        await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{database}\"{force}", admin);
        await drop.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// The shop the round trips save: four tables of rows in three schemas, one
    /// of them named so that only exact quoting matches it, a partitioned table,
    /// a materialized view, a view and the sequences behind serial and identity
    /// columns.
    /// </summary>
    public const string ShopSeed = """"
        CREATE SCHEMA sales;
        CREATE SCHEMA "Odd ""Schema"" *?";
        CREATE TABLE public.customers (id serial PRIMARY KEY, name text NOT NULL, tags text[], meta jsonb);
        CREATE TABLE sales.orders (
            id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            customer_id int REFERENCES public.customers (id),
            placed_at timestamptz DEFAULT now(),
            total numeric(12, 2));
        CREATE TABLE sales.events (id bigint, at date NOT NULL, payload text) PARTITION BY RANGE (at);
        CREATE TABLE sales.events_2026 PARTITION OF sales.events FOR VALUES FROM ('2026-01-01') TO ('2027-01-01');
        CREATE TABLE "Odd ""Schema"" *?"."Weird.Table" ("Col ""x""" int, v text);
        CREATE MATERIALIZED VIEW sales.order_totals AS SELECT customer_id, sum(total) AS total FROM sales.orders GROUP BY 1;
        CREATE VIEW public.active AS SELECT * FROM public.customers WHERE id > 0;
        INSERT INTO public.customers (name, tags, meta)
            SELECT 'c' || g, ARRAY['a', 'b'], jsonb_build_object('n', g) FROM generate_series(1, 2000) g;
        INSERT INTO sales.orders (customer_id, total) SELECT 1 + (g % 2000), g * 1.5 FROM generate_series(1, 20000) g;
        INSERT INTO sales.events SELECT g, '2026-03-01'::date + (g % 200), repeat('x', 100) FROM generate_series(1, 5000) g;
        INSERT INTO "Odd ""Schema"" *?"."Weird.Table" VALUES (1, 'one'), (2, 'two');
        REFRESH MATERIALIZED VIEW sales.order_totals;
        """";

    /// <summary>What <c>pg_restore --list</c> says an archive holds, one entry per line.</summary>
    public static async Task<string> ListAsync(PgToolInstall tool, string archive)
    {
        var output = await PgToolProcess.CaptureAsync(
            new PgToolInvocation(tool.PathOf(PgTool.PgRestore), ["--list", archive], tool.ExtraPath),
            TimeSpan.FromSeconds(60),
            CancellationToken.None);
        if (output.ExitCode != 0)
        {
            throw new InvalidOperationException($"pg_restore --list failed ({output.ExitCode}): {output.StandardError}");
        }

        return output.StandardOutput;
    }
}
