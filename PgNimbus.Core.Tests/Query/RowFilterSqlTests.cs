using PgNimbus.Core.Query;
using PgNimbus.Core.Schema;

namespace PgNimbus.Core.Tests.Query;

public class RowFilterSqlTests
{
    [Test]
    public async Task TextColumnsLeadWithContainsAndAlwaysOfferNullTests()
    {
        var ops = RowFilterSql.OperatorsFor(ColumnValueEditor.Text, "character varying(80)");

        await Assert.That(ops[0]).IsEqualTo(FilterOperator.Contains);
        await Assert.That(ops).Contains(FilterOperator.IsNull);
        await Assert.That(ops).Contains(FilterOperator.IsNotNull);
        await Assert.That(ops).DoesNotContain(FilterOperator.Greater);
    }

    [Test]
    public async Task NumericAndDateColumnsLeadWithEqualityAndOfferRanges()
    {
        foreach (var (editor, type) in new[]
        {
            (ColumnValueEditor.Text, "numeric(10,2)"),
            (ColumnValueEditor.Text, "bigint"),
            (ColumnValueEditor.Date, "date"),
            (ColumnValueEditor.Timestamp, "timestamp with time zone"),
        })
        {
            var ops = RowFilterSql.OperatorsFor(editor, type);
            await Assert.That(ops[0]).IsEqualTo(FilterOperator.Equals);
            await Assert.That(ops).Contains(FilterOperator.GreaterOrEqual);
            await Assert.That(ops).DoesNotContain(FilterOperator.Contains);
        }
    }

    [Test]
    public async Task BooleansAndEnumsGetOnlyWhatTheirTypesSupport()
    {
        await Assert.That(RowFilterSql.OperatorsFor(ColumnValueEditor.Boolean, "boolean"))
            .IsEquivalentTo(new[] { FilterOperator.IsTrue, FilterOperator.IsFalse, FilterOperator.IsNull, FilterOperator.IsNotNull }, CollectionOrdering.Matching);
        await Assert.That(RowFilterSql.OperatorsFor(ColumnValueEditor.Enum, "mood"))
            .IsEquivalentTo(new[] { FilterOperator.Equals, FilterOperator.NotEquals, FilterOperator.IsNull, FilterOperator.IsNotNull }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task JsonGetsNoEqualityBecauseJsonHasNoEqualsOperator()
    {
        var ops = RowFilterSql.OperatorsFor(ColumnValueEditor.Json, "json");

        await Assert.That(ops[0]).IsEqualTo(FilterOperator.Contains);
        await Assert.That(ops).DoesNotContain(FilterOperator.Equals);
    }

    [Test]
    public async Task ComparisonsInlineAnUntypedQuotedLiteral()
    {
        var sql = RowFilterSql.ToPredicate(new RowFilter("Price", FilterOperator.GreaterOrEqual, "10.5"), ColumnValueEditor.Text, "numeric");

        await Assert.That(sql).IsEqualTo("\"Price\" >= '10.5'");
    }

    [Test]
    public async Task ValuesAndIdentifiersAreQuotedSoTheyCannotEscapeTheLiteral()
    {
        var sql = RowFilterSql.ToPredicate(new RowFilter("we\"ird", FilterOperator.Equals, "O'Brien'; DROP TABLE t; --"), ColumnValueEditor.Text, "text");

        await Assert.That(sql).IsEqualTo("\"we\"\"ird\" = 'O''Brien''; DROP TABLE t; --'");
    }

    [Test]
    public async Task TextSearchEscapesLikeWildcards()
    {
        var sql = RowFilterSql.ToPredicate(new RowFilter("note", FilterOperator.Contains, @"50%_off\"), ColumnValueEditor.Text, "text");

        await Assert.That(sql).IsEqualTo(@"""note"" ILIKE '%50\%\_off\\%'");
    }

    [Test]
    public async Task TextSearchOnANonTextColumnComparesItsTextForm()
    {
        await Assert.That(RowFilterSql.ToPredicate(new RowFilter("id", FilterOperator.StartsWith, "4"), ColumnValueEditor.Text, "integer"))
            .IsEqualTo("\"id\"::text ILIKE '4%'");
        await Assert.That(RowFilterSql.ToPredicate(new RowFilter("payload", FilterOperator.NotContains, "x"), ColumnValueEditor.Json, "jsonb"))
            .IsEqualTo("\"payload\"::text NOT ILIKE '%x%'");
    }

    [Test]
    public async Task NullAndBooleanTestsIgnoreAnyValue()
    {
        await Assert.That(RowFilterSql.ToPredicate(new RowFilter("deleted_at", FilterOperator.IsNull, "ignored"), ColumnValueEditor.Timestamp, "timestamp"))
            .IsEqualTo("\"deleted_at\" IS NULL");
        await Assert.That(RowFilterSql.ToPredicate(new RowFilter("active", FilterOperator.IsFalse), ColumnValueEditor.Boolean, "boolean"))
            .IsEqualTo("\"active\" IS FALSE");
    }

    [Test]
    public async Task ValidateAsksForAValueOnlyWhenTheOperatorTakesOne()
    {
        await Assert.That(RowFilterSql.Validate(new RowFilter("n", FilterOperator.Equals, ""), ColumnValueEditor.Text, "integer")).IsNotNull();
        await Assert.That(RowFilterSql.Validate(new RowFilter("n", FilterOperator.IsNull), ColumnValueEditor.Text, "integer")).IsNull();
    }

    [Test]
    public async Task ValidateRejectsAMalformedNumberButNotATextSearchForOne()
    {
        await Assert.That(RowFilterSql.Validate(new RowFilter("n", FilterOperator.Greater, "abc"), ColumnValueEditor.Text, "integer")).IsNotNull();
        await Assert.That(RowFilterSql.Validate(new RowFilter("n", FilterOperator.Contains, "abc"), ColumnValueEditor.Text, "integer")).IsNull();
        await Assert.That(RowFilterSql.Validate(new RowFilter("n", FilterOperator.Greater, "42"), ColumnValueEditor.Text, "integer")).IsNull();
    }

    [Test]
    public async Task ValidateChecksADomainAgainstItsBaseType()
    {
        await Assert.That(RowFilterSql.Validate(new RowFilter("qty", FilterOperator.Equals, "many"), ColumnValueEditor.Text, "posint", "integer")).IsNotNull();
    }

    [Test]
    public async Task CombineParenthesizesSoAnOrCannotSwallowItsNeighbours()
    {
        await Assert.That(RowFilterSql.Combine(["a = 1 OR b = 2", "c IS NULL"]))
            .IsEqualTo("(a = 1 OR b = 2)\n  AND (c IS NULL)");
        await Assert.That(RowFilterSql.Combine(["a = 1 OR b = 2"])).IsEqualTo("a = 1 OR b = 2");
        await Assert.That(RowFilterSql.Combine(["", "  "])).IsNull();
    }

    [Test]
    public async Task ValueTextWritesDatesIsoAndMarksUtcSoTheSessionZoneCannotShiftIt()
    {
        await Assert.That(RowFilterSql.ValueText(new DateTime(2026, 7, 14, 8, 30, 0, DateTimeKind.Utc))).IsEqualTo("2026-07-14 08:30:00+00");
        await Assert.That(RowFilterSql.ValueText(new DateTime(2026, 7, 14, 8, 30, 0, 500))).IsEqualTo("2026-07-14 08:30:00.5");
        await Assert.That(RowFilterSql.ValueText(new DateOnly(2026, 7, 14))).IsEqualTo("2026-07-14");
        await Assert.That(RowFilterSql.ValueText(12.5m)).IsEqualTo("12.5");
        await Assert.That(RowFilterSql.ValueText(true)).IsEqualTo("true");
    }
}
