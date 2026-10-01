using System.Globalization;
using System.Text;
using Npgsql;
using NpgsqlTypes;
using PgNimbus.Core.Export;
using PgNimbus.Core.Schema;

namespace PgNimbus.Core.Tests.Export;

/// <summary>
/// Range and multirange values come back from Npgsql as <see cref="NpgsqlRange{T}"/>
/// (an array of them for a multirange), whose <c>ToString</c> writes each bound
/// in the process culture. An export of a <c>tstzrange</c> came out as
/// <c>[07/22/2026 19:56:13,07/25/2026 19:56:13)</c>: US order, the fraction of a
/// second gone, and no zone, beside a timestamptz written as ISO in the same row.
/// Every text shape must write the bounds the way the scalar cells are written,
/// in any culture, and as a literal Postgres reads back.
/// </summary>
public sealed class ResultExporterRangeTests
{
    // 2026-07-22 19:56:13.543613 UTC, as Npgsql hands a tstzrange bound over.
    private static readonly DateTime From = new DateTime(2026, 7, 22, 19, 56, 13, DateTimeKind.Utc).AddTicks(5_436_130);
    private static readonly DateTime To = new(2026, 7, 25, 19, 56, 13, DateTimeKind.Utc);

    private static NpgsqlRange<DateTime> TstzRange => new(From, true, To, false);

    // Czech writes dates as 22.07.2026 and a decimal comma, so a culture leak
    // shows up in both kinds of bound.
    private static T InCzech<T>(Func<T> write)
    {
        var saved = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("cs-CZ");
        try
        {
            return write();
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }
    }

    private static string Csv(object? value, string? type = null) => InCzech(() =>
    {
        var writer = new StringWriter();
        ResultExporter.WriteCsv(writer, ["v"], [[value]], columnTypes: [type]);
        return writer.ToString()["v\r\n".Length..^2];
    });

    private static string Tsv(object? value, string? type = null) => InCzech(() =>
    {
        var writer = new StringWriter();
        ResultExporter.WriteTsv(writer, ["v"], [[value]], columnTypes: [type]);
        return writer.ToString()["v\n".Length..^1];
    });

    private static string Insert(object? value, string? type = null) => InCzech(() =>
    {
        var writer = new StringWriter();
        ResultExporter.WriteInsert(writer, "t", ["v"], [[value]], columnTypes: [type]);
        return writer.ToString();
    });

    private static string Json(object? value, string? type = null) => InCzech(() =>
    {
        using var stream = new MemoryStream();
        ResultExporter.WriteJson(stream, ["v"], [[value]], columnTypes: [type]);
        return Encoding.UTF8.GetString(stream.ToArray());
    });

    [Test]
    public async Task A_tstzrange_is_written_like_the_timestamptz_beside_it()
    {
        // The scalar cell in the same row is "O"; each bound is too.
        await Assert.That(Csv(From)).IsEqualTo("2026-07-22T19:56:13.5436130Z");
        await Assert.That(Csv(TstzRange, "tstzrange"))
            .IsEqualTo("\"[2026-07-22T19:56:13.5436130Z,2026-07-25T19:56:13.0000000Z)\"");
        await Assert.That(Tsv(TstzRange, "tstzrange"))
            .IsEqualTo("[2026-07-22T19:56:13.5436130Z,2026-07-25T19:56:13.0000000Z)");
    }

    [Test]
    public async Task Bounds_never_take_the_process_culture()
    {
        await Assert.That(Tsv(new NpgsqlRange<decimal>(1.5m, true, 2.25m, false), "numrange")).IsEqualTo("[1.5,2.25)");
        await Assert.That(Tsv(new NpgsqlRange<double>(0.5, true, 1.5, true))).IsEqualTo("[0.5,1.5]");
        await Assert.That(Tsv(new NpgsqlRange<DateOnly>(new DateOnly(2026, 7, 20), true, new DateOnly(2026, 7, 25), false), "daterange"))
            .IsEqualTo("[2026-07-20,2026-07-25)");
        // A tsrange bound has no zone, so none is written.
        var local = new NpgsqlRange<DateTime>(new DateTime(2026, 7, 22, 19, 56, 13), true, new DateTime(2026, 7, 25, 0, 0, 0), false);
        await Assert.That(Tsv(local, "tsrange")).IsEqualTo("[2026-07-22T19:56:13.0000000,2026-07-25T00:00:00.0000000)");
    }

    [Test]
    public async Task Empty_and_unbounded_ranges_are_written_as_postgres_writes_them()
    {
        await Assert.That(Tsv(NpgsqlRange<int>.Empty, "int4range")).IsEqualTo("empty");
        await Assert.That(Tsv(new NpgsqlRange<int>(0, false, true, 6, false, false), "int4range")).IsEqualTo("(,6)");
        await Assert.That(Tsv(new NpgsqlRange<long>(3, true, false, 0, false, true), "int8range")).IsEqualTo("[3,)");
    }

    [Test]
    public async Task A_multirange_is_one_literal_and_a_range_array_is_an_array()
    {
        NpgsqlRange<int>[] ranges = [new(1, true, 3, false), new(5, true, 7, false)];

        // Npgsql hands both over as NpgsqlRange<int>[]; only the column type tells them apart.
        await Assert.That(Tsv(ranges, "int4multirange")).IsEqualTo("{[1,3),[5,7)}");
        await Assert.That(Tsv(Array.Empty<NpgsqlRange<int>>(), "int4multirange")).IsEqualTo("{}");
        await Assert.That(Tsv(ranges, "int4range[]")).IsEqualTo("[1,3);[5,7)");

        await Assert.That(Insert(ranges, "int4multirange")).IsEqualTo("INSERT INTO t (v) VALUES ('{[1,3),[5,7)}');\n");
        await Assert.That(Insert(ranges, "int4range[]")).IsEqualTo("INSERT INTO t (v) VALUES ('{\"[1,3)\",\"[5,7)\"}');\n");
    }

    [Test]
    public async Task An_insert_copy_writes_a_literal_postgres_reads()
    {
        await Assert.That(Insert(TstzRange, "tstzrange"))
            .IsEqualTo("INSERT INTO t (v) VALUES ('[2026-07-22T19:56:13.5436130Z,2026-07-25T19:56:13.0000000Z)');\n");
        // An array was written as its CSV form ('1;2'), which no array column takes.
        await Assert.That(Insert(new[] { 1, 2 }, "integer[]")).IsEqualTo("INSERT INTO t (v) VALUES ('{1,2}');\n");
        await Assert.That(Insert(new[] { 1.5m }, "numeric[]")).IsEqualTo("INSERT INTO t (v) VALUES ('{1.5}');\n");
    }

    [Test]
    public async Task Json_writes_a_range_as_its_literal()
    {
        await Assert.That(Json(TstzRange, "tstzrange"))
            .Contains("\"v\": \"[2026-07-22T19:56:13.5436130Z,2026-07-25T19:56:13.0000000Z)\"");
        await Assert.That(Json(new NpgsqlRange<int>[] { new(1, true, 3, false) }, "int4multirange")).Contains("\"v\": \"{[1,3)}\"");
    }

    [Test]
    public async Task A_date_is_iso_on_its_own_too()
    {
        // A daterange bound is a DateOnly, and so is a date read as one; the
        // invariant culture alone would write 07/20/2026.
        await Assert.That(Csv(new DateOnly(2026, 7, 20), "date")).IsEqualTo("2026-07-20");
        await Assert.That(Json(new DateOnly(2026, 7, 20), "date")).Contains("\"v\": \"2026-07-20\"");
    }

    [Test]
    public async Task Filtering_by_a_range_cell_writes_the_range_postgres_wrote()
    {
        var text = InCzech(() => PgNimbus.Core.Query.RowFilterSql.ValueText(TstzRange));
        await Assert.That(text).IsEqualTo("[\"2026-07-22 19:56:13.543613+00\",\"2026-07-25 19:56:13+00\")");
    }

    [Test]
    public async Task A_bound_that_needs_quotes_gets_them()
    {
        // Postgres quotes a bound holding a space, comma, bracket, quote or backslash.
        await Assert.That(PgValueSyntax.QuoteRangeBound("a b")).IsEqualTo("\"a b\"");
        await Assert.That(PgValueSyntax.QuoteRangeBound("a\"b\\c")).IsEqualTo("\"a\"\"b\\\\c\"");
        await Assert.That(PgValueSyntax.QuoteRangeBound("")).IsEqualTo("\"\"");
        await Assert.That(PgValueSyntax.QuoteRangeBound("2026-07-20")).IsEqualTo("2026-07-20");
    }

    // --- Against a real server ----------------------------------------------

    private static readonly string? ConnectionString = Environment.GetEnvironmentVariable("PGNIMBUS_TEST_CONN");

    /// <summary>
    /// Every range and multirange type read from the server, written by the CSV
    /// and INSERT paths under a Czech culture, and cast back by the server to the
    /// value it came from.
    /// </summary>
    [Test]
    [Arguments("int4range", "int4range(1, 5)")]
    [Arguments("int8range", "int8range(1, 5000000000)")]
    [Arguments("numrange", "numrange(1.5, 2.25)")]
    [Arguments("daterange", "daterange('2026-07-20', '2026-07-25')")]
    [Arguments("tsrange", "tsrange('2026-07-22 19:56:13.543613', '2026-07-25 19:56:13')")]
    [Arguments("tstzrange", "tstzrange('2026-07-22 19:56:13.543613+02', '2026-07-25 19:56:13+00')")]
    [Arguments("tstzrange", "'(,\"2026-07-25 19:56:13+00\"]'::tstzrange")]
    [Arguments("int4range", "'empty'::int4range")]
    [Arguments("int4multirange", "'{[1,3),[5,7)}'::int4multirange")]
    [Arguments("datemultirange", "'{[2026-01-01,2026-01-05),[2026-02-01,)}'::datemultirange")]
    [Arguments("tstzmultirange", "'{[\"2026-07-22 19:56:13.543613+00\",\"2026-07-25 19:56:13+00\")}'::tstzmultirange")]
    [Arguments("int4range[]", "ARRAY[int4range(1, 3), int4range(5, 7)]")]
    [Arguments("tstzrange[]", "ARRAY[tstzrange('2026-07-22 19:56:13.5+00', NULL)]")]
    public async Task What_is_written_reads_back_as_the_same_value(string type, string expression)
    {
        if (string.IsNullOrEmpty(ConnectionString))
        {
            Skip.Test("PGNIMBUS_TEST_CONN not set — no Postgres to read ranges back.");
        }

        await using var dataSource = NpgsqlDataSource.Create(ConnectionString!);
        object value;
        string wireType;
        await using (var read = dataSource.CreateCommand($"SELECT {expression}"))
        await using (var reader = await read.ExecuteReaderAsync())
        {
            await reader.ReadAsync();
            value = reader.GetValue(0);
            wireType = reader.GetDataTypeName(0);
        }

        var tsv = Tsv(value, wireType);
        var insert = Insert(value, wireType);
        var literal = insert["INSERT INTO t (v) VALUES (".Length..^");\n".Length];

        // A range array's TSV form is the ';'-joined list every array gets;
        // only its elements are literals.
        var texts = wireType.EndsWith("[]", StringComparison.Ordinal) ? tsv.Split(';') : [tsv];
        foreach (var text in texts)
        {
            var elementType = wireType.TrimEnd('[', ']');
            await using var check = dataSource.CreateCommand(
                $"SELECT @text::{elementType} = ANY(ARRAY[{expression}]::{elementType}[])");
            check.Parameters.AddWithValue("text", text);
            await Assert.That((bool)(await check.ExecuteScalarAsync())!).IsTrue().Because($"{type}: {text}");
        }

        await using var insertCheck = dataSource.CreateCommand($"SELECT {literal}::{wireType} = {expression}");
        await Assert.That((bool)(await insertCheck.ExecuteScalarAsync())!).IsTrue().Because($"{type}: {literal}");
    }
}
