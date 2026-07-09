using Avalonia.Controls;
using Avalonia.Interactivity;

namespace PgNimbus.App.Views;

/// <summary>A minimal yes/no confirmation modal used for destructive actions. Shown via <c>ShowDialog&lt;bool&gt;</c>.</summary>
public partial class ConfirmDialog : Window
{
    public ConfirmDialog()
    {
        InitializeComponent();
        ThemedWindowChrome.Attach(this);
    }

    public ConfirmDialog(string message, string confirmLabel) : this()
    {
        MessageText.Text = message;
        ConfirmButton.Content = confirmLabel;
    }

    private void OnConfirmClick(object? sender, RoutedEventArgs e) => Close(true);

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(false);
}
