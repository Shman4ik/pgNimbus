using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using PgNimbus.App.ViewModels;
using PgNimbus.Core.Localization;
using PgNimbus.Core.Query;

namespace PgNimbus.App.Views;

/// <summary>What the user settled on: the name, and the entry it replaces (null = a new one).</summary>
public sealed record SaveQueryResult(string Name, Guid? OverwriteId);

/// <summary>
/// What a naming dialog names, which decides the sentence it says when the name is
/// taken. A sentence per kind, not a noun dropped into one: other languages cannot
/// build "A saved plan named … already exists" from a word.
/// </summary>
public enum SaveQueryKind
{
    SavedQuery,
    SavedPlan,

    /// <summary>A table or view being renamed: the server refuses a taken name, so nothing collides here.</summary>
    Relation,
}

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
    private readonly Func<string, (Guid Id, string Name)?>? _findByName;

    // What a name collides with, which words the warning.
    private readonly SaveQueryKind _kind = SaveQueryKind.SavedQuery;

    // The entry this tab is already saved as, if any: matching it is an
    // ordinary update, not a collision, so it must not raise the warning.
    private readonly Guid? _currentId;

    private Guid? _conflictId;

    // The primary button's word when nothing collides: "Save", or "Rename"
    // when the dialog names a relation for the schema tree's Rename….
    private readonly Func<string> _saveLabel = () => Strings.Common_Save;

    public SaveQueryDialog()
    {
        InitializeComponent();
        DialogChrome.Attach(this);
    }

    public SaveQueryDialog(string heading, string initialName, Guid? currentId, Func<string, SavedQuery?> findByName)
        : this(() => heading, initialName, currentId, findByName)
    {
    }

    /// <summary>
    /// The saved-query dialog with a heading composed on demand, so it follows a change
    /// of language while the dialog is open.
    /// </summary>
    public SaveQueryDialog(Func<string> heading, string initialName, Guid? currentId, Func<string, SavedQuery?> findByName)
        : this(heading, initialName, currentId, name => findByName(name) is { } query ? (query.Id, query.Name) : null,
            SaveQueryKind.SavedQuery, () => Strings.Dialogs_SaveQueryTitle)
    {
    }

    /// <summary>
    /// Names any kind of saved item: <paramref name="findByName"/> answers the id and name
    /// of the item a name already belongs to, and <paramref name="kind"/> says what it is
    /// in the collision warning. The heading, the window title and the primary button's
    /// word are composed on demand (<paramref name="saveLabel"/> defaults to Save).
    /// </summary>
    public SaveQueryDialog(
        Func<string> heading,
        string initialName,
        Guid? currentId,
        Func<string, (Guid Id, string Name)?> findByName,
        SaveQueryKind kind,
        Func<string> windowTitle,
        Func<string>? saveLabel = null)
        : this()
    {
        this.Bind(TitleProperty, Localizer.Observe(windowTitle).ToBinding());
        if (saveLabel is not null)
        {
            _saveLabel = saveLabel;
        }

        HeadingText.Bind(TextBlock.TextProperty, Localizer.Observe(heading).ToBinding());
        _currentId = currentId;
        _findByName = findByName;
        _kind = kind;

        NameBox.Text = initialName;
        NameBox.TextChanged += (_, _) => UpdateConflictState();
        UpdateConflictState();
        Localizer.WhenChanged(this, static dialog => dialog.UpdateConflictState());

        // The name is the whole point of the dialog, and it arrives pre-filled
        // with a guess — so select it, the way a rename box does, and typing
        // replaces it without a select-all first.
        Opened += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };
    }

    /// <summary>
    /// What the name box opens with when a tab is saved for the first time: the
    /// tab's own title, selected, so Enter alone saves it. That includes the
    /// "Query N" placeholder — an empty box, which is what it used to open with,
    /// left Save disabled and made the most common save (Cmd/Ctrl+S, Enter) a
    /// dead end until something was typed. A title already in the list gets a
    /// number (" 2", " 3", …) instead, because the dialog turns a taken name into
    /// Replace, and Enter on a suggestion must never overwrite a saved query.
    /// </summary>
    public static string SuggestName(string title, Func<string, SavedQuery?> findByName) =>
        SaveQueryNames.Suggest(title, name => findByName(name) is not null);

    private void UpdateConflictState()
    {
        var name = (NameBox.Text ?? string.Empty).Trim();
        SaveButton.IsEnabled = name.Length > 0;

        var match = name.Length > 0 ? _findByName?.Invoke(name) : null;
        _conflictId = match is { } taken && taken.Id != _currentId ? taken.Id : null;

        ConflictText.IsVisible = _conflictId is not null;
        ConflictText.Text = _conflictId is null
            ? string.Empty
            : _kind == SaveQueryKind.SavedPlan
                ? Strings.Dialogs_NameTakenPlan(match!.Value.Name)
                : Strings.Dialogs_NameTakenQuery(match!.Value.Name);
        SaveButton.Content = _conflictId is null ? _saveLabel() : Strings.Dialogs_ReplaceButton;
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
