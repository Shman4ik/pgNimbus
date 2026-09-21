namespace PgNimbus.Core.Connections;

/// <summary>Picks the right <see cref="ICredentialStore"/> backend for the current OS.</summary>
public static class CredentialStore
{
    private static readonly Lazy<ICredentialStore> Instance = new(() => new RecoverableCredentialStore(
        OperatingSystem.IsWindows() ? new WindowsDpapiCredentialStore()
        : OperatingSystem.IsMacOS() ? new MacKeychainCredentialStore()
        : new LinuxSecretServiceCredentialStore(),
        OperatingSystem.IsWindows() ? null : Path.Combine(AppDataPaths.GetRootDirectory(), "credentials")));

    // Keep session-only passwords when the user reopens the connection dialog.
    public static ICredentialStore Create() => Instance.Value;
}
