namespace PgNimbus.Core.Connections;

/// <summary>
/// What the tunnel tells the user about a host key nobody has seen before,
/// so they can compare it with what the server's administrator gave them.
/// </summary>
/// <param name="Host">The SSH host as typed in the profile.</param>
/// <param name="Port">The SSH port.</param>
/// <param name="KeyType">The key's wire name: <c>ssh-ed25519</c>, <c>ssh-rsa</c>, …</param>
/// <param name="Fingerprint">The <c>SHA256:…</c> fingerprint, exactly as <c>ssh</c> and <c>ssh-keygen -l</c> print it.</param>
/// <param name="KnownHostsPath">The file an accepted key is written to, so the prompt can say where it will be remembered.</param>
public sealed record SshHostKeyPrompt(string Host, int Port, string KeyType, string Fingerprint, string KnownHostsPath)
{
    /// <summary><c>host</c> or <c>host:port</c> for a non-22 port, the way the messages name the server.</summary>
    public string Server => Port == KnownHosts.DefaultPort ? Host : $"{Host}:{Port}";
}

/// <summary>
/// Answers the one question the files cannot: whether to trust a host key
/// that neither <c>~/.ssh/known_hosts</c> nor pgNimbus's own list knows. The
/// App's implementation is a dialog; tests answer from a lambda. Called on the
/// thread SSH.NET runs the key exchange on, never the UI thread, and the
/// exchange waits for the answer.
/// </summary>
public interface ISshHostKeyPolicy
{
    bool AcceptUnknown(SshHostKeyPrompt prompt);
}

/// <summary>Refuses every unknown host. The safe default for anything that has no user to ask.</summary>
public sealed class RejectUnknownHostKeys : ISshHostKeyPolicy
{
    public static readonly RejectUnknownHostKeys Instance = new();

    public bool AcceptUnknown(SshHostKeyPrompt prompt) => false;
}

/// <summary>The outcome of checking a host key against both files and, if needed, the policy.</summary>
public enum HostKeyDecision
{
    /// <summary>A file already holds this key; connect.</summary>
    Trust,

    /// <summary>Nobody knew the key, the policy accepted it; connect, and write it to pgNimbus's own file.</summary>
    TrustAndRemember,

    /// <summary>A file holds a different key of the same type for this host; refuse.</summary>
    Changed,

    /// <summary>A file marks this key <c>@revoked</c>; refuse.</summary>
    Revoked,

    /// <summary>Nobody knew the key and the policy declined it; refuse.</summary>
    Declined,
}

/// <summary>
/// The host-key half of <see cref="SshTunnel.Connect"/>: consults the user's
/// <c>~/.ssh/known_hosts</c> (read, never written) and pgNimbus's own
/// <c>known_hosts</c> under the app data root (read and appended), asks the
/// <see cref="ISshHostKeyPolicy"/> about a key neither knows, and turns the
/// answer into either trust or an <see cref="SshTunnelException"/> written for
/// the connection form.
/// <para>
/// Why two files: the user's file is theirs and <c>ssh</c>'s (it may be hashed,
/// it may have <c>@revoked</c> lines from an administrator), so it is honoured
/// as read but never touched; what pgNimbus accepts goes into its own file in
/// the same format, so <c>ssh-keygen -F</c>/<c>-R</c> work on it and a changed
/// key is one deliberate line removal away from being re-accepted.
/// </para>
/// </summary>
public sealed class SshHostKeyVerifier
{
    private readonly ISshHostKeyPolicy _policy;

    /// <param name="policy">Asked about a key neither file knows.</param>
    /// <param name="userKnownHostsPath">The user's own file, consulted read-only; null to consult none.</param>
    /// <param name="ownKnownHostsPath">pgNimbus's file: consulted, and where an accepted key is written.</param>
    public SshHostKeyVerifier(ISshHostKeyPolicy policy, string? userKnownHostsPath, string ownKnownHostsPath)
    {
        _policy = policy;
        UserKnownHostsPath = userKnownHostsPath;
        OwnKnownHostsPath = ownKnownHostsPath;
    }

    public string? UserKnownHostsPath { get; }

    public string OwnKnownHostsPath { get; }

    /// <summary>The verifier the app uses: <c>~/.ssh/known_hosts</c> plus <c>known_hosts</c> under <see cref="AppDataPaths.GetRootDirectory"/>.</summary>
    public static SshHostKeyVerifier ForApp(ISshHostKeyPolicy policy) =>
        new(policy, DefaultUserKnownHostsPath(), DefaultOwnKnownHostsPath());

    public static string DefaultUserKnownHostsPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "known_hosts");

    public static string DefaultOwnKnownHostsPath() =>
        Path.Combine(AppDataPaths.GetRootDirectory(), "known_hosts");

    /// <summary>
    /// The pure decision, with OpenSSH's precedence across both files: a
    /// revocation anywhere refuses, a match anywhere trusts (a stale line in
    /// one file does not override a good one in the other), a mismatch
    /// anywhere refuses, and only when neither file has an opinion is
    /// <paramref name="ask"/> consulted.
    /// </summary>
    public static HostKeyDecision Decide(KnownHostsResult user, KnownHostsResult own, Func<bool> ask)
    {
        if (user == KnownHostsResult.Revoked || own == KnownHostsResult.Revoked)
        {
            return HostKeyDecision.Revoked;
        }

        if (user == KnownHostsResult.Match || own == KnownHostsResult.Match)
        {
            return HostKeyDecision.Trust;
        }

        if (user == KnownHostsResult.Mismatch || own == KnownHostsResult.Mismatch)
        {
            return HostKeyDecision.Changed;
        }

        return ask() ? HostKeyDecision.TrustAndRemember : HostKeyDecision.Declined;
    }

    /// <summary>
    /// Checks one received host key. Null means trust it (and, for a newly
    /// accepted key, it has been written to <see cref="OwnKnownHostsPath"/>);
    /// otherwise the exception the connect should fail with. Never throws:
    /// it runs inside SSH.NET's <c>HostKeyReceived</c> event, where an
    /// exception would surface as a bare connection failure with the reason
    /// lost.
    /// </summary>
    /// <param name="host">The SSH host as typed in the profile.</param>
    /// <param name="port">The SSH port.</param>
    /// <param name="hostKeyName">SSH.NET's name for the key (the negotiated algorithm; used only when the blob does not name its own type).</param>
    /// <param name="key">The public key blob the server presented.</param>
    public SshTunnelException? Check(string host, int port, string hostKeyName, byte[] key)
    {
        var keyType = KnownHosts.KeyTypeOf(key) ?? hostKeyName;
        var fingerprint = KnownHosts.Fingerprint(key);
        var server = port == KnownHosts.DefaultPort ? host : $"{host}:{port}";

        var user = KnownHosts.LookupFile(UserKnownHostsPath, host, port, keyType, key);
        var own = KnownHosts.LookupFile(OwnKnownHostsPath, host, port, keyType, key);

        Exception? askFailure = null;
        bool Ask()
        {
            try
            {
                return _policy.AcceptUnknown(new SshHostKeyPrompt(host, port, keyType, fingerprint, OwnKnownHostsPath));
            }
            catch (Exception ex)
            {
                askFailure = ex;
                return false;
            }
        }

        switch (Decide(user.Result, own.Result, Ask))
        {
            case HostKeyDecision.Trust:
                return null;

            case HostKeyDecision.TrustAndRemember:
                Remember(host, port, keyType, key);
                return null;

            case HostKeyDecision.Revoked:
            {
                var where = user.Result == KnownHostsResult.Revoked ? user : own;
                return new SshTunnelException(
                    $"The {keyType} key that {server} presented ({fingerprint}) is marked @revoked in {where.Path} (line {where.Entry!.LineNumber}). pgNimbus did not connect.");
            }

            case HostKeyDecision.Changed:
            {
                var where = user.Result == KnownHostsResult.Mismatch ? user : own;
                return new SshTunnelException(
                    $"The host key of {server} has changed. {where.Path} (line {where.Entry!.LineNumber}) knows its {keyType} key as {where.Entry.Fingerprint}, but the server now presents {fingerprint}. "
                    + "This happens when the server was reinstalled or its keys were regenerated, and also when someone between you and the server is intercepting the connection. pgNimbus did not connect. "
                    + $"If you know the key really changed, remove that line from {where.Path} and connect again; you will be asked to confirm the new key.");
            }

            default:
                return new SshTunnelException(
                    askFailure is null
                        ? $"The {keyType} key of {server} ({fingerprint}) was not accepted, so pgNimbus did not connect."
                        : $"The {keyType} key of {server} ({fingerprint}) could not be confirmed ({askFailure.Message}), so pgNimbus did not connect.",
                    askFailure);
        }
    }

    /// <summary>
    /// Writes an accepted key to pgNimbus's own file. A write that fails is
    /// logged and the connection still goes ahead, as <c>ssh</c> does ("Failed
    /// to add the host to the list of known hosts"): the user just confirmed
    /// this key, and refusing the connection over a full disk would teach them
    /// to click through the prompt.
    /// </summary>
    private void Remember(string host, int port, string keyType, byte[] key)
    {
        try
        {
            KnownHosts.Append(OwnKnownHostsPath, host, port, keyType, key);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Diagnostics.CrashLogger.LogCritical($"Could not write the accepted SSH host key to {OwnKnownHostsPath}", ex);
        }
    }
}
