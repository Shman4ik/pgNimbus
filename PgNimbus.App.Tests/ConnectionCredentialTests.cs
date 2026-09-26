using Avalonia.Controls;
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
            var directory = NewDirectory();
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
                await vm.FlushAsync();
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
    public async Task Clearing_the_password_removes_the_stored_credential()
    {
        await Ui.Run(async () =>
        {
            var directory = NewDirectory();
            var native = new MemoryCredentialStore();
            var vm = new ConnectionDialogViewModel(new ConnectionProfileStore(Path.Combine(directory, "profiles.json")), new RecoverableCredentialStore(native))
            {
                Name = "Test", Host = "localhost", Database = "postgres", Username = "postgres", Password = "old",
            };
            try
            {
                await vm.FlushAsync();
                var id = vm.SelectedProfile!.Id;
                await Assert.That(native.LoadPassword(id)).IsEqualTo("old");
                vm.Password = "";
                await vm.FlushAsync();
                await Assert.That(native.LoadPassword(id)).IsNull();
                await Assert.That(vm.Password).IsEqualTo("");
            }
            finally { Directory.Delete(directory, true); }
        });
    }

    [Test]
    public async Task Typing_into_a_blank_form_saves_a_new_profile_without_a_save_button()
    {
        await Ui.Run(async () =>
        {
            var directory = NewDirectory();
            var path = Path.Combine(directory, "profiles.json");
            var vm = new ConnectionDialogViewModel(new ConnectionProfileStore(path), new MemoryCredentialStore());
            try
            {
                await Assert.That(vm.Profiles.Count).IsEqualTo(0);

                vm.Host = "db.example.com";
                vm.Database = "app";

                // One profile, grown by both edits — not one per keystroke.
                await Assert.That(vm.Profiles.Count).IsEqualTo(1);
                await Assert.That(vm.SelectedProfile).IsNotNull();
                var saved = new ConnectionProfileStore(path).Load().Single();
                await Assert.That(saved.Host).IsEqualTo("db.example.com");
                await Assert.That(saved.Database).IsEqualTo("app");
                await Assert.That(saved.Name).IsEqualTo("db.example.com/app");
            }
            finally { Directory.Delete(directory, true); }
        });
    }

    [Test]
    public async Task Editing_a_saved_profile_keeps_it_selected_and_writes_it_at_once()
    {
        await Ui.Run(async () =>
        {
            var directory = NewDirectory();
            var path = Path.Combine(directory, "profiles.json");
            var first = Profile("First", "one.example.com");
            var second = Profile("Second", "two.example.com");
            new ConnectionProfileStore(path).Save([first, second]);

            var credentials = new MemoryCredentialStore();
            var vm = new ConnectionDialogViewModel(new ConnectionProfileStore(path), credentials, first.Id);
            var window = new ConnectionDialog { DataContext = vm };
            try
            {
                Ui.Show(window);
                var list = window.FindControl<ListBox>("ProfilesList")!;

                vm.Port = 6543;
                vm.Password = "secret";

                // The list swapped in the edited record, and neither it nor the
                // form lost its place while that happened.
                await Assert.That(list.SelectedItem).IsSameReferenceAs(vm.SelectedProfile);
                await Assert.That(vm.SelectedProfile!.Id).IsEqualTo(first.Id);
                await Assert.That(vm.SelectedProfile.Port).IsEqualTo(6543);
                await Assert.That(vm.Host).IsEqualTo("one.example.com");
                await Assert.That(new ConnectionProfileStore(path).Load().First().Port).IsEqualTo(6543);

                // Picking another row before the password settled still files it
                // under the profile it was typed into.
                list.SelectedIndex = 1;
                await vm.FlushAsync();
                await Assert.That(vm.Host).IsEqualTo("two.example.com");
                await Assert.That(credentials.LoadPassword(first.Id)).IsEqualTo("secret");
                await Assert.That(credentials.LoadPassword(second.Id)).IsNull();

                list.SelectedIndex = 0;
                await vm.FlushAsync();
                await Assert.That(Ui.SettleUntil(() => vm.Password == "secret")).IsTrue();
                await Assert.That(vm.Port).IsEqualTo(6543);
            }
            finally { window.Close(); Directory.Delete(directory, true); }
        });
    }

    [Test]
    public async Task Selecting_a_profile_writes_nothing()
    {
        await Ui.Run(async () =>
        {
            var directory = NewDirectory();
            var path = Path.Combine(directory, "profiles.json");
            var first = Profile("First", "one.example.com");
            var second = Profile("Second", "two.example.com");
            new ConnectionProfileStore(path).Save([first, second]);
            var before = File.GetLastWriteTimeUtc(path);
            var credentials = new CountingStore();

            var vm = new ConnectionDialogViewModel(new ConnectionProfileStore(path), credentials, first.Id);
            try
            {
                vm.SelectedProfile = vm.Profiles[1];
                vm.NewCommand.Execute(null);
                await vm.FlushAsync();

                await Assert.That(File.GetLastWriteTimeUtc(path)).IsEqualTo(before);
                await Assert.That(credentials.Writes).IsEqualTo(0);
                await Assert.That(vm.Profiles.Count).IsEqualTo(2);
            }
            finally { Directory.Delete(directory, true); }
        });
    }

    [Test]
    public async Task Deleting_a_profile_drops_a_password_still_settling()
    {
        await Ui.Run(async () =>
        {
            var directory = NewDirectory();
            var path = Path.Combine(directory, "profiles.json");
            var profile = Profile("Only", "one.example.com");
            new ConnectionProfileStore(path).Save([profile]);
            var credentials = new MemoryCredentialStore();
            var vm = new ConnectionDialogViewModel(new ConnectionProfileStore(path), credentials, profile.Id)
            {
                CredentialSaveDelay = TimeSpan.FromMinutes(1),
            };
            try
            {
                await vm.FlushAsync();
                vm.Password = "typed";
                await vm.DeleteCommand.ExecuteAsync(null);
                await Task.Delay(50);
                await vm.FlushAsync();

                await Assert.That(credentials.LoadPassword(profile.Id)).IsNull();
                await Assert.That(vm.Profiles.Count).IsEqualTo(0);
                await Assert.That(new ConnectionProfileStore(path).Load().Count).IsEqualTo(0);
            }
            finally { Directory.Delete(directory, true); }
        });
    }

    private static ConnectionProfile Profile(string name, string host) =>
        new(Guid.NewGuid(), name, host, 5432, "postgres", "postgres", SslMode.Prefer, null, null);

    private static string NewDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "pgnimbus-dialog-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private sealed class UnavailableStore : ICredentialStore
    {
        public void SavePassword(Guid id, string password) => throw new CredentialStoreException();
        public string? LoadPassword(Guid id) => throw new CredentialStoreException();
        public void DeletePassword(Guid id) => throw new CredentialStoreException();
    }

    private sealed class CountingStore : ICredentialStore
    {
        public int Writes;
        public void SavePassword(Guid id, string password) => Interlocked.Increment(ref Writes);
        public string? LoadPassword(Guid id) => null;
        public void DeletePassword(Guid id) => Interlocked.Increment(ref Writes);
    }
}
