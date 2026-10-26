using PgNimbus.Core.Query;
using PgNimbus.Core.QueryLab;
using static PgNimbus.Core.Tests.QueryLab.Plans;

namespace PgNimbus.Core.Tests.QueryLab;

/// <summary>
/// One label per node whichever format the plan came in: FORMAT TEXT folds fields into
/// the node name that FORMAT JSON keeps apart, and a JSON plan compared with its own
/// text twin must come out as no change at all.
/// </summary>
public class PlanNodeLabelTests
{
    [Test]
    [Arguments("Hash Left Join", null, "Hash Join", "Left")]
    [Arguments("Hash Join", null, "Hash Join", "Inner")]
    [Arguments("Hash Join", "Inner", "Hash Join", "Inner")]
    [Arguments("Merge Full Join", null, "Merge Join", "Full")]
    [Arguments("Hash Right Semi Join", null, "Hash Join", "Right Semi")]
    [Arguments("Nested Loop Anti Join", null, "Nested Loop", "Anti")]
    [Arguments("Nested Loop", null, "Nested Loop", "Inner")]
    public async Task Text_join_names_unfold_into_type_and_join_type(string type, string? joinType, string expectedType, string expectedJoin)
    {
        var label = PlanNodeLabel.Of(Node(type, joinType: joinType));

        await Assert.That(label.Type).IsEqualTo(expectedType);
        await Assert.That(label.JoinType).IsEqualTo(expectedJoin);
        await Assert.That(label.Family).IsEqualTo(PlanNodeFamily.Join);
    }

    [Test]
    public async Task Text_aggregate_names_unfold_into_strategy_and_partial_mode()
    {
        var text = PlanNodeLabel.Of(Node("Partial HashAggregate"));
        var json = PlanNodeLabel.Of(Node("Aggregate", strategy: "Hashed") with { PartialMode = "Partial" });

        await Assert.That(text).IsEqualTo(json);
        await Assert.That(PlanNodeLabel.Of(Node("GroupAggregate")))
            .IsEqualTo(PlanNodeLabel.Of(Node("Aggregate", strategy: "Sorted") with { PartialMode = "Simple" }));
        await Assert.That(PlanNodeLabel.Of(Node("Aggregate")).Strategy).IsEqualTo("Plain");
    }

    [Test]
    public async Task A_text_write_is_a_modify_table()
    {
        var text = PlanNodeLabel.Of(Node("Insert", relation: "orders"), PlanFormat.Text);
        var json = PlanNodeLabel.Of(Node("ModifyTable", relation: "orders", alias: "orders") with { Operation = "Insert" });

        await Assert.That(text).IsEqualTo(json);
        await Assert.That(text.Family).IsEqualTo(PlanNodeFamily.Modify);
    }

    [Test]
    public async Task Text_names_lose_their_quotes_and_schema_and_json_names_keep_theirs()
    {
        await Assert.That(PlanNodeLabel.Of(Node("Seq Scan", relation: "\"Order Items\""), PlanFormat.Text).Relation)
            .IsEqualTo("Order Items");
        await Assert.That(PlanNodeLabel.Of(Node("Seq Scan", relation: "public.orders"), PlanFormat.Text).Relation)
            .IsEqualTo("orders");
        await Assert.That(PlanNodeLabel.Of(Node("Seq Scan", relation: "a.b"), PlanFormat.Json).Relation).IsEqualTo("a.b");
        // The text format leaves out an alias that is the table's own name.
        await Assert.That(PlanNodeLabel.Of(Node("Seq Scan", relation: "orders"), PlanFormat.Text))
            .IsEqualTo(PlanNodeLabel.Of(Node("Seq Scan", relation: "orders", alias: "orders"), PlanFormat.Json));
    }

    [Test]
    public async Task A_subquery_scan_is_named_by_its_alias_in_either_format()
    {
        await Assert.That(PlanNodeLabel.Of(Node("Subquery Scan", relation: "sub"), PlanFormat.Text))
            .IsEqualTo(PlanNodeLabel.Of(Node("Subquery Scan", alias: "sub"), PlanFormat.Json));
    }

    [Test]
    public async Task A_subplan_is_the_same_whether_or_not_its_name_says_what_it_returns()
    {
        await Assert.That(PlanNodeLabel.Of(Node("Result") with { SubplanName = "InitPlan 1 (returns $0)" }).Subplan)
            .IsEqualTo("InitPlan 1");
    }

    [Test]
    [Arguments("before")]
    [Arguments("after")]
    public async Task A_real_json_plan_and_its_text_twin_compare_as_no_change(string which)
    {
        var (json, text) = which == "before"
            ? (RealPlans.BeforeJson, RealPlans.BeforeText)
            : (RealPlans.AfterJson, RealPlans.AfterText);
        var diff = PlanDiff.Build(ExplainService.Import(json).Result, ExplainService.Import(text).Result);

        await Assert.That(All(diff.Roots).Select(n => n.Change).Distinct()).IsEquivalentTo([PlanNodeChange.Same]);
        await Assert.That(diff.ShapeChanged).IsFalse();
    }

    [Test]
    [Arguments("before")]
    [Arguments("after")]
    public async Task A_json_plan_compares_as_no_change_with_its_formatted_text(string which)
    {
        var json = which == "before" ? RealPlans.BeforeJson : RealPlans.AfterJson;
        // The plan pane's own text layout, read back by the text parser.
        var result = ExplainService.Import(json).Result;
        var diff = PlanDiff.Build(result, ExplainService.Import(ExplainTextFormatter.Format(result)).Result);

        await Assert.That(diff.Same).IsEqualTo(diff.Roots.Sum(r => All([r]).Count()));
    }
}
