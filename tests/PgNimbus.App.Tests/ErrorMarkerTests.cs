using Avalonia.Controls;
using Avalonia.VisualTree;
using AvaloniaEdit;
using PgNimbus.App.Views;
using PgNimbus.Core.Query;
using PgNimbus.Screenshot;

namespace PgNimbus.App.Tests;

/// <summary>
/// After a failed run the editor underlines where the server says the error is
/// (<c>QueryError.Position</c>), scrolls it into view, and adds the line and column
/// to the error text, without moving the caret or taking focus. The run is seeded
/// (<c>QueryViewModel.SeedRunResults</c>): these tests have no server, and what is
/// under test is the mapping from the engine's result to the editor.
/// </summary>
public class ErrorMarkerTests
{
    private static QueryError ErrorAt(int? position) => new() { Message = "boom", Position = position, Elapsed = TimeSpan.Zero };

    private static StatementResult Ok() => new CommandResult { CommandTag = "SELECT 1", RowsAffected = 1, Elapsed = TimeSpan.Zero };

    [Test]
    public async Task A_failed_script_statement_is_marked_scrolled_to_and_the_caret_stays()
    {
        await Ui.Run(async () =>
        {
            var (window, vm) = Scenarios.Shell();
            Ui.Show(window);
            var (editor, panel, filter) = Parts(window);
            var tab = vm.ActiveTab;

            // Far enough down that marking it has to scroll.
            var filler = string.Concat(Enumerable.Range(1, 120).Select(i => $"SELECT {i};\n"));
            var script = filler + "SELECT id,\n       nmae\n  FROM users;\nSELECT 2;";
            tab.Sql = script;
            Ui.Settle();
            editor.CaretOffset = 3;
            filter.Focus();
            Ui.Settle();

            // Statement 121 (index 120) is "SELECT id,\n       nmae\n  FROM users";
            // the server puts "nmae" at character 19 of it.
            var results = Enumerable.Range(0, 120).Select(_ => Ok()).Append(ErrorAt(19)).Append(Ok()).ToList();
            tab.SeedRunResults(script, documentOffset: 0, results);
            Ui.Settle();

            var expected = script.IndexOf("nmae", StringComparison.Ordinal);
            await Assert.That(tab.ErrorMarker).IsEqualTo(new SqlErrorSpan(expected, 4));
            await Assert.That(panel.ShownErrorMarker).IsEqualTo(tab.ErrorMarker);
            await Assert.That(tab.Status).EndsWith("(line 122, column 8)");
            await Assert.That(editor.CaretOffset).IsEqualTo(3);
            await Assert.That(filter.IsFocused).IsTrue();
            await Assert.That(editor.TextArea.TextView.ScrollOffset.Y).IsGreaterThan(0);

            // Another section shows no marker; the failed one brings it back.
            tab.SelectedSection = tab.ResultSections[0];
            await Assert.That(panel.ShownErrorMarker).IsNull();
            tab.SelectedSection = tab.ResultSections[120];
            await Assert.That(panel.ShownErrorMarker).IsEqualTo(new SqlErrorSpan(expected, 4));

            // An edit anywhere clears it.
            editor.Document.Insert(0, "-- x\n");
            Ui.Settle();
            await Assert.That(tab.ErrorMarker).IsNull();
            await Assert.That(panel.ShownErrorMarker).IsNull();

            // Selecting the failed section again, the text no longer matches the run:
            // nothing is marked rather than the wrong place.
            tab.SelectedSection = tab.ResultSections[0];
            tab.SelectedSection = tab.ResultSections[120];
            await Assert.That(tab.ErrorMarker).IsNull();

            window.Close();
        });
    }

    [Test]
    public async Task A_selection_run_is_marked_at_the_selection_through_an_emoji()
    {
        await Ui.Run(async () =>
        {
            var (window, vm) = Scenarios.Shell();
            Ui.Show(window);
            var (editor, panel, _) = Parts(window);
            var tab = vm.ActiveTab;

            const string document = "SELECT 1;\n  SELECT '\U0001F600' AS e, nope FROM t;\nSELECT 3;";
            tab.Sql = document;
            Ui.Settle();
            var start = document.IndexOf("  SELECT '", StringComparison.Ordinal);
            var end = document.IndexOf(";\nSELECT 3", StringComparison.Ordinal) + 1;
            editor.Select(start, end - start);
            Ui.Settle();
            var caret = editor.CaretOffset;

            await Assert.That(tab.SelectedSqlOffset).IsEqualTo(start);

            // Sent trimmed, "nope" is character 18: the emoji counts once.
            tab.SeedRunResults(tab.SelectedSql!, tab.SelectedSqlOffset, [ErrorAt(18)]);
            Ui.Settle();

            var expected = document.IndexOf("nope", StringComparison.Ordinal);
            await Assert.That(tab.ErrorMarker).IsEqualTo(new SqlErrorSpan(expected, 4));
            await Assert.That(panel.ShownErrorMarker).IsEqualTo(tab.ErrorMarker);
            await Assert.That(tab.Status).IsEqualTo("Error: boom (line 2, column 20)");
            await Assert.That(editor.CaretOffset).IsEqualTo(caret);

            window.Close();
        });
    }

    [Test]
    public async Task No_position_or_an_unknown_offset_marks_nothing()
    {
        await Ui.Run(async () =>
        {
            var (window, vm) = Scenarios.Shell();
            Ui.Show(window);
            var (_, panel, _) = Parts(window);
            var tab = vm.ActiveTab;
            tab.Sql = "SELECT f();";
            Ui.Settle();

            // An error inside a function body has an internal position only, which
            // the engine does not report as Position.
            tab.SeedRunResults(tab.Sql, 0, [ErrorAt(null)]);
            await Assert.That(tab.ErrorMarker).IsNull();
            await Assert.That(tab.Status).IsEqualTo("Error: boom");

            // Text that isn't the editor's (no offset) is not marked.
            tab.SeedRunResults("SELECT f()", null, [ErrorAt(8)]);
            await Assert.That(tab.ErrorMarker).IsNull();

            // A marker, then the tab's text replaced: gone.
            tab.SeedRunResults(tab.Sql, 0, [ErrorAt(8)]);
            await Assert.That(tab.ErrorMarker).IsEqualTo(new SqlErrorSpan(7, 1));
            tab.Sql = "SELECT 2;";
            Ui.Settle();
            await Assert.That(tab.ErrorMarker).IsNull();
            await Assert.That(panel.ShownErrorMarker).IsNull();

            window.Close();
        });
    }

    [Test]
    public async Task Switching_tabs_shows_each_tabs_own_marker()
    {
        await Ui.Run(async () =>
        {
            var (window, vm) = Scenarios.Shell();
            Ui.Show(window);
            var (_, panel, _) = Parts(window);
            var first = vm.ActiveTab;
            first.Sql = "SELECT nope;";
            Ui.Settle();
            first.SeedRunResults(first.Sql, 0, [ErrorAt(8)]);

            vm.AddTabCommand.Execute(null);
            Ui.Settle();
            await Assert.That(panel.ShownErrorMarker).IsNull();

            vm.ActiveTab = first;
            Ui.Settle();
            await Assert.That(panel.ShownErrorMarker).IsEqualTo(new SqlErrorSpan(7, 4));

            window.Close();
        });
    }

    private static (TextEditor Editor, QueryEditorPanel Panel, TextBox Filter) Parts(Window window)
    {
        var editor = window.GetVisualDescendants().OfType<TextEditor>().First(e => e.Name == "SqlEditor");
        var panel = window.GetVisualDescendants().OfType<QueryEditorPanel>().First();
        var filter = window.GetVisualDescendants().OfType<TextBox>()
            .First(t => t.PlaceholderText?.StartsWith("Filter schemas", StringComparison.Ordinal) == true);
        return (editor, panel, filter);
    }
}
