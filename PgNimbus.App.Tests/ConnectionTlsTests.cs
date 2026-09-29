using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using PgNimbus.App.ViewModels;
using PgNimbus.App.Views;
using PgNimbus.Core.Connections;

namespace PgNimbus.App.Tests;

/// <summary>
/// Security audit 2026-09, finding 9, in the connect form: a new profile
/// starts at Require, the picker says what each mode checks, and the root
/// certificate a provider CA needs has a field that shows for the verifying
/// modes and is saved like every other one.
/// </summary>
[NotInParallel]
public class ConnectionTlsTests
{
    [Test]
    public async Task A_new_profile_starts_at_prefer_for_this_machine_and_at_require_for_a_remote_host()
    {
        // Require by default (finding 9), except for this machine, where there is
        // no network path to attack and a local Docker Postgres has TLS off, so
        // Require failed on the first connect anyone tries (review of the fixes).
        await Ui.Run(async () =>
        {
            var directory = NewDirectory();
            var vm = new ConnectionDialogViewModel(new ConnectionProfileStore(Path.Combine(directory, "profiles.json")), new MemoryCredentialStore());
            var window = new ConnectionDialog { DataContext = vm };
            try
            {
                Ui.Show(window);
                var combo = window.FindControl<ComboBox>("SslModeCombo")!;

                // A blank host means the localhost placeholder.
                await Assert.That(combo.SelectedItem).IsEqualTo(SslMode.Prefer);
                await Assert.That(RootCertificateVisible(window)).IsFalse();

                // A remote host moves it to Require; the first edit saves it so.
                vm.Host = "db.example.com";
                await vm.FlushAsync();
                await Assert.That(vm.SslMode).IsEqualTo(SslMode.Require);
                await Assert.That(TextsUnder(combo)).Contains("Require");
                await Assert.That(vm.SelectedProfile!.SslMode).IsEqualTo(SslMode.Require);

                // It follows the host while nobody has chosen a mode...
                vm.Host = "127.0.0.1";
                await Assert.That(vm.SslMode).IsEqualTo(SslMode.Prefer);

                // ...and not once someone has.
                vm.SslMode = SslMode.VerifyFull;
                vm.Host = "db.example.com";
                vm.Host = "localhost";
                await Assert.That(vm.SslMode).IsEqualTo(SslMode.VerifyFull);

                // New follows the host again.
                vm.NewCommand.Execute(null);
                await Assert.That(vm.SslMode).IsEqualTo(SslMode.Prefer);
                vm.Host = "db.example.com";
                await Assert.That(vm.SslMode).IsEqualTo(SslMode.Require);
            }
            finally { window.Close(); Directory.Delete(directory, true); }
        });
    }

    [Test]
    public async Task The_picker_describes_each_mode_and_recommends_verify_full()
    {
        await Ui.Run(async () =>
        {
            var directory = NewDirectory();
            var vm = new ConnectionDialogViewModel(new ConnectionProfileStore(Path.Combine(directory, "profiles.json")), new MemoryCredentialStore());
            var window = new ConnectionDialog { DataContext = vm };
            try
            {
                Ui.Show(window);
                var combo = window.FindControl<ComboBox>("SslModeCombo")!;
                combo.IsDropDownOpen = true;
                Ui.Settle();

                var items = combo.GetLogicalChildren().OfType<ComboBoxItem>().ToList();
                await Assert.That(items.Count).IsEqualTo(Enum.GetValues<SslMode>().Length);
                foreach (var item in items)
                {
                    var info = SslModes.Describe((SslMode)item.DataContext!);
                    var texts = TextsUnder(item);
                    await Assert.That(texts).Contains(info.Label);
                    await Assert.That(texts).Contains(info.Description);
                    var recommended = item.GetVisualDescendants().OfType<TextBlock>()
                        .Single(t => t.Text == "Recommended");
                    await Assert.That(recommended.IsVisible).IsEqualTo(info.Recommended);
                }

                combo.IsDropDownOpen = false;
            }
            finally { window.Close(); Directory.Delete(directory, true); }
        });
    }

    [Test]
    public async Task The_root_certificate_field_shows_for_verify_full_and_is_saved()
    {
        await Ui.Run(async () =>
        {
            var directory = NewDirectory();
            var path = Path.Combine(directory, "profiles.json");
            var vm = new ConnectionDialogViewModel(new ConnectionProfileStore(path), new MemoryCredentialStore())
            {
                Host = "mydb.abc123.eu-west-1.rds.amazonaws.com",
                Database = "app",
            };
            var window = new ConnectionDialog { DataContext = vm };
            try
            {
                Ui.Show(window);
                vm.SslMode = SslMode.VerifyCa;
                Ui.Settle();
                await Assert.That(RootCertificateVisible(window)).IsTrue();

                vm.SslMode = SslMode.VerifyFull;
                Ui.Settle();
                await Assert.That(RootCertificateVisible(window)).IsTrue();

                var box = window.FindControl<TextBox>("RootCertificateBox")!;
                box.Text = "/certs/global-bundle.pem";
                Ui.Settle();
                await vm.FlushAsync();

                var saved = new ConnectionProfileStore(path).Load().Single();
                await Assert.That(saved.SslMode).IsEqualTo(SslMode.VerifyFull);
                await Assert.That(saved.RootCertificatePath).IsEqualTo("/certs/global-bundle.pem");

                // The preview carries it, and parses back to the same fields.
                await Assert.That(vm.ImportText).Contains("sslmode=verify-full&sslrootcert=%2Fcerts%2Fglobal-bundle.pem");
                await Assert.That(ConnectionStringParser.TryParse(vm.ImportText, out var parsed, out _)).IsTrue();
                await Assert.That(parsed.SslMode).IsEqualTo(SslMode.VerifyFull);
                await Assert.That(parsed.RootCertificatePath).IsEqualTo("/certs/global-bundle.pem");

                // A mode that checks nothing hides it and leaves it out of the
                // preview, but the profile keeps it for a switch back.
                vm.SslMode = SslMode.Require;
                Ui.Settle();
                await Assert.That(RootCertificateVisible(window)).IsFalse();
                await Assert.That(vm.ImportText).DoesNotContain("sslrootcert");
                await Assert.That(new ConnectionProfileStore(path).Load().Single().RootCertificatePath).IsEqualTo("/certs/global-bundle.pem");

                // Reopened, the form shows what was saved.
                vm.SslMode = SslMode.VerifyFull;
                var reopened = new ConnectionDialogViewModel(new ConnectionProfileStore(path), new MemoryCredentialStore(), vm.SelectedProfile!.Id);
                await Assert.That(reopened.SslMode).IsEqualTo(SslMode.VerifyFull);
                await Assert.That(reopened.RootCertificatePath).IsEqualTo("/certs/global-bundle.pem");
            }
            finally { window.Close(); Directory.Delete(directory, true); }
        });
    }

    [Test]
    public async Task A_pasted_string_fills_the_root_certificate()
    {
        await Ui.Run(async () =>
        {
            var directory = NewDirectory();
            var vm = new ConnectionDialogViewModel(new ConnectionProfileStore(Path.Combine(directory, "profiles.json")), new MemoryCredentialStore());
            try
            {
                vm.ImportText = "host=db.example.com dbname=app sslmode=verify-full sslrootcert=/certs/ca.pem";
                await vm.FlushAsync();
                await Assert.That(vm.SslMode).IsEqualTo(SslMode.VerifyFull);
                await Assert.That(vm.RootCertificatePath).IsEqualTo("/certs/ca.pem");
                await Assert.That(vm.SelectedProfile!.RootCertificatePath).IsEqualTo("/certs/ca.pem");

                // Npgsql's own keyword too.
                vm.ImportText = "Host=db.example.com;SSL Mode=VerifyCA;Root Certificate=/certs/other.pem";
                await Assert.That(vm.SslMode).IsEqualTo(SslMode.VerifyCa);
                await Assert.That(vm.RootCertificatePath).IsEqualTo("/certs/other.pem");
            }
            finally { Directory.Delete(directory, true); }
        });
    }

    private static bool RootCertificateVisible(Window window) =>
        window.FindControl<TextBox>("RootCertificateBox")!.IsEffectivelyVisible;

    private static List<string> TextsUnder(Control control) =>
        control.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? string.Empty).ToList();

    private static string NewDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "pgnimbus-tls-dialog-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}
