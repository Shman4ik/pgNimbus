using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using PgNimbus.Core.Query;

namespace PgNimbus.App.Views;

/// <summary>What the user settled on: the name, and the entry it replaces (null = a new one).</summary>
public sealed record SaveQueryResult(string Name, Guid? OverwriteId);

/// <summary>
/// Names a query on its way into the Saved Queries list. Shown via
/// <c>ShowDialog&lt;SaveQueryResult?&gt;</c>; null means cancelled.
/// <para>
/// The one piece of real logic here is the name-collision check. Saving used to
/// mint a fresh <see cref="Guid"/> every time, so saving the same tab twice —
/// or reusing a name on purpose — left two rows the user could not tell apart.
/// Typing a name that is already taken now says so and turns Save into Replace,
/// which is the only honest way to offer a list keyed by a name people read.
/// </para>
/// </summary>
public partial class SaveQueryDialog : Window
{
    private readonly Func<string, SavedQuery?>? _findByName;

    // The entry this tab is already saved as, if any: matching it is an
    // ordinary update, not a collision, so it must not raise the warning.
    private readonly Guid? _currentId;

    private Guid? _conflictId;

    public SaveQueryDialog()
    {
        InitializeComponent();
        ThemedWindowChrome.Attach(this);
    }

    public SaveQueryDialog(string heading, string initialName, Guid? currentId, Func<string, SavedQuery?> findByName)
        : this()
    {
        HeadingText.Text = heading;
        _currentId = currentId;
        _findByName = findByName;

        NameBox.Text = initialName;
        NameBox.TextChanged += (_, _) => UpdateConflictState();
        UpdateConflictState();

        // The name is the whole point of the dialog, and it arrives pre-filled
        // with a guess — so select it, the way a rename box does, and typing
        // replaces it without a select-all first.
        Opened += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };
    }

    private void UpdateConflictState()
    {
        var name = (NameBox.Text ?? string.Empty).Trim();
        SaveButton.IsEnabled = name.Length > 0;

        var match = name.Length > 0 ? _findByName?.Invoke(name) : null;
        _conflictId = match is not null && match.Id != _currentId ? match.Id : null;

        ConflictText.IsVisible = _conflictId is not null;
        ConflictText.Text = _conflictId is null
            ? string.Empty
            : $"A saved query named \u201c{match!.Name}\u201d already exists. Saving replaces it.";
        SaveButton.Content = _conflictId is null ? "Save" : "Replace";
    }

    private void OnNameBoxKeyDown(object? sender, KeyEventArgs e)
    {
        // Enter commits and Escape cancels, so the whole dialog is one gesture
        // away from done without the pointer ever leaving the keyboard.
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            Commit();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close(null);
        }
    }

    private void OnSaveClick(object? sender, RoutedEventArgs e) => Commit();

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);

    private void Commit()
    {
        var name = (NameBox.Text ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            return;
        }

        // A name collision takes over the row it collided with; otherwise this
        // updates the tab's own entry, or creates one when there is none.
        Close(new SaveQueryResult(name, _conflictId ?? _currentId));
    }
}
