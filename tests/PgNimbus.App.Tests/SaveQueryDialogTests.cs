using Avalonia.Controls;
using Avalonia.Input;
using PgNimbus.App.Views;
using PgNimbus.Core.Query;

namespace PgNimbus.App.Tests;

/// <summary>
/// The first save of a tab asks for a name, and the box opens holding the tab's
/// title, selected, so Cmd/Ctrl+S then Enter saves. It used to open empty for a
/// "Query N" tab, which left Save disabled until something was typed.
/// </summary>
public class SaveQueryDialogTests
{
    [Test]
    public async Task The_suggestion_is_the_tab_title_even_a_placeholder_one()
    {
        await Assert.That(SaveQueryDialog.SuggestName("Query 1", _ => null)).IsEqualTo("Query 1");
        await Assert.That(SaveQueryDialog.SuggestName("  orders ", _ => null)).IsEqualTo("orders");
    }

    /// <summary>
    /// A taken name turns Save into Replace, so the suggestion steps around it:
    /// Enter on what the dialog offered must never overwrite a saved query.
    /// </summary>
    [Test]
    public async Task A_title_already_in_the_list_gets_a_number()
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "orders", "Orders 2" };
        SavedQuery? Find(string name) => taken.Contains(name) ? new SavedQuery(Guid.NewGuid(), name, "SELECT 1;") : null;

        await Assert.That(SaveQueryDialog.SuggestName("orders", Find)).IsEqualTo("orders 3");
        await Assert.That(SaveQueryDialog.SuggestName("customers", Find)).IsEqualTo("customers");
    }

    [Test]
    public async Task The_dialog_opens_with_the_name_selected_and_Enter_saves_it()
    {
        await Ui.Run(async () =>
        {
            var owner = new Window { Width = 800, Height = 600 };
            Ui.Show(owner);

            var dialog = new SaveQueryDialog("Save query", SaveQueryDialog.SuggestName("Query 1", _ => null), currentId: null, _ => null);
            var result = dialog.ShowDialog<SaveQueryResult?>(owner);
            Ui.Settle();

            var box = dialog.FindControl<TextBox>("NameBox")!;
            await Assert.That(box.Text).IsEqualTo("Query 1");
            await Assert.That(box.SelectionStart).IsEqualTo(0);
            await Assert.That(box.SelectionEnd).IsEqualTo("Query 1".Length);
            await Assert.That(dialog.FindControl<Button>("SaveButton")!.IsEnabled).IsTrue();

            Ui.Press(dialog, Key.Enter);

            await Assert.That(result.IsCompleted).IsTrue();
            var saved = await result;
            await Assert.That(saved).IsNotNull();
            await Assert.That(saved!.Name).IsEqualTo("Query 1");
            await Assert.That(saved.OverwriteId).IsNull();

            owner.Close();
        });
    }
}
