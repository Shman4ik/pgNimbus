using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using AvaloniaEdit;
using AvaloniaEdit.Folding;
using AvaloniaEdit.Rendering;
using PgNimbus.App.ViewModels;
using PgNimbus.Core.Commands;
using PgNimbus.Core.Json;
using PgNimbus.Screenshot;

namespace PgNimbus.App.Tests;

/// <summary>
/// The cell inspector as someone reading jsonb at work uses it. The first test
/// is the report that started this: a nested list of e-mail addresses drawn
/// partly blue, because AvaloniaEdit renders every address and URL as a link
/// in pure Blue over the highlighter's colours.
/// </summary>
public class JsonInspectorTests
{
    private const string Contacts =
        """{"owner": "Ann <ann@example.com>", "cc": ["bob@example.com", "carol@example.org"], "site": "https://example.com/x", "n": 5, "ok": true}""";

    private static readonly Func<int, string, Task<string?>> NoCommit = (_, _) => Task.FromResult<string?>(null);

    private static TextEditor Editor(Window window, string name) =>
        window.GetVisualDescendants().OfType<TextEditor>().First(e => e.Name == name);

    // Every run of text the editor draws, with the text it covers.
    private static List<(VisualLineElement Element, string Text)> Runs(TextEditor editor)
    {
        var view = editor.TextArea.TextView;
        view.EnsureVisualLines();
        var runs = new List<(VisualLineElement, string)>();
        foreach (var line in view.VisualLines)
        {
            foreach (var element in line.Elements)
            {
                var offset = line.FirstDocumentLine.Offset + element.RelativeTextOffset;
                runs.Add((element, editor.Document.GetText(offset, element.DocumentLength)));
            }
        }

        return runs;
    }

    private static Color Foreground(List<(VisualLineElement Element, string Text)> runs, string text) =>
        ((ISolidColorBrush)runs.First(r => r.Text == text).Element.TextRunProperties.ForegroundBrush!).Color;

    private static Color Resource(Window window, string key) =>
        window.TryFindResource(key, window.ActualThemeVariant, out var value) && value is ISolidColorBrush brush
            ? brush.Color
            : throw new InvalidOperationException($"no {key}");

    [Test]
    public async Task Addresses_and_urls_in_a_value_are_text_not_links()
    {
        await Ui.Run(async () =>
        {
            var (window, vm) = Scenarios.Shell();
            Ui.Show(window);
            vm.CellInspector.Open("contacts", Contacts, 0, canEdit: true, NoCommit, validatesAsJson: true, dataTypeName: "jsonb");
            Ui.Settle();

            var viewer = Runs(Editor(window, "Viewer"));
            await Assert.That(viewer.Where(r => r.Element is VisualLineLinkText).Select(r => r.Text)).IsEmpty();
            // And the address is drawn as the string it is part of.
            await Assert.That(viewer.Any(r => r.Text.Contains("bob@example.com"))).IsTrue();

            vm.CellInspector.EditCommand.Execute(null);
            Ui.Settle();
            var editor = Runs(Editor(window, "JsonInspectorEditor"));
            await Assert.That(editor.Where(r => r.Element is VisualLineLinkText).Select(r => r.Text)).IsEmpty();

            window.Close();
        });
    }

    [Test]
    public async Task An_address_in_a_sql_string_is_not_a_link_either()
    {
        await Ui.Run(async () =>
        {
            var (window, vm) = Scenarios.Shell();
            Ui.Show(window);
            vm.ActiveTab.Sql = "SELECT * FROM customers WHERE email = 'ann@example.com'; -- https://example.com";
            Ui.Settle();

            var runs = Runs(Editor(window, "SqlEditor"));

            await Assert.That(runs.Where(r => r.Element is VisualLineLinkText).Select(r => r.Text)).IsEmpty();
            window.Close();
        });
    }

    /// <summary>
    /// The key rule in Json.xshd was a Rule, which AvaloniaEdit only tries
    /// between Spans, and every key starts with the quote that begins the string
    /// Span: keys were drawn in the string colour, one undifferentiated colour.
    /// </summary>
    [Test]
    [Arguments("Light")]
    [Arguments("Dark")]
    public async Task Keys_values_and_words_each_have_their_colour(string theme)
    {
        await Ui.Run(async () =>
        {
            var (window, vm) = Scenarios.Shell();
            Ui.Show(window, theme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light);
            vm.CellInspector.Open("contacts", Contacts);
            Ui.Settle();

            var runs = Runs(Editor(window, "Viewer"));

            await Assert.That(Foreground(runs, "\"owner\"")).IsEqualTo(Resource(window, "JsonKeyBrush"));
            await Assert.That(Foreground(runs, "\"Ann <ann@example.com>\"")).IsEqualTo(Resource(window, "JsonStringBrush"));
            await Assert.That(Foreground(runs, "5")).IsEqualTo(Resource(window, "JsonNumberBrush"));
            await Assert.That(Foreground(runs, "true")).IsEqualTo(Resource(window, "JsonLiteralBrush"));
            await Assert.That(Resource(window, "JsonKeyBrush")).IsNotEqualTo(Resource(window, "JsonStringBrush"));
            window.Close();
        });
    }

    [Test]
    public async Task A_value_reads_with_its_own_characters()
    {
        await Ui.Run(async () =>
        {
            var (window, vm) = Scenarios.Shell();
            Ui.Show(window);
            // Npgsql hands jsonb over as text; a client that stored it escaped
            // still gets it back readable.
            vm.CellInspector.Open("contacts", """{"to": ["Ann <ann@example.com>", "it's 😀"]}""");
            Ui.Settle();

            await Assert.That(vm.CellInspector.DisplayText).Contains("\"Ann <ann@example.com>\"");
            await Assert.That(vm.CellInspector.DisplayText).Contains("\"it's 😀\"");
            await Assert.That(Editor(window, "Viewer").Text).IsEqualTo(vm.CellInspector.DisplayText);
            window.Close();
        });
    }

    [Test]
    public async Task A_json_value_folds_by_object_and_array()
    {
        await Ui.Run(async () =>
        {
            var (window, vm) = Scenarios.Shell();
            Ui.Show(window);
            vm.CellInspector.Open("contacts", Contacts);
            Ui.Settle();

            var viewer = Editor(window, "Viewer");
            var folds = viewer.TextArea.LeftMargins.OfType<FoldingMargin>().Single().FoldingManager.AllFoldings.ToList();

            await Assert.That(folds.Select(f => f.Title)).IsEquivalentTo(new[] { "{ 5 fields }", "[ 2 items ]" });

            // Plain text has no folding margin at all.
            vm.CellInspector.Open("note", "just text");
            Ui.Settle();
            await Assert.That(viewer.TextArea.LeftMargins.OfType<FoldingMargin>()).IsEmpty();
            window.Close();
        });
    }

    /// <summary>
    /// Ctrl/Cmd+Enter in the inspector's editor used to reach the window and run
    /// the query behind the overlay, which reloaded the grid under the cell being
    /// edited. It saves the value.
    /// </summary>
    [Test]
    public async Task The_run_chord_in_the_editor_saves_the_value_instead_of_running_the_query()
    {
        await Ui.Run(async () =>
        {
            var (window, vm) = Scenarios.Shell();
            Ui.Show(window);
            string? saved = null;
            vm.CellInspector.Open("contacts", """{"a": 1}""", 0, canEdit: true,
                (_, text) => { saved = text; return Task.FromResult<string?>(null); },
                validatesAsJson: true, startEditing: true, dataTypeName: "jsonb");
            Ui.Settle();
            var editor = Editor(window, "JsonInspectorEditor");
            editor.TextArea.Focus();
            editor.CaretOffset = editor.Text.Length;
            Ui.Type(window, " ");

            Ui.Press(window, CommandId.Run);
            Ui.Settle();

            await Assert.That(saved).IsNotNull();
            await Assert.That(vm.CellInspector.IsEditing).IsFalse();
            await Assert.That(vm.ActiveTab.IsRunning).IsFalse();
            window.Close();
        });
    }

    [Test]
    public async Task The_run_chord_does_not_save_what_is_not_valid_json()
    {
        await Ui.Run(async () =>
        {
            var (window, vm) = Scenarios.Shell();
            Ui.Show(window);
            var saves = 0;
            vm.CellInspector.Open("contacts", """{"a": 1}""", 0, canEdit: true,
                (_, _) => { saves++; return Task.FromResult<string?>(null); },
                validatesAsJson: true, startEditing: true, dataTypeName: "jsonb");
            Ui.Settle();
            var editor = Editor(window, "JsonInspectorEditor");
            editor.TextArea.Focus();
            editor.CaretOffset = editor.Text.Length;
            Ui.Type(window, ",");

            Ui.Press(window, CommandId.Run);
            Ui.Settle();

            await Assert.That(saves).IsEqualTo(0);
            await Assert.That(vm.CellInspector.IsEditing).IsTrue();
            await Assert.That(vm.ActiveTab.IsRunning).IsFalse();
            window.Close();
        });
    }

    /// <summary>The inspector is modal: no window chord acts on the tab behind it.</summary>
    [Test]
    public async Task Window_shortcuts_do_not_reach_through_the_inspector()
    {
        await Ui.Run(async () =>
        {
            var (window, vm) = Scenarios.Shell();
            Ui.Show(window);
            vm.CellInspector.Open("contacts", Contacts);
            Ui.Settle();
            var tabs = vm.Tabs.Count;

            Ui.Press(window, CommandId.NewTab);
            Ui.Press(window, CommandId.CloseTab);
            Ui.Press(window, CommandId.Run);
            Ui.Settle();

            await Assert.That(vm.Tabs.Count).IsEqualTo(tabs);
            await Assert.That(vm.ActiveTab.IsRunning).IsFalse();

            // Closed, the same chord works again.
            vm.CellInspector.CloseCommand.Execute(null);
            Ui.Press(window, CommandId.NewTab);
            Ui.Settle();
            await Assert.That(vm.Tabs.Count).IsEqualTo(tabs + 1);
            window.Close();
        });
    }

    [Test]
    public async Task The_find_chord_searches_the_value_not_the_editor_behind_it()
    {
        await Ui.Run(async () =>
        {
            var (window, vm) = Scenarios.Shell();
            Ui.Show(window);
            vm.CellInspector.Open("contacts", Contacts);
            Ui.Settle();
            window.FindControl<Button>("NoSuchControl")?.Focus();

            Ui.Press(window, CommandId.Find);
            Ui.Settle();

            await Assert.That(Editor(window, "Viewer").SearchPanel.IsOpened).IsTrue();
            await Assert.That(Editor(window, "SqlEditor").SearchPanel.IsOpened).IsFalse();
            window.Close();
        });
    }

    [Test]
    public async Task The_tree_opens_what_fits_and_names_the_selected_path()
    {
        await Ui.Run(async () =>
        {
            var (window, vm) = Scenarios.Shell();
            Ui.Show(window);
            vm.CellInspector.Open("contacts", Contacts, 0, canEdit: false, commit: null, dataTypeName: "jsonb");
            vm.CellInspector.IsTreeView = true;
            Ui.Settle();

            var tree = window.GetVisualDescendants().OfType<TreeView>().First(t => t.Name == "Tree");
            var rows = tree.GetVisualDescendants().OfType<TreeViewItem>().Where(i => i.IsEffectivelyVisible).ToList();
            // $, its five members, and the two addresses under "cc": everything.
            await Assert.That(rows.Count).IsEqualTo(8);

            var carol = vm.CellInspector.TreeRoots[0].Children[1].Children[1];
            tree.SelectedItem = carol;
            Ui.Settle();

            await Assert.That(vm.CellInspector.SelectedPath).IsEqualTo("contacts->'cc'->>1");
            await Assert.That(vm.CellInspector.NodeValue(carol)).IsEqualTo("carol@example.org");
            await Assert.That(CellInspectorViewModel.JsonPath(carol)).IsEqualTo("$.cc[1]");
            window.Close();
        });
    }

    [Test]
    public async Task A_text_column_holding_json_casts_before_its_path()
    {
        await Ui.Run(async () =>
        {
            var inspector = new CellInspectorViewModel();
            inspector.Open("payload", Contacts, 0, canEdit: false, commit: null, dataTypeName: "text");
            var owner = JsonTree.Parse(Contacts)!.Children[0];

            await Assert.That(inspector.SqlPath(owner)).IsEqualTo("payload::jsonb->>'owner'");

            // The notify monitor's payload has no column: a JSON path only.
            inspector.Open("channel", Contacts);
            await Assert.That(inspector.CanCopySqlPath).IsFalse();
            await Assert.That(inspector.SqlPath(owner)).IsNull();
        });
    }

    /// <summary>
    /// Tree or text is a preference: Space down a column of jsonb stays in the
    /// tree once it is picked (it used to be reset on every open), and a value
    /// that isn't JSON shows as text meanwhile.
    /// </summary>
    [Test]
    public async Task The_tree_stays_chosen_from_one_cell_to_the_next()
    {
        await Ui.Run(async () =>
        {
            var inspector = new CellInspectorViewModel();
            inspector.Open("a", Contacts);
            inspector.IsTreeView = true;

            inspector.Open("b", """{"x": [1, 2]}""");
            await Assert.That(inspector.ShowTree).IsTrue();
            await Assert.That(inspector.TreeRoots).IsNotEmpty();

            inspector.Open("c", "not json");
            await Assert.That(inspector.ShowTree).IsFalse();
            await Assert.That(inspector.ShowText).IsTrue();

            inspector.Open("d", "[1]");
            await Assert.That(inspector.ShowTree).IsTrue();
        });
    }
}
