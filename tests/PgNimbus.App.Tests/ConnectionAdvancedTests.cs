using System.Security.Cryptography;
using Avalonia.Controls;
using PgNimbus.App.ViewModels;
using PgNimbus.App.Views;
using PgNimbus.Core.Connections;

namespace PgNimbus.App.Tests;

/// <summary>
/// The connect form's Advanced block: a client certificate and key, and the
/// session's statement and lock timeouts. Collapsed until a profile uses it,
/// saved as it is edited like every other field, and never written by merely
/// looking at a profile.
/// </summary>
[NotInParallel]
public class ConnectionAdvancedTests
{
    [Test]
    public async Task The_block_is_closed_by_default_and_opening_it_saves_nothing()
    {
        await Ui.Run(async () =>
        {
            var directory = NewDirectory();
            var path = Path.Combine(directory, "profiles.json");
            var vm = new ConnectionDialogViewModel(new ConnectionProfileStore(path), new MemoryCredentialStore());
            var window = new ConnectionDialog { DataContext = vm };
            try
            {
                Ui.Show(window);
                await Assert.That(window.FindControl<Button>("AdvancedToggle")!.IsEffectivelyVisible).IsTrue();
                await Assert.That(AdvancedVisible(window)).IsFalse();

                vm.ToggleAdvancedCommand.Execute(null);
                Ui.Settle();
                await Assert.That(AdvancedVisible(window)).IsTrue();
                await Assert.That(vm.Profiles).IsEmpty();
                await Assert.That(File.Exists(path)).IsFalse();

                vm.ToggleAdvancedCommand.Execute(null);
                Ui.Settle();
                await Assert.That(AdvancedVisible(window)).IsFalse();
            }
            finally { window.Close(); Directory.Delete(directory, true); }
        });
    }

    [Test]
    public async Task A_profile_that_uses_it_opens_it_and_one_that_does_not_closes_it()
    {
        await Ui.Run(async () =>
        {
            var directory = NewDirectory();
            var path = Path.Combine(directory, "profiles.json");
            var plain = new ConnectionProfile(Guid.NewGuid(), "plain", "db.example.com", 5432, "app", "me");
            var timed = plain with { Id = Guid.NewGuid(), Name = "timed", LockTimeoutSeconds = 5 };
            var certified = plain with { Id = Guid.NewGuid(), Name = "certified", ClientCertificatePath = "/certs/client.crt" };
            new ConnectionProfileStore(path).Save([plain, timed, certified]);

            var vm = new ConnectionDialogViewModel(new ConnectionProfileStore(path), new MemoryCredentialStore(), timed.Id);
            var window = new ConnectionDialog { DataContext = vm };
            try
            {
                Ui.Show(window);
                await Assert.That(AdvancedVisible(window)).IsTrue();
                await Assert.That(window.FindControl<NumericUpDown>("LockTimeoutBox")!.Value).IsEqualTo(5m);
                await Assert.That(window.FindControl<NumericUpDown>("StatementTimeoutBox")!.Value).IsNull();

                vm.SelectedProfile = vm.Profiles.Single(p => p.Name == "plain");
                Ui.Settle();
                await Assert.That(AdvancedVisible(window)).IsFalse();

                vm.SelectedProfile = vm.Profiles.Single(p => p.Name == "certified");
                Ui.Settle();
                await Assert.That(AdvancedVisible(window)).IsTrue();
                await Assert.That(window.FindControl<TextBox>("ClientCertificateBox")!.Text).IsEqualTo("/certs/client.crt");
            }
            finally { window.Close(); Directory.Delete(directory, true); }
        });
    }

    [Test]
    public async Task An_edit_in_the_block_is_saved_and_the_certificate_reaches_the_preview()
    {
        await Ui.Run(async () =>
        {
            var directory = NewDirectory();
            var path = Path.Combine(directory, "profiles.json");
            var profile = new ConnectionProfile(Guid.NewGuid(), "p", "db.example.com", 5432, "app", "me");
            new ConnectionProfileStore(path).Save([profile]);
            var vm = new ConnectionDialogViewModel(new ConnectionProfileStore(path), new MemoryCredentialStore(), profile.Id);
            var window = new ConnectionDialog { DataContext = vm };
            try
            {
                Ui.Show(window);
                vm.ToggleAdvancedCommand.Execute(null);
                Ui.Settle();

                window.FindControl<NumericUpDown>("StatementTimeoutBox")!.Value = 30;
                window.FindControl<NumericUpDown>("LockTimeoutBox")!.Value = 0; // zero is "none"
                window.FindControl<TextBox>("ClientCertificateBox")!.Text = "/certs/client.crt";
                window.FindControl<TextBox>("ClientKeyBox")!.Text = "/certs/client.key";
                Ui.Settle();
                await vm.FlushAsync();

                var saved = new ConnectionProfileStore(path).Load().Single();
                await Assert.That(saved.StatementTimeoutSeconds).IsEqualTo(30);
                await Assert.That(saved.LockTimeoutSeconds).IsNull();
                await Assert.That(saved.ClientCertificatePath).IsEqualTo("/certs/client.crt");
                await Assert.That(saved.ClientKeyPath).IsEqualTo("/certs/client.key");

                // Paths are part of the target, like sslrootcert; the timeouts are
                // the app's own settings and stay out, like read-only.
                await Assert.That(vm.ImportText).Contains("sslcert=%2Fcerts%2Fclient.crt&sslkey=%2Fcerts%2Fclient.key");
                await Assert.That(vm.ImportText).DoesNotContain("timeout");

                // An emptied field is "none" again.
                window.FindControl<NumericUpDown>("StatementTimeoutBox")!.Value = null;
                Ui.Settle();
                await Assert.That(new ConnectionProfileStore(path).Load().Single().StatementTimeoutSeconds).IsNull();
            }
            finally { window.Close(); Directory.Delete(directory, true); }
        });
    }

    [Test]
    public async Task Selecting_a_profile_that_uses_the_block_writes_nothing()
    {
        await Ui.Run(async () =>
        {
            var directory = NewDirectory();
            var path = Path.Combine(directory, "profiles.json");
            var first = new ConnectionProfile(Guid.NewGuid(), "first", "db.example.com", 5432, "app", "me",
                ClientCertificatePath: "/certs/a.crt", ClientKeyPath: "/certs/a.key", StatementTimeoutSeconds: 30);
            var second = first with { Id = Guid.NewGuid(), Name = "second", ClientCertificatePath = "/certs/b.crt", LockTimeoutSeconds = 2, StatementTimeoutSeconds = null };
            new ConnectionProfileStore(path).Save([first, second]);
            var before = File.ReadAllText(path);
            var stamp = File.GetLastWriteTimeUtc(path);

            var vm = new ConnectionDialogViewModel(new ConnectionProfileStore(path), new MemoryCredentialStore());
            var window = new ConnectionDialog { DataContext = vm };
            try
            {
                Ui.Show(window);
                vm.SelectedProfile = vm.Profiles[0];
                Ui.Settle();
                vm.SelectedProfile = vm.Profiles[1];
                Ui.Settle();
                vm.NewCommand.Execute(null);
                Ui.Settle();
                await vm.FlushAsync();

                await Assert.That(File.ReadAllText(path)).IsEqualTo(before);
                await Assert.That(File.GetLastWriteTimeUtc(path)).IsEqualTo(stamp);
                await Assert.That(AdvancedVisible(window)).IsFalse();
            }
            finally { window.Close(); Directory.Delete(directory, true); }
        });
    }

    [Test]
    public async Task A_key_with_a_passphrase_is_refused_before_anything_connects()
    {
        await Ui.Run(async () =>
        {
            var directory = NewDirectory();
            var certificatePath = Path.Combine(directory, "client.crt");
            var keyPath = Path.Combine(directory, "client.key");
            File.WriteAllText(certificatePath, "-----BEGIN CERTIFICATE-----\n-----END CERTIFICATE-----\n");
            using (var key = RSA.Create(2048))
            {
                File.WriteAllText(keyPath, key.ExportEncryptedPkcs8PrivateKeyPem("secret",
                    new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 10_000)));
            }

            var vm = new ConnectionDialogViewModel(new ConnectionProfileStore(Path.Combine(directory, "profiles.json")), new MemoryCredentialStore())
            {
                Host = "203.0.113.1",
            };
            try
            {
                vm.ClientCertificatePath = certificatePath;
                vm.ClientKeyPath = keyPath;
                vm.IsAdvancedOpen = false;
                await vm.TestConnectionCommand.ExecuteAsync(null);

                await Assert.That(vm.ErrorMessage).Contains("passphrase");
                await Assert.That(vm.IsAdvancedOpen).IsTrue();
                await Assert.That(vm.StatusMessage).IsNull();
            }
            finally { await vm.FlushAsync(); Directory.Delete(directory, true); }
        });
    }

    private static bool AdvancedVisible(Window window) =>
        window.FindControl<Border>("AdvancedBlock")!.IsEffectivelyVisible;

    private static string NewDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "pgnimbus-advanced-dialog-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}
