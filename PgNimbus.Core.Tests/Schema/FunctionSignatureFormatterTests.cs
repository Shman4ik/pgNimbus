using PgNimbus.Core.Schema;

namespace PgNimbus.Core.Tests.Schema;

/// <summary>
/// Exercises <see cref="FunctionSignatureFormatter"/> — the text that fills
/// completion's tooltip panel for a catalog function, standing in for a
/// dedicated parameter-hints popup.
/// </summary>
public class FunctionSignatureFormatterTests
{
    [Test]
    public async Task Function_WithArgsAndReturnType()
    {
        var info = new FunctionInfo("total_revenue", "customer_id integer", "numeric", 'f');

        await Assert.That(FunctionSignatureFormatter.Format(info)).IsEqualTo("(customer_id integer) → numeric");
        await Assert.That(FunctionSignatureFormatter.KindLabel(info)).IsEqualTo("function");
    }

    [Test]
    public async Task Function_NoArguments()
    {
        var info = new FunctionInfo("now_utc", "", "timestamp with time zone", 'f');

        await Assert.That(FunctionSignatureFormatter.Format(info)).IsEqualTo("() → timestamp with time zone");
    }

    [Test]
    public async Task Procedure_HasNoReturnType()
    {
        // pg_get_function_result returns null for a procedure; SchemaService
        // COALESCEs that to "" — Format must not print a bare "→ ".
        var info = new FunctionInfo("archive_orders", "cutoff date", "", 'p');

        await Assert.That(FunctionSignatureFormatter.Format(info)).IsEqualTo("(cutoff date)");
        await Assert.That(FunctionSignatureFormatter.KindLabel(info)).IsEqualTo("procedure");
    }

    [Test]
    public async Task Procedure_NoArgumentsAndNoReturnType()
    {
        var info = new FunctionInfo("vacuum_all", "", "", 'p');

        await Assert.That(FunctionSignatureFormatter.Format(info)).IsEqualTo("()");
    }

    [Test]
    public async Task Aggregate_KindLabel()
    {
        var info = new FunctionInfo("median", "numeric", "numeric", 'a');

        await Assert.That(FunctionSignatureFormatter.Format(info)).IsEqualTo("(numeric) → numeric");
        await Assert.That(FunctionSignatureFormatter.KindLabel(info)).IsEqualTo("aggregate");
    }

    [Test]
    public async Task WindowFunction_KindLabel()
    {
        var info = new FunctionInfo("running_total", "amount numeric", "numeric", 'w');

        await Assert.That(FunctionSignatureFormatter.KindLabel(info)).IsEqualTo("window function");
    }

    [Test]
    public async Task MultipleArguments_KeptAsOneCommaList()
    {
        var info = new FunctionInfo("make_range", "lo integer, hi integer, inclusive boolean", "int4range", 'f');

        await Assert.That(FunctionSignatureFormatter.Format(info)).IsEqualTo("(lo integer, hi integer, inclusive boolean) → int4range");
    }
}
