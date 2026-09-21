using System.Text;
using PgNimbus.Core.Connections;

namespace PgNimbus.Core.Tests.Connections;

public class CredentialStoreTests
{
    private sealed class Fixture : IDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "pgnimbus-credential-tests", Guid.NewGuid().ToString("N"));
        public Guid Id { get; } = Guid.NewGuid();
        public FakeStore Native { get; } = new();
        public RecoverableCredentialStore Store { get; }
        public string Legacy => Path.Combine(Directory, $"{Id:N}.cred");
        public Fixture()
        {
            System.IO.Directory.CreateDirectory(Directory);
            Store = new(Native, Directory);
        }
        public void Seed(string password) => File.WriteAllText(Legacy, Convert.ToBase64String(Encoding.UTF8.GetBytes(password)));
        public void Dispose() => System.IO.Directory.Delete(Directory, true);
    }

    private sealed class FakeStore : ICredentialStore
    {
        public Dictionary<Guid, string> Values { get; } = [];
        public bool Unavailable { get; set; }
        public bool IgnoreWrites { get; set; }
        public void SavePassword(Guid id, string password)
        {
            if (Unavailable) throw new CredentialStoreException();
            if (!IgnoreWrites) Values[id] = password;
        }
        public string? LoadPassword(Guid id)
        {
            if (Unavailable) throw new CredentialStoreException();
            return Values.GetValueOrDefault(id);
        }
        public void DeletePassword(Guid id)
        {
            if (Unavailable) throw new CredentialStoreException();
            Values.Remove(id);
        }
    }

    [Test]
    public async Task Migration_verifies_native_value_before_removing_legacy_file()
    {
        using var f = new Fixture();
        f.Seed("пароль 🔑\n'with quotes'");
        var loaded = f.Store.LoadPassword(f.Id);
        await Assert.That(loaded).IsEqualTo("пароль 🔑\n'with quotes'");
        await Assert.That(f.Native.Values[f.Id]).IsEqualTo(loaded);
        await Assert.That(File.Exists(f.Legacy)).IsFalse();
        await Assert.That(f.Store.Warning).IsNull();
    }

    [Test]
    public async Task Failed_verification_keeps_legacy_and_warns_without_losing_session_password()
    {
        using var f = new Fixture();
        f.Seed("legacy-password");
        f.Native.IgnoreWrites = true;
        await Assert.That(f.Store.LoadPassword(f.Id)).IsEqualTo("legacy-password");
        await Assert.That(File.Exists(f.Legacy)).IsTrue();
        await Assert.That(f.Store.Warning).IsNotNull();
        f.Native.IgnoreWrites = false;
        f.Store.SavePassword(f.Id, "new-password");
        await Assert.That(f.Native.Values[f.Id]).IsEqualTo("new-password");
        await Assert.That(File.Exists(f.Legacy)).IsFalse();
        await Assert.That(f.Store.Warning).IsNull();
    }

    [Test]
    public async Task Unavailable_store_keeps_new_password_only_in_memory_and_does_not_create_files()
    {
        using var f = new Fixture();
        f.Native.Unavailable = true;
        f.Store.SavePassword(f.Id, "session-password");
        await Assert.That(f.Store.LoadPassword(f.Id)).IsEqualTo("session-password");
        await Assert.That(System.IO.Directory.GetFiles(f.Directory).Length).IsEqualTo(0);
        await Assert.That(f.Store.Warning).IsNotNull();
        await Assert.That(f.Store.Warning!.Contains("session-password")).IsFalse();
        await Assert.That(new RecoverableCredentialStore(f.Native, f.Directory).LoadPassword(f.Id)).IsNull();
    }

    [Test]
    public async Task Unavailable_store_preserves_existing_file_for_retry()
    {
        using var f = new Fixture();
        f.Seed("existing");
        f.Native.Unavailable = true;
        await Assert.That(f.Store.LoadPassword(f.Id)).IsEqualTo("existing");
        await Assert.That(File.Exists(f.Legacy)).IsTrue();
        f.Native.Unavailable = false;
        f.Store.SavePassword(f.Id, "existing");
        await Assert.That(File.Exists(f.Legacy)).IsFalse();
    }

    [Test]
    public async Task Native_entry_wins_over_different_legacy_value_until_explicit_save()
    {
        using var f = new Fixture();
        f.Seed("old");
        f.Native.Values[f.Id] = "new";
        await Assert.That(f.Store.LoadPassword(f.Id)).IsEqualTo("new");
        await Assert.That(File.Exists(f.Legacy)).IsTrue();
        f.Store.SavePassword(f.Id, "new");
        await Assert.That(File.Exists(f.Legacy)).IsFalse();
    }

    [Test]
    public async Task Corrupt_legacy_file_does_not_hide_valid_native_entry()
    {
        using var f = new Fixture();
        File.WriteAllText(f.Legacy, "not base64!");
        f.Native.Values[f.Id] = "native";
        await Assert.That(f.Store.LoadPassword(f.Id)).IsEqualTo("native");
        await Assert.That(File.Exists(f.Legacy)).IsTrue();
        await Assert.That(f.Store.Warning).IsNotNull();
    }

    [Test]
    public async Task Delete_failure_warns_and_does_not_resurrect_password_in_session()
    {
        using var f = new Fixture();
        f.Store.SavePassword(f.Id, "secret");
        f.Seed("old");
        f.Native.Unavailable = true;
        f.Store.DeletePassword(f.Id);
        await Assert.That(f.Store.LoadPassword(f.Id)).IsNull();
        await Assert.That(File.Exists(f.Legacy)).IsFalse();
        await Assert.That(f.Store.Warning).IsNotNull();
        f.Native.Unavailable = false;
        f.Store.DeletePassword(f.Id);
        await Assert.That(f.Native.Values.Count).IsEqualTo(0);
        await Assert.That(f.Store.Warning).IsNull();
    }

    [Test]
    public async Task Successful_second_credential_does_not_hide_first_failure()
    {
        using var f = new Fixture();
        f.Native.Unavailable = true;
        f.Store.SavePassword(f.Id, "db");
        f.Native.Unavailable = false;
        f.Store.SavePassword(Guid.NewGuid(), "ssh");
        await Assert.That(f.Store.Warning).IsNotNull();
        f.Store.SavePassword(f.Id, "db");
        await Assert.That(f.Store.Warning).IsNull();
    }

    [Test]
    public async Task Native_store_round_trip()
    {
        if (Environment.GetEnvironmentVariable("PGNIMBUS_TEST_CREDENTIAL_STORE") != "1")
            Skip.Test("Set PGNIMBUS_TEST_CREDENTIAL_STORE=1 with an isolated OS credential store.");
        ICredentialStore native = OperatingSystem.IsMacOS() ? new MacKeychainCredentialStore()
            : OperatingSystem.IsLinux() ? new LinuxSecretServiceCredentialStore()
            : OperatingSystem.IsWindows() ? new WindowsDpapiCredentialStore(Path.Combine(Path.GetTempPath(), "pgnimbus-native-credential-tests"))
            : throw new PlatformNotSupportedException();
        var id = Guid.NewGuid();
        try
        {
            await Assert.That(native.LoadPassword(id)).IsNull();
            native.SavePassword(id, "пароль 🔑\n'quotes'");
            await Assert.That(native.LoadPassword(id)).IsEqualTo("пароль 🔑\n'quotes'");
            native.SavePassword(id, "updated");
            await Assert.That(native.LoadPassword(id)).IsEqualTo("updated");
            native.SavePassword(id, "");
            await Assert.That(native.LoadPassword(id)).IsEqualTo("");
            native.DeletePassword(id);
            await Assert.That(native.LoadPassword(id)).IsNull();
            native.DeletePassword(id);
        }
        finally { native.DeletePassword(id); }
    }
}
