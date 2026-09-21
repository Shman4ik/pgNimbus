using PgNimbus.App.ViewModels;
using PgNimbus.App.Views;
using PgNimbus.Core.Connections;

namespace PgNimbus.App.Tests;

[NotInParallel]
public class ConnectionCredentialTests
{
    [Test]
    public async Task Unavailable_storage_warns_and_preserves_database_and_ssh_passwords()
    {
        await Ui.Run(async () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "pgnimbus-dialog-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var store = new RecoverableCredentialStore(new UnavailableStore());
            var vm = new ConnectionDialogViewModel(new ConnectionProfileStore(Path.Combine(directory, "profiles.json")), store)
            {
                Name = "Test", Host = "localhost", Database = "postgres", Username = "postgres",
                Password = "db-password", UseSshTunnel = true, SshHost = "localhost",
                SshUsername = "test", SshPassword = "ssh-password",
            };
            var window = new ConnectionDialog { DataContext = vm };
            try
            {
                Ui.Show(window);
                await vm.SaveCommand.ExecuteAsync(null);
                await Assert.That(vm.Password).IsEqualTo("db-password");
                await Assert.That(vm.SshPassword).IsEqualTo("ssh-password");
                await Assert.That(vm.CredentialWarning).IsNotNull();
                await Assert.That(vm.CredentialsReady).IsTrue();
                var file = File.ReadAllText(Path.Combine(directory, "profiles.json"));
                await Assert.That(file.Contains("db-password")).IsFalse();
                await Assert.That(file.Contains("ssh-password")).IsFalse();
                await vm.DuplicateCommand.ExecuteAsync(null);
                await Assert.That(vm.Password).IsEqualTo("db-password");
                await Assert.That(vm.SshPassword).IsEqualTo("ssh-password");
            }
            finally { window.Close(); Directory.Delete(directory, true); }
        });
    }

    [Test]
    public async Task Saving_empty_password_removes_previous_credential()
    {
        await Ui.Run(async () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "pgnimbus-dialog-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var native = new MemoryCredentialStore();
            var vm = new ConnectionDialogViewModel(new ConnectionProfileStore(Path.Combine(directory, "profiles.json")), new RecoverableCredentialStore(native))
            {
                Name = "Test", Host = "localhost", Database = "postgres", Username = "postgres", Password = "old",
            };
            try
            {
                await vm.SaveCommand.ExecuteAsync(null);
                var id = vm.SelectedProfile!.Id;
                vm.Password = "";
                await vm.SaveCommand.ExecuteAsync(null);
                await Assert.That(native.LoadPassword(id)).IsNull();
                await Assert.That(vm.Password).IsEqualTo("");
            }
            finally { Directory.Delete(directory, true); }
        });
    }

    private sealed class UnavailableStore : ICredentialStore
    {
        public void SavePassword(Guid id, string password) => throw new CredentialStoreException();
        public string? LoadPassword(Guid id) => throw new CredentialStoreException();
        public void DeletePassword(Guid id) => throw new CredentialStoreException();
    }
}
