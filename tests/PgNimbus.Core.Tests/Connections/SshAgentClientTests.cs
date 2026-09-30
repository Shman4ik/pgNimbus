using System.Buffers.Binary;
using System.Text;
using PgNimbus.Core.Connections;

namespace PgNimbus.Core.Tests.Connections;

public class SshAgentClientTests
{
    /// <summary>One request/reply exchange: records what was written, replies with a canned frame.</summary>
    private sealed class FakeAgentStream(byte[] reply) : Stream
    {
        private readonly MemoryStream _reply = new(reply);
        public MemoryStream Written { get; } = new();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => _reply.Read(buffer, offset, count);
        public override void Write(byte[] buffer, int offset, int count) => Written.Write(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private static byte[] U32(uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return bytes;
    }

    private static byte[] Str(byte[] value) => [.. U32((uint)value.Length), .. value];

    private static byte[] Str(string value) => Str(Encoding.UTF8.GetBytes(value));

    private static byte[] Frame(params byte[][] parts)
    {
        byte[] body = [.. parts.SelectMany(p => p)];
        return [.. U32((uint)body.Length), .. body];
    }

    private static byte[] KeyBlob(string type) => [.. Str(type), .. Str([1, 2, 3])];

    private static (SshAgentClient Client, FakeAgentStream Stream) Agent(byte[] reply)
    {
        var stream = new FakeAgentStream(reply);
        return (new SshAgentClient(() => stream, "test"), stream);
    }

    [Test]
    public async Task Lists_identities_with_their_key_type_and_comment()
    {
        var ed = KeyBlob("ssh-ed25519");
        var rsa = KeyBlob("ssh-rsa");
        var (client, stream) = Agent(Frame([12], U32(2), Str(ed), Str("me@laptop"), Str(rsa), Str("работа")));

        var identities = client.ListIdentities();

        await Assert.That(stream.Written.ToArray()).IsEquivalentTo(Frame([11]));
        await Assert.That(identities.Count).IsEqualTo(2);
        await Assert.That(identities[0].KeyType).IsEqualTo("ssh-ed25519");
        await Assert.That(identities[0].PublicKeyBlob).IsEquivalentTo(ed);
        await Assert.That(identities[0].Comment).IsEqualTo("me@laptop");
        await Assert.That(identities[1].KeyType).IsEqualTo("ssh-rsa");
        await Assert.That(identities[1].Comment).IsEqualTo("работа");
    }

    [Test]
    public async Task Sign_sends_key_data_and_flags_and_returns_the_signature_blob()
    {
        var blob = KeyBlob("ssh-rsa");
        byte[] signature = [.. Str("rsa-sha2-512"), .. Str([9, 9, 9])];
        var (client, stream) = Agent(Frame([14], Str(signature)));

        var result = client.Sign(blob, [7, 7], SshAgentClient.RsaSha2_512);

        await Assert.That(stream.Written.ToArray()).IsEquivalentTo(Frame([13], Str(blob), Str([7, 7]), U32(4)));
        await Assert.That(result).IsEquivalentTo(signature);
    }

    [Test]
    public async Task An_agent_failure_reply_is_an_agent_exception()
    {
        var (client, _) = Agent(Frame([5]));

        await Assert.That(() => client.Sign(KeyBlob("ssh-ed25519"), [1], 0)).Throws<SshAgentException>();
    }

    [Test]
    public async Task A_truncated_reply_is_an_agent_exception_not_an_index_error()
    {
        // Claims two identities, carries half of one.
        var (client, _) = Agent(Frame([12], U32(2), U32(40), [1, 2]));

        await Assert.That(() => client.ListIdentities()).Throws<SshAgentException>();
    }

    [Test]
    public async Task A_closed_connection_mid_reply_is_an_agent_exception()
    {
        var (client, _) = Agent([0, 0, 0, 9, 12]);

        await Assert.That(() => client.ListIdentities()).Throws<SshAgentException>();
    }

    [Test]
    public async Task An_agent_that_cannot_be_reached_says_where_it_looked()
    {
        var client = new SshAgentClient(() => throw new TimeoutException(), @"\\.\pipe\openssh-ssh-agent");

        var error = await Assert.That(() => client.ListIdentities()).Throws<SshAgentException>();
        await Assert.That(error!.Message).Contains(@"\\.\pipe\openssh-ssh-agent");
    }

    [Test]
    public async Task Rsa_keys_are_offered_as_sha2_only_and_other_types_under_their_own_name()
    {
        await Assert.That(SshAgentKeySource.SignatureAlgorithms("ssh-rsa").Select(a => a.Name))
            .IsEquivalentTo(["rsa-sha2-512", "rsa-sha2-256"]);
        await Assert.That(SshAgentKeySource.SignatureAlgorithms("ssh-rsa").Select(a => a.Flags))
            .IsEquivalentTo([SshAgentClient.RsaSha2_512, SshAgentClient.RsaSha2_256]);
        await Assert.That(SshAgentKeySource.SignatureAlgorithms("ssh-ed25519"))
            .IsEquivalentTo([("ssh-ed25519", 0u)]);
    }

    [Test]
    public async Task Key_source_signs_through_the_agent_with_the_algorithms_flag()
    {
        var blob = KeyBlob("ssh-rsa");
        var (client, stream) = Agent(Frame([14], Str([4, 2])));
        var source = new SshAgentKeySource(client, [new SshAgentIdentity(blob, "k")]);

        var sha256 = source.HostKeyAlgorithms.Single(a => a.Name == "rsa-sha2-256");
        await Assert.That(sha256.Data).IsEquivalentTo(blob);
        await Assert.That(sha256.Sign([1])).IsEquivalentTo(new byte[] { 4, 2 });
        await Assert.That(stream.Written.ToArray()).IsEquivalentTo(Frame([13], Str(blob), Str([1]), U32(2)));
    }

    [Test]
    public async Task Key_paths_expand_the_home_directory()
    {
        var home = Path.Combine(Path.GetTempPath(), "home");

        await Assert.That(SshTunnel.ResolveKeyPath("~/.ssh/work", home, _ => false))
            .IsEqualTo(Path.GetFullPath(Path.Combine(home, ".ssh", "work")));
        await Assert.That(SshTunnel.ResolveKeyPath("  \"/keys/id\"  ", home, _ => false)).IsEqualTo("/keys/id");
    }

    [Test]
    public async Task A_blank_key_path_means_the_first_default_key_that_exists()
    {
        var home = Path.Combine(Path.GetTempPath(), "home");
        var ecdsa = Path.Combine(home, ".ssh", "id_ecdsa");
        var rsa = Path.Combine(home, ".ssh", "id_rsa");

        await Assert.That(SshTunnel.ResolveKeyPath("", home, p => p == ecdsa || p == rsa)).IsEqualTo(ecdsa);
        await Assert.That(SshTunnel.ResolveKeyPath(null, home, _ => false)).IsNull();
    }
}
