using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using PgNimbus.App.ViewModels;
using PgNimbus.App.Views;
using PgNimbus.Core.Connections;
using PgNimbus.Core.Localization;
using PgNimbus.Core.Settings;
using PgNimbus.Screenshot;

namespace PgNimbus.App.Tests;

/// <summary>
/// The connection dialog is the first window anyone sees, so its bar carries the
/// interface language: someone who can't read English must get past it without
/// Settings, which is behind a connection. Switches the process-wide language,
/// hence <c>[NotInParallel]</c> and English put back however it ends.
/// </summary>
[NotInParallel]
public class ConnectionLanguageTests
{
    [Test]
    public async Task Picking_a_language_in_the_connection_dialog_switches_it_and_saves_it()
    {
        var before = new AppSettingsStore().Load().Language;
        try
        {
            await Ui.Run(async () =>
            {
                var path = Path.Combine(IsolatedAppData.NewDirectory("connection-language"), "profiles.json");
                var vm = new ConnectionDialogViewModel(new ConnectionProfileStore(path), new MemoryCredentialStore());
                var window = new ConnectionDialog { DataContext = vm };
                Ui.Show(window);

                bool Shows(string text) => window.GetVisualDescendants().OfType<TextBlock>().Any(t => t.IsEffectivelyVisible && t.Text == text);

                var button = window.FindControl<Button>("LanguageButton")!;
                await Assert.That(button.IsEffectivelyVisible).IsTrue();
                await Assert.That(Shows("English")).IsTrue();
                await Assert.That(Shows("Saved connections")).IsTrue();

                // A real click opens the menu: "System (…)" first, then each language in itself.
                var centre = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
                window.MouseMove(centre);
                window.MouseDown(centre, MouseButton.Left);
                window.MouseUp(centre, MouseButton.Left);
                Ui.Settle();

                var menu = window.LanguageMenu!;
                await Assert.That(menu.IsOpen).IsTrue();
                var items = menu.Items.OfType<MenuItem>().ToList();
                await Assert.That((string)items[0].Header!).StartsWith("System (");
                await Assert.That(items.Select(i => (string)i.Header!)).Contains("English");
                await Assert.That(items.Where(i => i.IsChecked).Count()).IsEqualTo(1);

                var russian = items.Single(i => (string?)i.Header == "Русский");
                russian.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                menu.Hide();
                Ui.Settle();

                await Assert.That(Localizer.Language).IsEqualTo("ru");
                await Assert.That(Shows(Strings.Connection_SavedConnections)).IsTrue();
                await Assert.That(Strings.Connection_SavedConnections).IsNotEqualTo("Saved connections");
                await Assert.That(Shows("Saved connections")).IsFalse();
                await Assert.That(Shows("Русский")).IsTrue();
                await Assert.That(new AppSettingsStore().Load().Language).IsEqualTo("ru");

                // Opened again, the menu checks what was saved.
                window.MouseDown(centre, MouseButton.Left);
                window.MouseUp(centre, MouseButton.Left);
                Ui.Settle();
                var reopened = window.LanguageMenu!.Items.OfType<MenuItem>().Single(i => i.IsChecked);
                await Assert.That((string?)reopened.Header).IsEqualTo("Русский");
                window.LanguageMenu.Hide();

                window.Close();
            });
        }
        finally
        {
            await Ui.Run(() =>
            {
                var store = new AppSettingsStore();
                store.Save(store.Load() with { Language = before });
                Localizer.Load(Localizer.English);
                return Task.CompletedTask;
            });
        }
    }
}
