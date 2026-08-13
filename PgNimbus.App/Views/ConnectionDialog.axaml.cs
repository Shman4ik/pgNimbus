using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PgNimbus.App.ViewModels;
using PgNimbus.Core.Connections;

namespace PgNimbus.App.Views;

public partial class ConnectionDialog : Window
{
    // Drag-to-reorder state for the saved-connections list: the row the
    // pointer went down on, where, and whether it has moved far enough to
    // count as a reorder rather than a click.
    private ConnectionProfile? _dragProfile;
    private Avalonia.Point _dragStartPoint;
    private bool _isReordering;

    public ConnectionDialog()
    {
        InitializeComponent();
        ThemedWindowChrome.Attach(this);

        Opened += OnDialogOpened;

        // Reorder saved connections by dragging rows. Live-moves the row while
        // the pointer travels (the list itself is the drop preview), then
        // persists the order on release. Tunneled press so the row is known
        // even if a child ever marks the event handled.
        ProfilesList.AddHandler(PointerPressedEvent, OnProfilesPointerPressed, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        ProfilesList.PointerMoved += OnProfilesPointerMoved;
        ProfilesList.PointerReleased += OnProfilesPointerReleased;

        // Address-bar behaviour for the paste box. Its content is written by the
        // app, not by the user: the form mirrors itself back into it as a
        // postgres:// URI, so by the time anyone reaches for it it already holds
        // a string that reads as a hint. Splicing a pasted connection string into
        // the middle of that produced a hybrid of the two - which then parsed,
        // and filled the form with nonsense. Selecting everything as focus
        // arrives makes a paste (or a keystroke) replace the whole string, the
        // way an address bar does.
        ImportBox.AddHandler(PointerPressedEvent, OnImportBoxPointerPressed, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        ImportBox.GotFocus += OnImportBoxGotFocus;
    }

    /// <summary>
    /// The click that brings focus to the paste box selects its whole content;
    /// clicks after that place the caret as usual, so the string is still
    /// editable by hand. Left button only - marking a right-click handled would
    /// swallow the box's own cut/copy/paste menu.
    /// </summary>
    private void OnImportBoxPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (ImportBox.IsFocused || !e.GetCurrentPoint(ImportBox).Properties.IsLeftButtonPressed)
        {
            return;
        }

        ImportBox.Focus();
        ImportBox.SelectAll();

        // The TextBox's own press handler runs next and would collapse the
        // selection back to a caret, so this click ends here.
        e.Handled = true;
    }

    /// <summary>Keyboard focus never goes through the pointer handler above, so Tab into the box selects all too.</summary>
    private void OnImportBoxGotFocus(object? sender, FocusChangedEventArgs e)
    {
        if (e.NavigationMethod is NavigationMethod.Tab or NavigationMethod.Directional)
        {
            ImportBox.SelectAll();
        }
    }

    /// <summary>
    /// Puts the keyboard where the next action is: on the preselected
    /// last-used connection (arrows pick another, Enter connects — no click
    /// anywhere), or on the name field when there is nothing to preselect and
    /// the form is what needs filling in. Then, if startup armed it, connects
    /// on its own.
    /// </summary>
    private void OnDialogOpened(object? sender, EventArgs e)
    {
        if (DataContext is not ConnectionDialogViewModel vm)
        {
            return;
        }

        if (vm.SelectedProfile is not null)
        {
            ProfilesList.Focus();
        }
        else
        {
            NameBox.Focus();
        }

        if (vm.ShouldAutoConnect)
        {
            vm.ConnectCommand.Execute(null);
        }
    }

    /// <summary>
    /// Right-clicking a row acts on that row, so the menu's commands (which
    /// read <see cref="ConnectionDialogViewModel.SelectedProfile"/>) target
    /// what the pointer is actually over. Right-clicking empty space below the
    /// rows opens nothing — every entry needs a profile.
    /// </summary>
    private void OnProfilesContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        var profile = (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext as ConnectionProfile;
        if (profile is null || DataContext is not ConnectionDialogViewModel vm)
        {
            e.Handled = true;
            return;
        }

        vm.SelectedProfile = profile;
    }

    /// <summary>
    /// Deleting a saved connection also drops its stored passwords, and nothing
    /// undoes that — hence the confirm the old toolbar button never had.
    /// </summary>
    private async void OnDeleteProfileClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ConnectionDialogViewModel vm || vm.SelectedProfile is not { } profile)
        {
            return;
        }

        var confirm = new ConfirmDialog($"Delete the saved connection \"{profile.Name}\"? Its stored password goes with it.", "Delete");
        if (await confirm.ShowDialog<bool>(this))
        {
            vm.DeleteCommand.Execute(null);
        }
    }

    /// <summary>
    /// Picking a swatch is the whole point of the flyout — close it rather than
    /// making the user click away. Deferred to the next dispatcher pass on
    /// purpose: <c>Button.OnClick</c> raises Click *before* it invokes Command,
    /// and hiding the flyout detaches the swatch from the tree, which tears down
    /// its <c>Command</c> binding — so a synchronous Hide() here swallows the
    /// selection entirely and the accent color never changes.
    /// </summary>
    private void OnAccentSwatchClick(object? sender, RoutedEventArgs e) =>
        Dispatcher.UIThread.Post(() => AccentButton.Flyout?.Hide());

    private void OnProfilesPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(ProfilesList).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _dragProfile = (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext as ConnectionProfile;
        _dragStartPoint = e.GetPosition(ProfilesList);
        _isReordering = false;
    }

    private void OnProfilesPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragProfile is not { } dragged || DataContext is not ConnectionDialogViewModel vm
            || !e.GetCurrentPoint(ProfilesList).Properties.IsLeftButtonPressed)
        {
            return;
        }

        var position = e.GetPosition(ProfilesList);
        if (!_isReordering && Math.Abs(position.Y - _dragStartPoint.Y) < 6)
        {
            return; // still a click, not a drag
        }

        _isReordering = true;

        // The pressed row holds the pointer capture, so e.Source is useless
        // here - hit-test the list by position to find the row underneath.
        var target = ProfilesList.GetVisualAt(position)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext as ConnectionProfile;
        if (target is null || ReferenceEquals(target, dragged))
        {
            return;
        }

        var from = vm.Profiles.IndexOf(dragged);
        var to = vm.Profiles.IndexOf(target);
        if (from >= 0 && to >= 0 && from != to)
        {
            vm.Profiles.Move(from, to);
        }
    }

    private void OnProfilesPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_isReordering && _dragProfile is { } dragged && DataContext is ConnectionDialogViewModel vm)
        {
            vm.SelectedProfile = dragged;
            vm.PersistProfileOrder();
        }

        _dragProfile = null;
        _isReordering = false;
    }

    // Reads the profile off the tapped ListBoxItem's DataContext rather than
    // the ListBox's SelectedItem: on the very first click of a row that
    // wasn't already selected, SelectedItem can still be stale/null at the
    // point this handler runs.
    private void OnProfileDoubleTapped(object? sender, TappedEventArgs e)
    {
        var container = (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true);
        if (container?.DataContext is ConnectionProfile profile && DataContext is ConnectionDialogViewModel vm)
        {
            vm.SelectedProfile = profile;
            vm.ConnectCommand.Execute(null);
        }
    }

    private async void OnCopyConnectionStringClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ConnectionDialogViewModel vm || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
        {
            return;
        }

        try
        {
            // The visible preview masks the password - the clipboard gets the
            // real, usable connection string.
            await clipboard.SetTextAsync(vm.BuildClipboardConnectionString());
        }
        catch
        {
            // Clipboard access can throw if another app holds it locked. This is
            // an async void handler, so an unhandled throw would crash the app —
            // a failed copy is not worth that.
        }
    }
}
