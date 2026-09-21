using System.Globalization;
using PgNimbus.Core.Schema;

namespace PgNimbus.Core.Query;

/// <summary>A comparison a browse filter can apply to one column.</summary>
public enum FilterOperator
{
    Equals,
    NotEquals,
    Less,
    LessOrEqual,
    Greater,
    GreaterOrEqual,
    Contains,
    NotContains,
    StartsWith,
    EndsWith,
    IsNull,
    IsNotNull,
    IsTrue,
    IsFalse,
}

/// <summary>
/// One typed predicate on a browsed table: <paramref name="Column"/>
/// <paramref name="Operator"/> <paramref name="Value"/>. <paramref name="Value"/>
/// is the text the user typed (or a typed editor produced) and is ignored by the
/// operators that take none (<see cref="FilterOperator.IsNull"/> and friends).
/// </summary>
public sealed record RowFilter(string Column, FilterOperator Operator, string? Value = null);

/// <summary>
/// Turns browse filters into the <c>WHERE</c> text browse mode composes. The SQL
/// is meant to be read: it lands in the editor, where the user can see exactly
/// what ran and carry on from it by hand. So values are inlined as quoted
/// literals rather than parameters, and they are left untyped (<c>'42'</c>, not
/// <c>CAST('42' AS integer)</c>): Postgres resolves an untyped literal against
/// the column it is compared with, which makes the comparison typed without
/// making the text noisy. Postgres stays the parser of record; <see cref="Validate"/>
/// only front-runs the mistakes a client can catch cheaply.
/// </summary>
public static class RowFilterSql
{
    private static readonly FilterOperator[] NullOperators = [FilterOperator.IsNull, FilterOperator.IsNotNull];

    /// <summary>
    /// The operators that make sense for a column, most useful first — the
    /// first entry is what a new filter on that column starts with. Every
    /// column gets the two NULL tests.
    /// </summary>
    public static IReadOnlyList<FilterOperator> OperatorsFor(ColumnValueEditor editor, string dataType) =>
        [.. FamilyOperators(Family(editor, dataType)), .. NullOperators];

    /// <summary>True when <paramref name="op"/> compares against a value (and so needs one).</summary>
    public static bool TakesValue(FilterOperator op) =>
        op is not (FilterOperator.IsNull or FilterOperator.IsNotNull or FilterOperator.IsTrue or FilterOperator.IsFalse);

    /// <summary>A short label for the operator picker ("=", "contains", "is null").</summary>
    public static string Label(FilterOperator op) => op switch
    {
        FilterOperator.Equals => "=",
        FilterOperator.NotEquals => "≠",
        FilterOperator.Less => "<",
        FilterOperator.LessOrEqual => "≤",
        FilterOperator.Greater => ">",
        FilterOperator.GreaterOrEqual => "≥",
        FilterOperator.Contains => "contains",
        FilterOperator.NotContains => "doesn't contain",
        FilterOperator.StartsWith => "starts with",
        FilterOperator.EndsWith => "ends with",
        FilterOperator.IsNull => "is null",
        FilterOperator.IsNotNull => "is not null",
        FilterOperator.IsTrue => "is true",
        FilterOperator.IsFalse => "is false",
        _ => op.ToString(),
    };

    /// <summary>
    /// A client-side error for <paramref name="filter"/>, or null when it can be
    /// sent. Checks that a value-taking operator has a value and, for the
    /// numeric and uuid families, that the value parses — the same checks the
    /// Add-row dialog applies (<see cref="PgValueSyntax.ValidateScalar"/>).
    /// A text search (<c>contains</c> and friends) is exempt: it matches the
    /// value's text, so "4" is a fine thing to look for in an integer column.
    /// </summary>
    public static string? Validate(RowFilter filter, ColumnValueEditor editor, string dataType, string? domainBaseType = null)
    {
        if (!TakesValue(filter.Operator))
        {
            return null;
        }

        if (string.IsNullOrEmpty(filter.Value))
        {
            return $"{filter.Column} {Label(filter.Operator)}: enter a value.";
        }

        if (IsTextSearch(filter.Operator) || editor != ColumnValueEditor.Text)
        {
            return null;
        }

        return PgValueSyntax.ValidateScalar(domainBaseType ?? dataType, filter.Value) is { } error
            ? $"{filter.Column}: {error}"
            : null;
    }

    /// <summary>
    /// The predicate for one filter, e.g. <c>"price" &gt;= '10'</c>,
    /// <c>"name" ILIKE '%ann%'</c> or <c>"deleted_at" IS NULL</c>. Text searches
    /// on a non-text column compare its text form (<c>"id"::text ILIKE '%4%'</c>),
    /// which is what a user typing into a filter box expects.
    /// </summary>
    public static string ToPredicate(RowFilter filter, ColumnValueEditor editor, string dataType)
    {
        var column = SqlIdentifier.Quote(filter.Column);
        var value = filter.Value ?? string.Empty;
        var textColumn = Family(editor, dataType) == OperatorFamily.Text ? column : $"{column}::text";

        return filter.Operator switch
        {
            FilterOperator.Equals => $"{column} = {SqlLiteral.Quote(value)}",
            FilterOperator.NotEquals => $"{column} <> {SqlLiteral.Quote(value)}",
            FilterOperator.Less => $"{column} < {SqlLiteral.Quote(value)}",
            FilterOperator.LessOrEqual => $"{column} <= {SqlLiteral.Quote(value)}",
            FilterOperator.Greater => $"{column} > {SqlLiteral.Quote(value)}",
            FilterOperator.GreaterOrEqual => $"{column} >= {SqlLiteral.Quote(value)}",
            FilterOperator.Contains => $"{textColumn} ILIKE {SqlLiteral.Quote($"%{EscapeLike(value)}%")}",
            FilterOperator.NotContains => $"{textColumn} NOT ILIKE {SqlLiteral.Quote($"%{EscapeLike(value)}%")}",
            FilterOperator.StartsWith => $"{textColumn} ILIKE {SqlLiteral.Quote($"{EscapeLike(value)}%")}",
            FilterOperator.EndsWith => $"{textColumn} ILIKE {SqlLiteral.Quote($"%{EscapeLike(value)}")}",
            FilterOperator.IsNull => $"{column} IS NULL",
            FilterOperator.IsNotNull => $"{column} IS NOT NULL",
            FilterOperator.IsTrue => $"{column} IS TRUE",
            FilterOperator.IsFalse => $"{column} IS FALSE",
            _ => throw new ArgumentOutOfRangeException(nameof(filter), filter.Operator, "Unknown filter operator."),
        };
    }

    /// <summary>
    /// ANDs predicates into one <c>WHERE</c> body, parenthesizing each so a raw
    /// predicate containing <c>OR</c> (a hand-seeded one, say) can't swallow its
    /// neighbours. A lone predicate is returned as-is. Blank entries are skipped;
    /// null when nothing is left.
    /// </summary>
    public static string? Combine(IEnumerable<string> predicates)
    {
        var parts = predicates.Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
        return parts.Count switch
        {
            0 => null,
            1 => parts[0],
            _ => string.Join("\n  AND ", parts.Select(p => $"({p})")),
        };
    }

    /// <summary>
    /// A result value as filter text Postgres parses back to the same value —
    /// what "filter by this cell" pre-fills. Dates and times are written ISO
    /// rather than through <c>ToString</c> (culture-shaped, and ambiguous under
    /// a DMY <c>DateStyle</c>), and a UTC <see cref="DateTime"/> — how Npgsql
    /// hands back a timestamptz — carries an explicit <c>+00</c>, since an
    /// offset-less literal would be read in the session's time zone instead.
    /// </summary>
    public static string ValueText(object value) => value switch
    {
        DateTime { Kind: DateTimeKind.Utc } dt => dt.ToString("yyyy-MM-dd HH:mm:ss.FFFFFF", CultureInfo.InvariantCulture) + "+00",
        DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss.FFFFFF", CultureInfo.InvariantCulture),
        DateTimeOffset dto => dto.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFzzz", CultureInfo.InvariantCulture),
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        TimeOnly t => t.ToString("HH:mm:ss.FFFFFF", CultureInfo.InvariantCulture),
        bool b => b ? "true" : "false",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    // Escapes LIKE's wildcards so a typed "50%" matches the text "50%" and not
    // everything starting with "50". Backslash is LIKE's default escape
    // character, and with standard_conforming_strings on (the default since 9.1)
    // it reaches LIKE unchanged inside a plain literal.
    private static string EscapeLike(string value) =>
        value.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");

    private static bool IsTextSearch(FilterOperator op) =>
        op is FilterOperator.Contains or FilterOperator.NotContains or FilterOperator.StartsWith or FilterOperator.EndsWith;

    private enum OperatorFamily
    {
        Text,
        Ordered,
        Boolean,
        Enum,
        Other,
    }

    private static OperatorFamily Family(ColumnValueEditor editor, string dataType) => editor switch
    {
        ColumnValueEditor.Boolean => OperatorFamily.Boolean,
        ColumnValueEditor.Enum => OperatorFamily.Enum,
        ColumnValueEditor.Date or ColumnValueEditor.Timestamp => OperatorFamily.Ordered,
        ColumnValueEditor.Text => PgTypeCategorizer.Categorize(dataType) switch
        {
            PgTypeCategory.Text => OperatorFamily.Text,
            PgTypeCategory.Numeric or PgTypeCategory.DateTime or PgTypeCategory.Uuid => OperatorFamily.Ordered,
            _ => OperatorFamily.Other,
        },
        _ => OperatorFamily.Other,
    };

    private static FilterOperator[] FamilyOperators(OperatorFamily family) => family switch
    {
        OperatorFamily.Text =>
        [
            FilterOperator.Contains, FilterOperator.Equals, FilterOperator.NotEquals, FilterOperator.StartsWith,
            FilterOperator.EndsWith, FilterOperator.NotContains,
        ],
        OperatorFamily.Ordered =>
        [
            FilterOperator.Equals, FilterOperator.NotEquals, FilterOperator.Greater, FilterOperator.GreaterOrEqual,
            FilterOperator.Less, FilterOperator.LessOrEqual,
        ],
        OperatorFamily.Boolean => [FilterOperator.IsTrue, FilterOperator.IsFalse],
        OperatorFamily.Enum => [FilterOperator.Equals, FilterOperator.NotEquals],
        // json, arrays, composites, network/geometric/range types, …: equality
        // isn't defined for all of them (json has no = operator), so the safe
        // default is a search over the value's text form.
        _ => [FilterOperator.Contains, FilterOperator.NotContains],
    };
}
