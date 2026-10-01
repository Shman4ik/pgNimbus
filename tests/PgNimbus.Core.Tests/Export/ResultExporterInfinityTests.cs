using System.Text;
using Npgsql;
using NpgsqlTypes;
using PgNimbus.Core.Export;

namespace PgNimbus.Core.Tests.Export;

/// <summary>
/// Npgsql reads a Postgres <c>infinity</c> or <c>-infinity</c> timestamp or date
/// as the largest or smallest <see cref="DateTime"/> (and <see cref="DateOnly"/>).
/// Written with <c>"O"</c>, <c>infinity</c> came out of every export and copy as
/// <c>9999-12-31T23:59:59.9999999</c>, which Postgres reads back as a finite
/// timestamp (rounded up into the year 10000), and <c>-infinity</c> as the year 1.
/// The grid already showed the words; export and copy now write them too, in a
/// scalar cell, an array element and a range bound alike.
/// </summary>
public sealed class ResultExporterInfinityTests
{
    // How Npgsql hands over a timestamptz: UTC kind. A timestamp is Unspecified.
    private static readonly DateTime UtcMax = DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc);
    private static readonly DateTime UtcMin = DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc);
    private static readonly DateTime Finite = new(2026, 7, 22, 19, 56, 13, DateTimeKind.Utc);

    private static string Csv(object? value, string? type = null, bool spreadsheetSafe = false)
    {
        var writer = new StringWriter();
        ResultExporter.WriteCsv(writer, ["v"], [[value]], spreadsheetSafe, [type]);
        return writer.ToString()["v\r\n".Length..^2];
    }

    private static string Tsv(object? value, string? type = null)
    {
        var writer = new StringWriter();
        ResultExporter.WriteTsv(writer, ["v"], [[value]], columnTypes: [type]);
        return writer.ToString()["v\n".Length..^1];
    }

    private static string Insert(object? value, string? type = null)
    {
        var writer = new StringWriter();
        ResultExporter.WriteInsert(writer, "t", ["v"], [[value]], columnTypes: [type]);
        return writer.ToString()["INSERT INTO t (v) VALUES (".Length..^");\n".Length];
    }

    private static string Markdown(object? value)
    {
        var writer = new StringWriter();
        ResultExporter.WriteMarkdown(writer, ["v"], [[value]]);
        return writer.ToString();
    }

    private static string Json(object? value, string? type = null)
    {
        using var stream = new MemoryStream();
        ResultExporter.WriteJson(stream, ["v"], [[value]], columnTypes: [type]);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    [Test]
    public async Task A_timestamp_at_infinity_is_written_as_the_word()
    {
        await Assert.That(Csv(DateTime.MaxValue, "timestamp without time zone")).IsEqualTo("infinity");
        await Assert.That(Csv(DateTime.MinValue, "timestamp without time zone")).IsEqualTo("-infinity");
        await Assert.That(Tsv(UtcMax, "timestamp with time zone")).IsEqualTo("infinity");
        await Assert.That(Tsv(UtcMin, "timestamp with time zone")).IsEqualTo("-infinity");
        await Assert.That(Markdown(UtcMax)).Contains("| infinity |");

        // A finite value beside it is still ISO.
        await Assert.That(Tsv(Finite, "timestamp with time zone")).IsEqualTo("2026-07-22T19:56:13.0000000Z");
    }

    [Test]
    public async Task A_date_at_infinity_is_written_as_the_word()
    {
        await Assert.That(Tsv(DateOnly.MaxValue, "date")).IsEqualTo("infinity");
        await Assert.That(Tsv(DateOnly.MinValue, "date")).IsEqualTo("-infinity");
        // A date column read through GetValue is a DateTime.
        await Assert.That(Tsv(DateTime.MaxValue, "date")).IsEqualTo("infinity");
    }

    [Test]
    public async Task A_datetimeoffset_at_infinity_is_written_as_the_word()
    {
        await Assert.That(Tsv(DateTimeOffset.MaxValue)).IsEqualTo("infinity");
        await Assert.That(Tsv(DateTimeOffset.MinValue)).IsEqualTo("-infinity");
    }

    [Test]
    public async Task Insert_and_json_write_the_word_as_a_string()
    {
        await Assert.That(Insert(UtcMax, "timestamp with time zone")).IsEqualTo("'infinity'");
        await Assert.That(Insert(DateOnly.MinValue, "date")).IsEqualTo("'-infinity'");
        await Assert.That(Json(UtcMax, "timestamp with time zone")).Contains("\"v\": \"infinity\"");
        await Assert.That(Json(DateTime.MinValue, "timestamp without time zone")).Contains("\"v\": \"-infinity\"");
        await Assert.That(Json(DateTimeOffset.MaxValue)).Contains("\"v\": \"infinity\"");
        await Assert.That(Json(DateOnly.MaxValue, "date")).Contains("\"v\": \"infinity\"");
    }

    [Test]
    public async Task An_array_element_at_infinity_is_written_as_the_word()
    {
        DateTime[] stamps = [DateTime.MinValue, Finite, UtcMax];

        await Assert.That(Tsv(stamps, "timestamp with time zone[]"))
            .IsEqualTo("-infinity;2026-07-22T19:56:13.0000000Z;infinity");
        await Assert.That(Insert(stamps, "timestamp with time zone[]"))
            .IsEqualTo("'{-infinity,2026-07-22T19:56:13.0000000Z,infinity}'");
        await Assert.That(Json(stamps, "timestamp with time zone[]")).Contains("\"-infinity\"");
        await Assert.That(Json(stamps, "timestamp with time zone[]")).Contains("\"infinity\"");
    }

    [Test]
    public async Task A_range_bound_at_infinity_is_written_as_the_word()
    {
        // An infinite bound is still a bound ([x,infinity) is not [x,)), so it
        // must survive as the word, not as a missing bound.
        await Assert.That(Tsv(new NpgsqlRange<DateTime>(Finite, true, UtcMax, false), "tstzrange"))
            .IsEqualTo("[2026-07-22T19:56:13.0000000Z,infinity)");
        await Assert.That(Tsv(new NpgsqlRange<DateTime>(DateTime.MinValue, true, DateTime.MaxValue, false), "tsrange"))
            .IsEqualTo("[-infinity,infinity)");
        await Assert.That(Tsv(new NpgsqlRange<DateOnly>(new DateOnly(2026, 1, 1), true, DateOnly.MaxValue, false), "daterange"))
            .IsEqualTo("[2026-01-01,infinity)");
        await Assert.That(Insert(new NpgsqlRange<DateTime>(UtcMin, true, Finite, false), "tstzrange"))
            .IsEqualTo("'[-infinity,2026-07-22T19:56:13.0000000Z)'");
        await Assert.That(Json(new NpgsqlRange<DateTime>(Finite, true, UtcMax, false), "tstzrange"))
            .Contains("\"v\": \"[2026-07-22T19:56:13.0000000Z,infinity)\"");

        NpgsqlRange<DateOnly>[] multirange = [new(new DateOnly(2026, 1, 1), true, DateOnly.MaxValue, false)];
        await Assert.That(Tsv(multirange, "datemultirange")).IsEqualTo("{[2026-01-01,infinity)}");
    }

    [Test]
    public async Task Spreadsheet_safe_export_still_guards_minus_infinity()
    {
        // A spreadsheet reads -infinity as a formula (#NAME?), not a number, so
        // it is quoted like any other text starting with '-'.
        await Assert.That(Csv(DateTime.MinValue, "timestamp without time zone", spreadsheetSafe: true)).IsEqualTo("'-infinity");
        await Assert.That(Csv(DateTime.MaxValue, "timestamp without time zone", spreadsheetSafe: true)).IsEqualTo("infinity");
    }

    // --- Against a real server ----------------------------------------------

    private static readonly string? ConnectionString = Environment.GetEnvironmentVariable("PGNIMBUS_TEST_CONN");

    /// <summary>
    /// Infinite values read from the server, written by the TSV, INSERT and JSON
    /// paths, and cast back by the server to the value they came from.
    /// </summary>
    [Test]
    [Arguments("'infinity'::timestamp")]
    [Arguments("'-infinity'::timestamp")]
    [Arguments("'infinity'::timestamptz")]
    [Arguments("'-infinity'::timestamptz")]
    [Arguments("'infinity'::date")]
    [Arguments("'-infinity'::date")]
    [Arguments("ARRAY['-infinity', '2026-07-22 19:56:13.5+00', 'infinity']::timestamptz[]")]
    [Arguments("ARRAY['infinity', '2026-07-22']::date[]")]
    [Arguments("tsrange('-infinity', 'infinity')")]
    [Arguments("tstzrange('2026-07-22 19:56:13.543613+00', 'infinity')")]
    [Arguments("daterange('2026-01-01', 'infinity')")]
    [Arguments("'{[-infinity,2026-01-01),[2026-02-01,infinity)}'::datemultirange")]
    public async Task What_is_written_reads_back_as_the_same_value(string expression)
    {
        if (string.IsNullOrEmpty(ConnectionString))
        {
            Skip.Test("PGNIMBUS_TEST_CONN not set — no Postgres to read infinities back.");
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

        // An array's TSV form is the ';'-joined list every array gets; only its
        // elements are literals. Its INSERT form is an array literal.
        var isArray = wireType.EndsWith("[]", StringComparison.Ordinal);
        var elementType = wireType.TrimEnd('[', ']');
        var tsv = Tsv(value, wireType);
        foreach (var text in isArray ? tsv.Split(';') : [tsv])
        {
            await using var check = dataSource.CreateCommand(
                $"SELECT @text::{elementType} = ANY(ARRAY[{expression}]::{elementType}[])");
            check.Parameters.AddWithValue("text", text);
            await Assert.That((bool)(await check.ExecuteScalarAsync())!).IsTrue().Because($"TSV {wireType}: {text}");
        }

        var literal = Insert(value, wireType);
        await using (var check = dataSource.CreateCommand($"SELECT {literal}::{wireType} = {expression}"))
        {
            await Assert.That((bool)(await check.ExecuteScalarAsync())!).IsTrue().Because($"INSERT {wireType}: {literal}");
        }

        // JSON writes a scalar or a range as a string and an array as an array
        // of them; the server reads either back through jsonb.
        var json = Json(value, wireType);
        await using (var check = dataSource.CreateCommand(isArray
            ? $"SELECT ARRAY(SELECT jsonb_array_elements_text(@json::jsonb -> 0 -> 'v'))::{wireType} = {expression}"
            : $"SELECT (@json::jsonb -> 0 ->> 'v')::{wireType} = {expression}"))
        {
            check.Parameters.AddWithValue("json", json);
            await Assert.That((bool)(await check.ExecuteScalarAsync())!).IsTrue().Because($"JSON {wireType}: {json}");
        }
    }
}
