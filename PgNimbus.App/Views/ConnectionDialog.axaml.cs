using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
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

    /// <summary>Picking a swatch is the whole point of the flyout — close it rather than making the user click away.</summary>
    private void OnAccentSwatchClick(object? sender, RoutedEventArgs e) => AccentButton.Flyout?.Hide();

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
