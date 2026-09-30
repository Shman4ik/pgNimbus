using PgNimbus.Core.Text;

namespace PgNimbus.Core.Tests.Text;

/// <summary>Where the caret types a value, and what decides it (sql-completion-audit-2.md E07). <c>|</c> marks the caret.</summary>
public class SqlValueSlotTests
{
    private static SqlValueSlot? At(string marked)
    {
        var caret = marked.IndexOf('|');
        return SqlValueSlot.At(marked.Remove(caret, 1), caret);
    }

    [Test]
    [Arguments("SELECT * FROM t WHERE status = |", "status", false)]
    [Arguments("SELECT * FROM t WHERE i.status = |", "i.status", false)]
    [Arguments("SELECT * FROM t WHERE i.status <> 'op|", "i.status", true)]
    [Arguments("SELECT * FROM t WHERE i.status = '|'", "i.status", true)]
    [Arguments("SELECT * FROM t WHERE saas.issues.status != |", "saas.issues.status", false)]
    [Arguments("SELECT * FROM t WHERE status IN (|", "status", false)]
    [Arguments("SELECT * FROM t WHERE status IN ('open', '|", "status", true)]
    [Arguments("SELECT * FROM t WHERE status NOT IN ('open', |", "status", false)]
    [Arguments("UPDATE t SET status = 'do|", "status", true)]
    [Arguments("SELECT * FROM t WHERE \"Status\" = |", "Status", false)]
    public async Task The_right_side_of_a_comparison_is_the_columns_value(string marked, string column, bool inString)
    {
        var slot = At(marked);

        await Assert.That(slot).IsNotNull();
        await Assert.That(string.Join('.', slot!.ComparedColumn!)).IsEqualTo(column);
        await Assert.That(slot.InString).IsEqualTo(inString);
    }

    [Test]
    [Arguments("SELECT nextval('|", "nextval", 0, true)]
    [Arguments("SELECT date_trunc('mo|', now())", "date_trunc", 0, true)]
    [Arguments("SELECT extract(|", "extract", 0, false)]
    [Arguments("SELECT round(a, |", "round", 1, false)]
    [Arguments("SELECT coalesce(f(a, b), |", "coalesce", 1, false)]
    public async Task An_argument_of_a_call_names_the_call(string marked, string function, int index, bool inString)
    {
        var slot = At(marked);

        await Assert.That(slot).IsNotNull();
        await Assert.That(slot!.Function).IsEqualTo(function);
        await Assert.That(slot.ArgumentIndex).IsEqualTo(index);
        await Assert.That(slot.InString).IsEqualTo(inString);
    }

    [Test]
    [Arguments("SELECT |")]
    [Arguments("SELECT * FROM t WHERE a > |")] // not a comparison whose type decides
    [Arguments("SELECT * FROM t WHERE (a + 1) = |")]
    [Arguments("SELECT 'text |")]
    [Arguments("SELECT * FROM t -- status = |")]
    [Arguments("SELECT * FROM t WHERE a IN (SELECT |")]
    [Arguments("SELECT $$ status = |")]
    public async Task Anything_else_is_no_value_slot(string marked)
    {
        var slot = At(marked);

        await Assert.That(slot is null || (slot.ComparedColumn is null && slot.Function is null)).IsTrue();
    }
}
