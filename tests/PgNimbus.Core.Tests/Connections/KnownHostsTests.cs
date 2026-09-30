using System.Text;
using PgNimbus.Core.Connections;

namespace PgNimbus.Core.Tests.Connections;

/// <summary>
/// The <c>known_hosts</c> reader against entries OpenSSH itself wrote. The
/// key is a real one (<c>ssh-keygen -t ed25519</c>, OpenSSH 10.3), and the
/// hashed lines are what <c>ssh-keygen -H</c> produced for it, so a match
/// here means the HMAC and the host token are spelled the way OpenSSH spells
/// them, not just consistently with ourselves.
/// </summary>
public class KnownHostsTests
{
    // ssh-keygen -t ed25519 -f k -N "" -C test; ssh-keygen -l -f k.pub:
    // 256 SHA256:N1SA3QGBX6UpjLt0OmRDWW3oayFMz/ZtKDZsysTgEag test (ED25519)
    private const string KeyBase64 = "AAAAC3NzaC1lZDI1NTE5AAAAIFwcGraO/1L7NHdN9EynwPiJzV1B2bne9G9VZ0KVTdiv";
    private const string KeyFingerprint = "SHA256:N1SA3QGBX6UpjLt0OmRDWW3oayFMz/ZtKDZsysTgEag";
    private const string KeyType = "ssh-ed25519";

    // ssh-keygen -H over "example.com <key>" and "[example.com]:2222 <key>".
    private const string HashedExampleCom = "|1|wATuDWkqiLCEocO5Noawqpc6O4c=|zU6wCEsVX+ImVxqi5wsvDVwc+tA=";
    private const string HashedExampleCom2222 = "|1|duzsN0F9qdkyMzKgaNkgUVbqK/w=|URzLCDXsK2eXuiOwmZiTKimEXuA=";

    // A second, unrelated ed25519 key (an OpenSSH container's host key) for "same type, different key".
    private const string OtherKeyBase64 = "AAAAC3NzaC1lZDI1NTE5AAAAIEJjfOLjEukuhd7KxihNMG+O26mr+Od2vKPDEsGQ3WZn";

    private static readonly byte[] Key = Convert.FromBase64String(KeyBase64);
    private static readonly byte[] OtherKey = Convert.FromBase64String(OtherKeyBase64);

    private static KnownHostsAnswer Look(string file, string host, int port, byte[]? key = null) =>
        KnownHosts.Lookup(KnownHosts.Parse(file.Split('\n')), host, port, KeyType, key ?? Key, "known_hosts");

    [Test]
    public async Task The_fingerprint_is_what_ssh_keygen_prints()
    {
        await Assert.That(KnownHosts.Fingerprint(Key)).IsEqualTo(KeyFingerprint);
        await Assert.That(KnownHosts.KeyTypeOf(Key)).IsEqualTo("ssh-ed25519");
    }

    [Test]
    public async Task A_plain_entry_matches_its_host_on_the_default_port()
    {
        var answer = Look($"example.com {KeyType} {KeyBase64}", "example.com", 22);

        await Assert.That(answer.Result).IsEqualTo(KnownHostsResult.Match);
        await Assert.That(answer.Entry!.LineNumber).IsEqualTo(1);
        await Assert.That(answer.Path).IsEqualTo("known_hosts");
    }

    /// <summary>OpenSSH folds host names to lower case before it looks them up.</summary>
    [Test]
    public async Task Host_names_match_regardless_of_case()
    {
        await Assert.That(Look($"Example.COM {KeyType} {KeyBase64}", "example.com", 22).Result).IsEqualTo(KnownHostsResult.Match);
        await Assert.That(Look($"example.com {KeyType} {KeyBase64}", "EXAMPLE.com", 22).Result).IsEqualTo(KnownHostsResult.Match);
    }

    /// <summary>
    /// A non-22 port is its own host token: the key on <c>[host]:2222</c> says
    /// nothing about port 22, and the bare host says nothing about 2222.
    /// </summary>
    [Test]
    public async Task A_bracketed_port_entry_and_a_bare_host_entry_are_different_hosts()
    {
        var file = $"[example.com]:2222 {KeyType} {KeyBase64}";

        await Assert.That(Look(file, "example.com", 2222).Result).IsEqualTo(KnownHostsResult.Match);
        await Assert.That(Look(file, "example.com", 22).Result).IsEqualTo(KnownHostsResult.Unknown);

        var bare = $"example.com {KeyType} {KeyBase64}";
        await Assert.That(Look(bare, "example.com", 2222).Result).IsEqualTo(KnownHostsResult.Unknown);
        // "[host]:22" spelled out is still port 22.
        await Assert.That(Look($"[example.com]:22 {KeyType} {KeyBase64}", "example.com", 22).Result).IsEqualTo(KnownHostsResult.Match);
    }

    [Test]
    public async Task Hashed_entries_written_by_ssh_keygen_H_match()
    {
        var file = $"{HashedExampleCom} {KeyType} {KeyBase64}\n{HashedExampleCom2222} {KeyType} {KeyBase64}";

        var plain = Look(file, "example.com", 22);
        await Assert.That(plain.Result).IsEqualTo(KnownHostsResult.Match);
        await Assert.That(plain.Entry!.LineNumber).IsEqualTo(1);
        await Assert.That(plain.Entry.IsHashed).IsTrue();

        var port = Look(file, "example.com", 2222);
        await Assert.That(port.Result).IsEqualTo(KnownHostsResult.Match);
        await Assert.That(port.Entry!.LineNumber).IsEqualTo(2);

        await Assert.That(Look(file, "other.example.com", 22).Result).IsEqualTo(KnownHostsResult.Unknown);
    }

    /// <summary>The hash we write is the one OpenSSH wrote, salt for salt.</summary>
    [Test]
    public async Task Our_hashing_reproduces_ssh_keygen_H_byte_for_byte()
    {
        var salt = Convert.FromBase64String(HashedExampleCom.Split('|')[2]);
        await Assert.That(KnownHosts.HashHost("example.com", salt)).IsEqualTo(HashedExampleCom);

        var salt2222 = Convert.FromBase64String(HashedExampleCom2222.Split('|')[2]);
        await Assert.That(KnownHosts.HashHost("[example.com]:2222", salt2222)).IsEqualTo(HashedExampleCom2222);
    }

    [Test]
    public async Task Comma_separated_patterns_and_wildcards_match()
    {
        await Assert.That(Look($"bastion,10.0.0.5,*.example.com {KeyType} {KeyBase64}", "db.example.com", 22).Result).IsEqualTo(KnownHostsResult.Match);
        await Assert.That(Look($"bastion,10.0.0.5,*.example.com {KeyType} {KeyBase64}", "10.0.0.5", 22).Result).IsEqualTo(KnownHostsResult.Match);
        await Assert.That(Look($"*.example.com {KeyType} {KeyBase64}", "example.com", 22).Result).IsEqualTo(KnownHostsResult.Unknown);
        await Assert.That(Look($"host?.example.com {KeyType} {KeyBase64}", "host1.example.com", 22).Result).IsEqualTo(KnownHostsResult.Match);
        await Assert.That(Look($"host?.example.com {KeyType} {KeyBase64}", "host12.example.com", 22).Result).IsEqualTo(KnownHostsResult.Unknown);
        // A wildcard covers the [host]:port form too, as ssh's does.
        await Assert.That(Look($"[*.example.com]:2222 {KeyType} {KeyBase64}", "a.example.com", 2222).Result).IsEqualTo(KnownHostsResult.Match);
    }

    [Test]
    public async Task A_negated_pattern_vetoes_the_line()
    {
        var file = $"*.example.com,!evil.example.com {KeyType} {KeyBase64}";

        await Assert.That(Look(file, "good.example.com", 22).Result).IsEqualTo(KnownHostsResult.Match);
        await Assert.That(Look(file, "evil.example.com", 22).Result).IsEqualTo(KnownHostsResult.Unknown);
    }

    [Test]
    public async Task A_different_key_of_the_same_type_is_a_mismatch_that_names_the_stored_key()
    {
        var answer = Look($"# comment\nexample.com {KeyType} {OtherKeyBase64}", "example.com", 22);

        await Assert.That(answer.Result).IsEqualTo(KnownHostsResult.Mismatch);
        await Assert.That(answer.Entry!.LineNumber).IsEqualTo(2);
        await Assert.That(answer.Entry.Fingerprint).IsEqualTo(KnownHosts.Fingerprint(OtherKey));
    }

    /// <summary>
    /// ssh: a host known only by another key type is a new key ("host key
    /// found but of a different type"), not a changed one.
    /// </summary>
    [Test]
    public async Task A_key_of_another_type_for_the_same_host_leaves_this_one_unknown()
    {
        // A real ecdsa-sha2-nistp256 blob (an OpenSSH container's host key).
        const string ecdsa = "AAAAE2VjZHNhLXNoYTItbmlzdHAyNTYAAAAIbmlzdHAyNTYAAABBBJJXtcW8Yr1UkHue1pmFMZap1/p1s7Z9RZ6RAeaHJx3/sdZyUt3HQ1S2xqVmBVMb6Vaqrttod3/zb3TCNupCp78=";

        await Assert.That(Look($"example.com ecdsa-sha2-nistp256 {ecdsa}", "example.com", 22).Result).IsEqualTo(KnownHostsResult.Unknown);
    }

    [Test]
    public async Task A_match_anywhere_beats_a_mismatch_elsewhere_and_a_revocation_beats_both()
    {
        var mismatchThenMatch = $"example.com {KeyType} {OtherKeyBase64}\nexample.com {KeyType} {KeyBase64}";
        await Assert.That(Look(mismatchThenMatch, "example.com", 22).Result).IsEqualTo(KnownHostsResult.Match);

        var matchThenRevoked = $"example.com {KeyType} {KeyBase64}\n@revoked example.com {KeyType} {KeyBase64}";
        var revoked = Look(matchThenRevoked, "example.com", 22);
        await Assert.That(revoked.Result).IsEqualTo(KnownHostsResult.Revoked);
        await Assert.That(revoked.Entry!.LineNumber).IsEqualTo(2);
    }

    /// <summary>A revocation of some other key says nothing about this one.</summary>
    [Test]
    public async Task A_revoked_line_for_another_key_is_ignored()
    {
        await Assert.That(Look($"@revoked example.com {KeyType} {OtherKeyBase64}", "example.com", 22).Result).IsEqualTo(KnownHostsResult.Unknown);
    }

    /// <summary>Certificates are not understood, so a CA line neither matches nor mismatches.</summary>
    [Test]
    public async Task A_cert_authority_line_is_skipped()
    {
        await Assert.That(Look($"@cert-authority *.example.com {KeyType} {KeyBase64}", "a.example.com", 22).Result).IsEqualTo(KnownHostsResult.Unknown);
    }

    [Test]
    public async Task Comments_blank_lines_and_garbage_are_skipped_and_never_throw()
    {
        var file = string.Join('\n',
            "# a comment",
            "",
            "   ",
            "garbage",
            "example.com ssh-ed25519 not-base64!!",
            "example.com ssh-ed25519",
            "@unknown-marker example.com ssh-ed25519 " + KeyBase64,
            "example.com ssh-rsa " + KeyBase64, // stated type disagrees with the blob
            "|1|broken",
            "|1|notbase64|alsonot ssh-ed25519 " + KeyBase64,
            $"example.com {KeyType} {KeyBase64} a trailing comment");

        // The broken hashed line is a well-formed entry whose host can match nothing.
        var entries = KnownHosts.Parse(file.Split('\n'));
        await Assert.That(entries.Count).IsEqualTo(2);
        await Assert.That(entries[0].LineNumber).IsEqualTo(10);
        await Assert.That(entries[0].MatchesHost("example.com", 22)).IsFalse();
        await Assert.That(entries[1].LineNumber).IsEqualTo(11);
        await Assert.That(Look(file, "example.com", 22).Result).IsEqualTo(KnownHostsResult.Match);
    }

    [Test]
    public async Task Any_text_at_all_parses_without_throwing()
    {
        var random = new Random(42);
        for (var i = 0; i < 500; i++)
        {
            var line = new string(Enumerable.Range(0, random.Next(0, 80)).Select(_ => (char)random.Next(' ', '~' + 1)).ToArray());
            _ = KnownHosts.ParseLine(line, 1);
            _ = KnownHosts.HostFieldMatches(line, "example.com", 22);
            _ = KnownHosts.PatternMatches(line, "example.com");
        }

        await Assert.That(KnownHosts.ParseLine("|1|", 1)).IsNull();
        await Assert.That(KnownHosts.KeyTypeOf([0xff, 0xff, 0xff, 0xff])).IsNull();
        await Assert.That(KnownHosts.KeyTypeOf([])).IsNull();
    }

    [Test]
    public async Task A_missing_or_unreadable_file_answers_unknown()
    {
        await Assert.That(KnownHosts.LookupFile(null, "h", 22, KeyType, Key).Result).IsEqualTo(KnownHostsResult.Unknown);
        await Assert.That(KnownHosts.LookupFile(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "known_hosts"), "h", 22, KeyType, Key).Result)
            .IsEqualTo(KnownHostsResult.Unknown);
    }

    /// <summary>What is appended is what <c>ssh</c> writes, so <c>ssh-keygen -F</c>/<c>-R</c> understand it.</summary>
    [Test]
    public async Task Append_writes_the_form_ssh_writes_and_the_entry_reads_back()
    {
        var directory = Path.Combine(Path.GetTempPath(), "pgnimbus-known-hosts-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "sub", "known_hosts");
        try
        {
            KnownHosts.Append(path, "Bastion.Example.com", 22, KeyType, Key);
            KnownHosts.Append(path, "bastion.example.com", 2222, KeyType, OtherKey);

            var lines = File.ReadAllLines(path);
            await Assert.That(lines).IsEquivalentTo(
                [$"bastion.example.com {KeyType} {KeyBase64}", $"[bastion.example.com]:2222 {KeyType} {OtherKeyBase64}"],
                CollectionOrdering.Matching);
            await Assert.That(File.ReadAllText(path).EndsWith('\n')).IsTrue();

            await Assert.That(KnownHosts.LookupFile(path, "bastion.example.com", 22, KeyType, Key).Result).IsEqualTo(KnownHostsResult.Match);
            await Assert.That(KnownHosts.LookupFile(path, "bastion.example.com", 2222, KeyType, OtherKey).Result).IsEqualTo(KnownHostsResult.Match);
            await Assert.That(KnownHosts.LookupFile(path, "bastion.example.com", 2222, KeyType, Key).Result).IsEqualTo(KnownHostsResult.Mismatch);

            // A file whose last line lacks its newline (hand-edited) still gets a line of its own.
            File.WriteAllText(path, $"other {KeyType} {KeyBase64}");
            KnownHosts.Append(path, "again", 22, KeyType, Key);
            await Assert.That(File.ReadAllLines(path).Length).IsEqualTo(2);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task The_host_token_is_spelled_like_ssh_spells_it()
    {
        await Assert.That(KnownHosts.HostToken("Bastion", 22)).IsEqualTo("bastion");
        await Assert.That(KnownHosts.HostToken(" bastion ", 2200)).IsEqualTo("[bastion]:2200");
        await Assert.That(Encoding.ASCII.GetString(Key, 4, 11)).IsEqualTo("ssh-ed25519");
    }
}
