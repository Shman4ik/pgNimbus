using PgNimbus.App.ViewModels;
using PgNimbus.Core.Schema;
using PgNimbus.Screenshot;

namespace PgNimbus.App.Tests;

/// <summary>
/// The sidebar's filter box. The case these cover is the one it used to get
/// wrong: it filtered only the nodes the tree had already loaded, so a table in
/// a schema nobody had expanded looked like it did not exist — while the command
/// palette, which searches the whole catalog, found it straight away.
///
/// Plain view-model tests, no window: the filter is view-model logic, and the
/// tree's nodes are lazily loaded, which is exactly the state a rendered fixture
/// tree does not have.
/// </summary>
public class SchemaTreeFilterTests
{
    private static readonly SchemaService Service = new(Fixtures.DataSource);

    private static readonly IReadOnlyList<RelationInfo> Catalog =
    [
        new("public", "orders", RelationKind.Table),
        new("public", "customers", RelationKind.Table),
        new("billing", "invoices", RelationKind.Table),
        new("billing", "invoice_lines", RelationKind.Table),
    ];

    /// <summary>A tree of never-expanded schemas, the state right after connecting.</summary>
    private static SchemaTreeViewModel CollapsedTree(
        Func<Task<IReadOnlyList<RelationInfo>>>? relations = null,
        params string[] schemas)
    {
        var tree = new SchemaTreeViewModel(Service)
        {
            AllRelationsRequested = relations ?? (() => Task.FromResult(Catalog)),
        };

        foreach (var name in schemas)
        {
            tree.Schemas.Add(new SchemaNode(Service, name, () => false, () => false));
        }

        return tree;
    }

    private static SchemaNode SchemaNamed(SchemaTreeViewModel tree, string name) =>
        tree.Schemas.OfType<SchemaNode>().First(s => s.Name == name);

    // The filter's catalog pass is async, so an assertion made straight after
    // setting FilterText can read the synchronous first pass instead.
    private static async Task<bool> Until(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        return condition();
    }

    [Test]
    public async Task Filter_finds_a_table_in_a_schema_that_was_never_expanded()
    {
        var tree = CollapsedTree(schemas: ["public", "billing"]);

        tree.FilterText = "invoices";

        var billing = SchemaNamed(tree, "billing");
        await Assert.That(await Until(() => billing.IsFilteredIn)).IsTrue();

        // ...and it opens the schema so the match is actually on screen, rather
        // than leaving the user a collapsed row to guess at.
        await Assert.That(billing.IsExpanded).IsTrue();

        // A schema with no match either way is still filtered out.
        await Assert.That(SchemaNamed(tree, "public").IsFilteredIn).IsFalse();
    }

    [Test]
    public async Task Children_loaded_after_the_filter_expanded_a_schema_are_vetted_too()
    {
        var tree = CollapsedTree(schemas: ["billing"]);
        var billing = SchemaNamed(tree, "billing");

        tree.FilterText = "invoice_lines";
        await Assert.That(await Until(() => billing.IsFilteredIn)).IsTrue();

        // Stand in for the lazy load the expansion kicked off: children arrive
        // after the filter pass, all visible by default.
        billing.SeedChildren(
        [
            new TableNode(Service, "billing", "invoices", RelationKind.Table, null, () => false, () => false),
            new TableNode(Service, "billing", "invoice_lines", RelationKind.Table, null, () => false, () => false),
        ]);

        await Assert.That(billing.Children.Single(c => c.Name == "invoice_lines").IsFilteredIn).IsTrue();
        await Assert.That(billing.Children.Single(c => c.Name == "invoices").IsFilteredIn).IsFalse();
    }

    [Test]
    public async Task A_loaded_schema_is_judged_by_its_own_children()
    {
        var tree = CollapsedTree(schemas: ["public"]);
        var schema = SchemaNamed(tree, "public");

        // The catalog snapshot still lists public.customers; the loaded tree says
        // it is gone. The tree wins, so the schema does not linger on screen with
        // nothing in it.
        schema.SeedChildren([new TableNode(Service, "public", "orders", RelationKind.Table, null, () => false, () => false)]);

        tree.FilterText = "customers";
        await Assert.That(await Until(() => !schema.IsFilteredIn)).IsTrue();
    }

    [Test]
    public async Task Clearing_the_filter_reveals_everything_again()
    {
        var tree = CollapsedTree(schemas: ["public", "billing"]);

        tree.FilterText = "invoices";
        await Assert.That(await Until(() => !SchemaNamed(tree, "public").IsFilteredIn)).IsTrue();

        tree.ClearFilterCommand.Execute(null);

        await Assert.That(tree.Schemas.All(s => s.IsFilteredIn)).IsTrue();
    }

    /// <summary>
    /// The reveal has to be undoable. A one-character query matches a table in
    /// nearly every schema, so the filter used to leave the whole tree open —
    /// clearing the box put the rows back but never the expansion, which read as
    /// the sidebar having "remembered" a state nobody asked for.
    /// </summary>
    [Test]
    public async Task Clearing_the_filter_closes_the_schemas_it_opened()
    {
        var tree = CollapsedTree(schemas: ["public", "billing"]);
        var billing = SchemaNamed(tree, "billing");

        tree.FilterText = "invoices";
        await Assert.That(await Until(() => billing.IsExpanded)).IsTrue();

        tree.ClearFilterCommand.Execute(null);

        await Assert.That(billing.IsExpanded).IsFalse();
    }

    /// <summary>A schema the user opened is theirs, and the filter never closes it.</summary>
    [Test]
    public async Task Clearing_the_filter_leaves_a_hand_opened_schema_open()
    {
        var tree = CollapsedTree(schemas: ["public", "billing"]);
        var billing = SchemaNamed(tree, "billing");
        billing.IsExpanded = true;

        tree.FilterText = "invoices";
        await Assert.That(await Until(() => billing.IsFilteredIn)).IsTrue();

        tree.ClearFilterCommand.Execute(null);

        await Assert.That(billing.IsExpanded).IsTrue();
    }

    /// <summary>Typing on past the match closes the reveal there and then, not at clear time.</summary>
    [Test]
    public async Task A_schema_that_stops_matching_closes_again()
    {
        var tree = CollapsedTree(schemas: ["billing"]);
        var billing = SchemaNamed(tree, "billing");

        tree.FilterText = "invoices";
        await Assert.That(await Until(() => billing.IsExpanded)).IsTrue();

        tree.FilterText = "invoices_that_do_not_exist";

        await Assert.That(await Until(() => !billing.IsExpanded)).IsTrue();
    }

    /// <summary>
    /// A refresh rebuilds every node, and used to collapse everything the user had
    /// open with it. The expansions are remembered by name and re-applied as the
    /// new nodes arrive.
    /// </summary>
    [Test]
    public async Task A_hand_opened_schema_is_reopened_when_the_tree_is_rebuilt()
    {
        var tree = CollapsedTree(schemas: ["public", "billing"]);
        SchemaNamed(tree, "billing").IsExpanded = true;

        // Stand in for RefreshAsync's rebuild: same names, brand-new nodes.
        tree.Schemas.Clear();
        tree.Schemas.Add(new SchemaNode(Service, "public", () => false, () => false));
        tree.Schemas.Add(new SchemaNode(Service, "billing", () => false, () => false));

        await Assert.That(SchemaNamed(tree, "billing").IsExpanded).IsTrue();
        await Assert.That(SchemaNamed(tree, "public").IsExpanded).IsFalse();
    }

    /// <summary>
    /// Roles and Extensions aren't schemas, and were exempt from the filter
    /// entirely — so a query that found nothing left "Roles" alone on screen,
    /// reading as a hit.
    /// </summary>
    [Test]
    public async Task Root_groups_are_filtered_by_their_own_name()
    {
        var tree = CollapsedTree(schemas: ["billing"]);
        var roles = tree.Schemas.OfType<RolesGroupNode>().FirstOrDefault();
        if (roles is null)
        {
            roles = new RolesGroupNode(Service);
            tree.Schemas.Add(roles);
        }

        tree.FilterText = "invoices";
        await Assert.That(await Until(() => !roles.IsFilteredIn)).IsTrue();

        tree.FilterText = "rol";
        await Assert.That(await Until(() => roles.IsFilteredIn)).IsTrue();
    }

    /// <summary>An empty tree with no explanation reads as a broken sidebar.</summary>
    [Test]
    public async Task Nothing_matching_is_an_explicit_state()
    {
        var tree = CollapsedTree(schemas: ["public", "billing"]);

        tree.FilterText = "nothing_by_that_name";
        await Assert.That(await Until(() => tree.ShowNoMatches)).IsTrue();

        tree.FilterText = "invoices";
        await Assert.That(await Until(() => !tree.ShowNoMatches)).IsTrue();
    }

    /// <summary>Collapse all closes the tree however deep it was opened; expand all opens the schemas.</summary>
    [Test]
    public async Task Collapse_all_and_expand_all_walk_the_tree()
    {
        var tree = CollapsedTree(schemas: ["public", "billing"]);
        var billing = SchemaNamed(tree, "billing");
        var table = new TableNode(Service, "billing", "invoices", RelationKind.Table, null, () => false, () => false)
        {
            IsExpanded = true,
        };
        billing.SeedChildren([table]);
        billing.IsExpanded = true;

        tree.CollapseAllCommand.Execute(null);

        await Assert.That(billing.IsExpanded).IsFalse();
        await Assert.That(table.IsExpanded).IsFalse();

        tree.ExpandAllCommand.Execute(null);

        await Assert.That(tree.Schemas.OfType<SchemaNode>().All(s => s.IsExpanded)).IsTrue();
    }

    /// <summary>
    /// A catalog fetch that fails (no connection, a permissions error) must not
    /// take the filter down with it — it falls back to what the tree has loaded.
    /// </summary>
    [Test]
    public async Task A_failed_catalog_fetch_leaves_the_loaded_filter_working()
    {
        var tree = CollapsedTree(() => Task.FromException<IReadOnlyList<RelationInfo>>(new InvalidOperationException("no connection")), "public");
        var schema = SchemaNamed(tree, "public");
        schema.SeedChildren([new TableNode(Service, "public", "orders", RelationKind.Table, null, () => false, () => false)]);

        tree.FilterText = "orders";

        await Assert.That(schema.IsFilteredIn).IsTrue();
        await Assert.That(schema.Children.Single().IsFilteredIn).IsTrue();
    }
}
