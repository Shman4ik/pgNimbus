namespace PgNimbus.Core.Connections;

/// <summary>Picks the right <see cref="ICredentialStore"/> backend for the current OS.</summary>
public static class CredentialStore
{
    public static ICredentialStore Create() =>
        OperatingSystem.IsWindows() ? new WindowsDpapiCredentialStore() : new PlainFileCredentialStore();
}
