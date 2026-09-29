using PgNimbus.Core.Connections;

namespace PgNimbus.Core.Tests.Connections;

/// <summary>
/// The host-key decision the tunnel makes from what the two files say, and
/// the messages it fails with. The files are real temp files so the read,
/// the append and the "line N of path" in the message are all exercised.
/// </summary>
public class SshHostKeyVerifierTests
{
    private const string KeyBase64 = "AAAAC3NzaC1lZDI1NTE5AAAAIFwcGraO/1L7NHdN9EynwPiJzV1B2bne9G9VZ0KVTdiv";
    private const string OtherKeyBase64 = "AAAAC3NzaC1lZDI1NTE5AAAAIEJjfOLjEukuhd7KxihNMG+O26mr+Od2vKPDEsGQ3WZn";
    private static readonly byte[] Key = Convert.FromBase64String(KeyBase64);
    private static readonly byte[] OtherKey = Convert.FromBase64String(OtherKeyBase64);

    private sealed class Policy(bool answer) : ISshHostKeyPolicy
    {
        public List<SshHostKeyPrompt> Asked { get; } = [];

        public Func<SshHostKeyPrompt, bool>? Answer { get; init; }

        public bool AcceptUnknown(SshHostKeyPrompt prompt)
        {
            Asked.Add(prompt);
            return Answer?.Invoke(prompt) ?? answer;
        }
    }

    private sealed class Files : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "pgnimbus-hostkeys-" + Guid.NewGuid().ToString("N"));

        public Files()
        {
            Directory.CreateDirectory(_directory);
        }

        public string User => Path.Combine(_directory, "user_known_hosts");
        public string Own => Path.Combine(_directory, "app", "known_hosts");

        public void WriteUser(params string[] lines) => File.WriteAllLines(User, lines);

        public void WriteOwn(params string[] lines)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Own)!);
            File.WriteAllLines(Own, lines);
        }

        public void Dispose() => Directory.Delete(_directory, recursive: true);
    }

    [Test]
    [Arguments(KnownHostsResult.Match, KnownHostsResult.Unknown, HostKeyDecision.Trust)]
    [Arguments(KnownHostsResult.Unknown, KnownHostsResult.Match, HostKeyDecision.Trust)]
    [Arguments(KnownHostsResult.Match, KnownHostsResult.Mismatch, HostKeyDecision.Trust)]
    [Arguments(KnownHostsResult.Mismatch, KnownHostsResult.Match, HostKeyDecision.Trust)]
    [Arguments(KnownHostsResult.Mismatch, KnownHostsResult.Unknown, HostKeyDecision.Changed)]
    [Arguments(KnownHostsResult.Unknown, KnownHostsResult.Mismatch, HostKeyDecision.Changed)]
    [Arguments(KnownHostsResult.Revoked, KnownHostsResult.Match, HostKeyDecision.Revoked)]
    [Arguments(KnownHostsResult.Match, KnownHostsResult.Revoked, HostKeyDecision.Revoked)]
    [Arguments(KnownHostsResult.Revoked, KnownHostsResult.Mismatch, HostKeyDecision.Revoked)]
    public async Task Decide_follows_OpenSSH_precedence_without_asking(KnownHostsResult user, KnownHostsResult own, HostKeyDecision expected)
    {
        var asked = false;
        var decision = SshHostKeyVerifier.Decide(user, own, () => { asked = true; return true; });

        await Assert.That(decision).IsEqualTo(expected);
        await Assert.That(asked).IsFalse();
    }

    [Test]
    public async Task Decide_asks_only_when_neither_file_knows_the_host()
    {
        await Assert.That(SshHostKeyVerifier.Decide(KnownHostsResult.Unknown, KnownHostsResult.Unknown, () => true)).IsEqualTo(HostKeyDecision.TrustAndRemember);
        await Assert.That(SshHostKeyVerifier.Decide(KnownHostsResult.Unknown, KnownHostsResult.Unknown, () => false)).IsEqualTo(HostKeyDecision.Declined);
    }

    [Test]
    public async Task A_key_in_the_users_file_is_trusted_and_nothing_is_written()
    {
        using var files = new Files();
        files.WriteUser($"[bastion]:2222 ssh-ed25519 {KeyBase64}");
        var policy = new Policy(answer: false);
        var verifier = new SshHostKeyVerifier(policy, files.User, files.Own);

        await Assert.That(verifier.Check("bastion", 2222, "ssh-ed25519", Key)).IsNull();
        await Assert.That(policy.Asked).IsEmpty();
        await Assert.That(File.Exists(files.Own)).IsFalse();
    }

    [Test]
    public async Task An_unknown_key_is_put_to_the_policy_with_the_fingerprint_ssh_prints_and_accepted_keys_are_remembered()
    {
        using var files = new Files();
        var policy = new Policy(answer: true);
        var verifier = new SshHostKeyVerifier(policy, files.User, files.Own);

        // SSH.NET names an RSA key by its negotiated algorithm; the blob decides the type.
        await Assert.That(verifier.Check("Bastion.example.com", 22, "rsa-sha2-512", Key)).IsNull();

        var prompt = policy.Asked.Single();
        await Assert.That(prompt.Host).IsEqualTo("Bastion.example.com");
        await Assert.That(prompt.Port).IsEqualTo(22);
        await Assert.That(prompt.Server).IsEqualTo("Bastion.example.com");
        await Assert.That(prompt.KeyType).IsEqualTo("ssh-ed25519");
        await Assert.That(prompt.Fingerprint).IsEqualTo("SHA256:N1SA3QGBX6UpjLt0OmRDWW3oayFMz/ZtKDZsysTgEag");
        await Assert.That(prompt.KnownHostsPath).IsEqualTo(files.Own);

        await Assert.That(File.ReadAllLines(files.Own)).IsEquivalentTo([$"bastion.example.com ssh-ed25519 {KeyBase64}"]);

        // The second connect finds it and asks nobody.
        await Assert.That(verifier.Check("bastion.example.com", 22, "ssh-ed25519", Key)).IsNull();
        await Assert.That(policy.Asked.Count).IsEqualTo(1);
    }

    [Test]
    public async Task A_declined_key_fails_the_connect_and_writes_nothing()
    {
        using var files = new Files();
        var policy = new Policy(answer: false);
        var verifier = new SshHostKeyVerifier(policy, files.User, files.Own);

        var failure = verifier.Check("bastion", 2222, "ssh-ed25519", Key);

        await Assert.That(failure).IsNotNull();
        await Assert.That(failure!.Message).Contains("bastion:2222");
        await Assert.That(failure.Message).Contains("SHA256:N1SA3QGBX6UpjLt0OmRDWW3oayFMz/ZtKDZsysTgEag");
        await Assert.That(failure.Message).Contains("not accepted");
        await Assert.That(File.Exists(files.Own)).IsFalse();
    }

    [Test]
    public async Task A_changed_key_fails_with_both_fingerprints_and_the_line_to_remove()
    {
        using var files = new Files();
        files.WriteOwn("# pgNimbus", $"bastion ssh-ed25519 {OtherKeyBase64}");
        var policy = new Policy(answer: true);
        var verifier = new SshHostKeyVerifier(policy, files.User, files.Own);

        var failure = verifier.Check("bastion", 22, "ssh-ed25519", Key);

        await Assert.That(failure).IsNotNull();
        await Assert.That(failure!.Message).Contains("The host key of bastion has changed");
        await Assert.That(failure.Message).Contains(KnownHosts.Fingerprint(OtherKey));
        await Assert.That(failure.Message).Contains(KnownHosts.Fingerprint(Key));
        await Assert.That(failure.Message).Contains($"{files.Own} (line 2)");
        await Assert.That(failure.Message).Contains("intercepting");
        await Assert.That(failure.Message).Contains("remove that line");
        // Never asked, never overwritten: the old line is the evidence.
        await Assert.That(policy.Asked).IsEmpty();
        await Assert.That(File.ReadAllLines(files.Own).Length).IsEqualTo(2);
    }

    [Test]
    public async Task A_changed_key_in_the_users_file_is_reported_against_that_file()
    {
        using var files = new Files();
        files.WriteUser($"bastion ssh-ed25519 {OtherKeyBase64}");
        var verifier = new SshHostKeyVerifier(new Policy(answer: true), files.User, files.Own);

        var failure = verifier.Check("bastion", 22, "ssh-ed25519", Key);

        await Assert.That(failure!.Message).Contains($"{files.User} (line 1)");
    }

    [Test]
    public async Task A_revoked_key_is_refused_even_when_the_other_file_trusts_it()
    {
        using var files = new Files();
        files.WriteUser($"@revoked bastion ssh-ed25519 {KeyBase64}");
        files.WriteOwn($"bastion ssh-ed25519 {KeyBase64}");
        var verifier = new SshHostKeyVerifier(new Policy(answer: true), files.User, files.Own);

        var failure = verifier.Check("bastion", 22, "ssh-ed25519", Key);

        await Assert.That(failure).IsNotNull();
        await Assert.That(failure!.Message).Contains("@revoked");
        await Assert.That(failure.Message).Contains($"{files.User} (line 1)");
    }

    /// <summary>A prompt that blows up (a closed window, say) is a refusal that says why, not a trusted key.</summary>
    [Test]
    public async Task A_policy_that_throws_refuses_the_key_with_its_reason()
    {
        using var files = new Files();
        var policy = new Policy(answer: true) { Answer = _ => throw new InvalidOperationException("no window") };
        var verifier = new SshHostKeyVerifier(policy, files.User, files.Own);

        var failure = verifier.Check("bastion", 22, "ssh-ed25519", Key);

        await Assert.That(failure).IsNotNull();
        await Assert.That(failure!.Message).Contains("no window");
        await Assert.That(failure.InnerException).IsTypeOf<InvalidOperationException>();
        await Assert.That(File.Exists(files.Own)).IsFalse();
    }

    [Test]
    public async Task No_user_file_at_all_is_fine()
    {
        using var files = new Files();
        var verifier = new SshHostKeyVerifier(new Policy(answer: true), userKnownHostsPath: null, files.Own);

        await Assert.That(verifier.Check("bastion", 22, "ssh-ed25519", Key)).IsNull();
        await Assert.That(File.Exists(files.Own)).IsTrue();
    }

    [Test]
    public async Task With_no_file_to_write_an_accepted_key_is_trusted_for_the_session()
    {
        // No app data folder (AppDataPaths.Resolve is null): the key cannot be
        // written, and used to throw before the prompt. It is kept in memory, so
        // the next check (a reconnect, a retry after a slow prompt) trusts it.
        var policy = new Policy(answer: true);
        var verifier = new SshHostKeyVerifier(policy, userKnownHostsPath: null, ownKnownHostsPath: null);

        await Assert.That(verifier.Check("bastion", 22, "ssh-ed25519", Key)).IsNull();
        await Assert.That(verifier.Check("bastion", 22, "ssh-ed25519", Key)).IsNull();
        await Assert.That(verifier.Prompts).IsEqualTo(1);
        await Assert.That(verifier.KnownKeyTypes("bastion", 22)).Contains("ssh-ed25519");
        await Assert.That(verifier.Check("bastion", 22, "ssh-ed25519", OtherKey)).IsNotNull();
    }

    [Test]
    public async Task The_known_key_types_come_from_both_files_and_skip_revoked_and_ca_lines()
    {
        using var files = new Files();
        files.WriteUser($"@revoked bastion ssh-ed25519 {OtherKeyBase64}", $"@cert-authority bastion ssh-ed25519 {KeyBase64}", $"other ssh-ed25519 {KeyBase64}");
        files.WriteOwn($"[bastion]:2222 ssh-ed25519 {KeyBase64}");
        var verifier = new SshHostKeyVerifier(new Policy(answer: false), files.User, files.Own);

        // Revoked and CA lines say nothing about which key type the host uses.
        await Assert.That(verifier.KnownKeyTypes("bastion", 22)).IsEmpty();
        await Assert.That(verifier.KnownKeyTypes("other", 22)).IsEquivalentTo(new[] { "ssh-ed25519" });
        await Assert.That(verifier.KnownKeyTypes("bastion", 2222)).IsEquivalentTo(new[] { "ssh-ed25519" });
    }

    [Test]
    public async Task The_app_default_puts_its_own_file_under_the_app_data_root()
    {
        var verifier = SshHostKeyVerifier.ForApp(RejectUnknownHostKeys.Instance);

        await Assert.That(verifier.OwnKnownHostsPath).IsEqualTo(AppDataPaths.Resolve("known_hosts"));
        await Assert.That(verifier.UserKnownHostsPath).IsEqualTo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "known_hosts"));
        await Assert.That(RejectUnknownHostKeys.Instance.AcceptUnknown(new SshHostKeyPrompt("h", 22, "ssh-ed25519", "SHA256:x", "p"))).IsFalse();
    }
}
