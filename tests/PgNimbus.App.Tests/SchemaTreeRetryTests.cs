using PgNimbus.App.ViewModels;

namespace PgNimbus.App.Tests;

/// <summary>
/// A node whose children failed to load tries again the next time it is
/// expanded. Reported on 0.13.1: a table expanded while the connection was down
/// kept its "Failed to connect to …" row after the server was back and the same
/// table browsed fine, because the node counted itself loaded after the failure
/// and only a refresh of the whole tree read it again.
/// </summary>
public class SchemaTreeRetryTests
{
    [Test]
    public async Task A_failed_load_is_tried_again_on_the_next_expand()
    {
        var node = new FlakyNode { FailuresLeft = 1 };

        node.IsExpanded = true;
        await Assert.That(node.Children.Single()).IsTypeOf<ErrorNode>();
        await Assert.That(node.IsLoaded).IsFalse();

        node.IsExpanded = false;
        node.IsExpanded = true;

        await Assert.That(node.Fetches).IsEqualTo(2);
        await Assert.That(node.Children.Single().Name).IsEqualTo("id");
        await Assert.That(node.IsLoaded).IsTrue();
    }

    [Test]
    public async Task A_loaded_node_is_not_read_again_on_expand()
    {
        var node = new FlakyNode();

        node.IsExpanded = true;
        node.IsExpanded = false;
        node.IsExpanded = true;

        await Assert.That(node.Fetches).IsEqualTo(1);
    }

    /// <summary>Fails its first <see cref="FailuresLeft"/> fetches; completes synchronously, so an expand has loaded by the time it returns.</summary>
    private sealed class FlakyNode : SchemaTreeNode
    {
        public int FailuresLeft { get; set; }

        public int Fetches { get; private set; }

        protected override Task<IReadOnlyList<SchemaTreeNode>> FetchChildrenAsync()
        {
            Fetches++;
            if (FailuresLeft-- > 0)
            {
                return Task.FromException<IReadOnlyList<SchemaTreeNode>>(
                    new InvalidOperationException("Failed to connect to 178.128.196.185:25060"));
            }

            return Task.FromResult<IReadOnlyList<SchemaTreeNode>>([new ColumnStub { Name = "id" }]);
        }
    }

    private sealed class ColumnStub : SchemaTreeNode;
}
