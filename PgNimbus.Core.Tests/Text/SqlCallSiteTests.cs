using PgNimbus.Core.Schema;
using PgNimbus.Core.Text;

namespace PgNimbus.Core.Tests.Text;

/// <summary>
/// The argument hint's reading of a call (docs/design/sql-editing-experience.md,
/// package H, T27): which call and which argument the caret is in, with commas
/// inside strings, arrays, nested calls and subqueries not counting, and which
/// overloads fit. <c>|</c> marks the caret.
/// </summary>
public class SqlCallSiteTests
{
    private static SqlCallSite? At(string marked)
    {
        var caret = marked.IndexOf('|');
        return SqlCallSite.At(marked.Remove(caret, 1), caret);
    }

    [Test]
    [Arguments("SELECT coalesce(|", "coalesce", 0)]
    [Arguments("SELECT coalesce(a, |", "coalesce", 1)]
    [Arguments("SELECT coalesce(a, 'x,y', |", "coalesce", 2)]
    [Arguments("SELECT coalesce(E'a\\',b', |", "coalesce", 1)]
    [Arguments("SELECT coalesce($$a,b$$, |", "coalesce", 1)]
    [Arguments("SELECT coalesce(ARRAY[1, 2, 3], |", "coalesce", 1)]
    [Arguments("SELECT coalesce(ARRAY[1, |", "coalesce", 0)]
    [Arguments("SELECT coalesce(lower(a, b), |", "coalesce", 1)]
    [Arguments("SELECT coalesce(lower(a, |", "lower", 1)]
    [Arguments("SELECT coalesce((SELECT a, b FROM t), |", "coalesce", 1)]
    [Arguments("SELECT coalesce ( a, |", "coalesce", 1)]
    [Arguments("SELECT \"My Func\"(a, |", "My Func", 1)]
    public async Task The_argument_index_ignores_commas_that_belong_to_something_else(string marked, string name, int index)
    {
        var site = At(marked);

        await Assert.That(site).IsNotNull();
        await Assert.That(site!.Name[^1]).IsEqualTo(name);
        await Assert.That(site.ArgumentIndex).IsEqualTo(index);
    }

    [Test]
    public async Task A_qualified_name_keeps_its_schema()
    {
        await Assert.That(At("SELECT Custom.Normalize(|")!.Name).IsEquivalentTo(new[] { "custom", "normalize" });
    }

    [Test]
    [Arguments("SELECT * FROM t WHERE id IN (1, |")]
    [Arguments("INSERT INTO t VALUES (1, |")]
    [Arguments("SELECT * FROM t WHERE EXISTS (SELECT 1, |")]
    [Arguments("SELECT (a, |")]
    [Arguments("SELECT lower(a) |")]
    [Arguments("SELECT lower('a, |")]
    [Arguments("SELECT lower(a) -- (b, |")]
    [Arguments("SELECT lower(a); SELECT |")]
    public async Task Not_a_call_or_not_inside_one(string marked)
    {
        await Assert.That(At(marked)).IsNull();
    }

    [Test]
    public async Task A_named_argument_is_recognised_in_both_spellings()
    {
        await Assert.That(At("SELECT make_interval(days => 1, hours => |")!.ArgumentName).IsEqualTo("hours");
        await Assert.That(At("SELECT make_interval(days := |")!.ArgumentName).IsEqualTo("days");
        await Assert.That(At("SELECT make_interval(1, |")!.ArgumentName).IsNull();
    }

    [Test]
    public async Task Parameters_are_split_and_named()
    {
        var parameters = SqlParameters.Parse("x integer, \"Y z\" numeric(10,2), double precision, VARIADIC rest text[]");

        await Assert.That(parameters.Select(p => p.Name)).IsEquivalentTo(new string?[] { "x", "Y z", null, "rest" });
        await Assert.That(parameters[1].Text).IsEqualTo("\"Y z\" numeric(10,2)");
        await Assert.That(parameters[3].IsVariadic).IsTrue();
        await Assert.That(SqlParameters.Parse("")).IsEmpty();
    }

    private static readonly (string, FunctionInfo)[] Overloads =
    [
        ("pg_catalog", new FunctionInfo("round", "numeric", "numeric", 'f')),
        ("pg_catalog", new FunctionInfo("round", "numeric, integer", "numeric", 'f')),
        ("pg_catalog", new FunctionInfo("concat", "VARIADIC \"any\"", "text", 'f')),
        ("public", new FunctionInfo("make", "days integer, hours integer", "interval", 'f')),
        ("public", new FunctionInfo("round", "x integer", "", 'p')),
    ];

    [Test]
    public async Task Only_overloads_that_take_the_argument_are_shown_with_it_marked()
    {
        var second = SignatureHints.For(At("SELECT round(a, |")!, Overloads.Where(o => o.Item2.Name == "round"));

        await Assert.That(second.Select(h => h.Parameters.Count)).IsEquivalentTo(new[] { 2 });
        await Assert.That(second[0].ActiveParameter).IsEqualTo(1);

        var first = SignatureHints.For(At("SELECT round(|")!, Overloads.Where(o => o.Item2.Name == "round"));
        await Assert.That(first.Count).IsEqualTo(2); // the procedure is not callable here
    }

    [Test]
    public async Task Variadic_named_and_unfitting_calls()
    {
        var variadic = SignatureHints.For(At("SELECT concat(a, b, c, |")!, Overloads.Where(o => o.Item2.Name == "concat"));
        await Assert.That(variadic.Single().ActiveParameter).IsEqualTo(0);

        var named = SignatureHints.For(At("SELECT make(hours => |")!, Overloads.Where(o => o.Item2.Name == "make"));
        await Assert.That(named.Single().ActiveParameter).IsEqualTo(1);

        var tooMany = SignatureHints.For(At("SELECT round(a, b, |")!, Overloads.Where(o => o.Item2.Name == "round"));
        await Assert.That(tooMany.Count).IsEqualTo(2);
        await Assert.That(tooMany.All(h => h.ActiveParameter == -1)).IsTrue();
    }
}
