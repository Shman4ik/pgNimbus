using System.Buffers.Binary;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;

namespace PgNimbus.Core.Connections;

/// <summary>
/// A key the SSH agent holds: the public blob the server is offered, and the
/// comment <c>ssh-add</c> gave it. The private half never leaves the agent.
/// </summary>
public sealed record SshAgentIdentity(byte[] PublicKeyBlob, string Comment)
{
    /// <summary>The key's wire type (<c>ssh-ed25519</c>, <c>ssh-rsa</c>, a certificate type, …).</summary>
    public string KeyType => SshAgentClient.ReadKeyType(PublicKeyBlob);
}

/// <summary>The agent could not be reached, or refused or garbled a request.</summary>
public sealed class SshAgentException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// A minimal client for the SSH agent protocol (draft-miller-ssh-agent): list
/// the keys the agent holds and ask it to sign with one. That is all public-key
/// auth needs, and it is what lets a passphrase-protected key that is already
/// unlocked in the agent (<c>ssh-add</c>) be used without typing the passphrase
/// again — SSH.NET has no agent support of its own.
/// </summary>
/// <remarks>
/// One connection per request: the protocol is strictly request/reply, the
/// agent is local, and it keeps nothing open between the two calls a login
/// makes. The transport is injected so the codec is testable without an agent.
/// </remarks>
public sealed class SshAgentClient
{
    /// <summary>Sign-request flag: an RSA signature with SHA-256 (<c>rsa-sha2-256</c>).</summary>
    public const uint RsaSha2_256 = 2;

    /// <summary>Sign-request flag: an RSA signature with SHA-512 (<c>rsa-sha2-512</c>).</summary>
    public const uint RsaSha2_512 = 4;

    /// <summary>The named pipe Windows' OpenSSH Authentication Agent service listens on.</summary>
    public const string WindowsPipeName = "openssh-ssh-agent";

    private const byte AgentFailure = 5;
    private const byte RequestIdentities = 11;
    private const byte IdentitiesAnswer = 12;
    private const byte SignRequest = 13;
    private const byte SignResponse = 14;

    // OpenSSH's own agent caps a message at 256 KiB; anything longer is not a reply.
    private const int MaxMessageLength = 256 * 1024;
    private const int PipeConnectTimeoutMs = 2000;
    private const string PipePrefix = @"\\.\pipe\";

    private readonly Func<Stream> _connect;

    public SshAgentClient(Func<Stream> connect, string address)
    {
        _connect = connect;
        Address = address;
    }

    /// <summary>Where the agent is expected — a pipe or socket path, shown in errors.</summary>
    public string Address { get; }

    /// <summary>
    /// The agent this process would use, the way <c>ssh</c> finds it: on Windows
    /// the OpenSSH service's pipe unless <c>SSH_AUTH_SOCK</c> names another pipe
    /// (Pageant, 1Password and gpg4win can all serve one); elsewhere the socket
    /// <c>SSH_AUTH_SOCK</c> names. Null when there is nowhere to look.
    /// </summary>
    public static SshAgentClient? FromEnvironment()
    {
        var socket = Environment.GetEnvironmentVariable("SSH_AUTH_SOCK");

        if (OperatingSystem.IsWindows())
        {
            // A Unix-style SSH_AUTH_SOCK on Windows is Git Bash's MSYS emulation,
            // which is not a socket anything outside MSYS can open.
            var pipe = socket is not null && socket.StartsWith(PipePrefix, StringComparison.OrdinalIgnoreCase)
                ? socket[PipePrefix.Length..]
                : WindowsPipeName;
            return new SshAgentClient(() => ConnectPipe(pipe), PipePrefix + pipe);
        }

        return string.IsNullOrEmpty(socket)
            ? null
            : new SshAgentClient(() => ConnectSocket(socket), socket);
    }

    public IReadOnlyList<SshAgentIdentity> ListIdentities()
    {
        var reader = new Reader(Request([RequestIdentities]));
        var type = reader.ReadByte();
        if (type == AgentFailure)
        {
            throw new SshAgentException("The SSH agent refused to list its keys.");
        }

        if (type != IdentitiesAnswer)
        {
            throw Unexpected(type);
        }

        var count = reader.ReadUInt32();
        var identities = new List<SshAgentIdentity>();
        for (var i = 0; i < count; i++)
        {
            var blob = reader.ReadString();
            var comment = Encoding.UTF8.GetString(reader.ReadString());
            identities.Add(new SshAgentIdentity(blob, comment));
        }

        return identities;
    }

    /// <summary>
    /// Asks the agent to sign <paramref name="data"/> with the key whose public
    /// blob is given, and returns the encoded signature (algorithm name plus
    /// signature bytes) — the exact form an SSH userauth request carries.
    /// </summary>
    public byte[] Sign(byte[] publicKeyBlob, byte[] data, uint flags)
    {
        var message = new MemoryStream();
        message.WriteByte(SignRequest);
        WriteString(message, publicKeyBlob);
        WriteString(message, data);
        WriteUInt32(message, flags);

        var reader = new Reader(Request(message.ToArray()));
        var type = reader.ReadByte();
        if (type == AgentFailure)
        {
            throw new SshAgentException(
                "The SSH agent declined to sign with the key (it may have been removed, or it needs a confirmation that was refused).");
        }

        if (type != SignResponse)
        {
            throw Unexpected(type);
        }

        return reader.ReadString();
    }

    /// <summary>The first string of a public-key blob is its type; "unknown" for a blob that isn't one.</summary>
    public static string ReadKeyType(byte[] publicKeyBlob)
    {
        try
        {
            return Encoding.ASCII.GetString(new Reader(publicKeyBlob).ReadString());
        }
        catch (SshAgentException)
        {
            return "unknown";
        }
    }

    private byte[] Request(byte[] message)
    {
        Stream stream;
        try
        {
            stream = _connect();
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or SocketException or UnauthorizedAccessException)
        {
            throw new SshAgentException($"No SSH agent answered at {Address}. {StartHint}", ex);
        }

        using (stream)
        {
            try
            {
                var frame = new MemoryStream();
                WriteString(frame, message);
                stream.Write(frame.GetBuffer(), 0, (int)frame.Length);
                stream.Flush();

                Span<byte> header = stackalloc byte[4];
                stream.ReadExactly(header);
                var length = BinaryPrimitives.ReadUInt32BigEndian(header);
                if (length is 0 or > MaxMessageLength)
                {
                    throw new SshAgentException("The SSH agent sent a malformed reply.");
                }

                var body = new byte[length];
                stream.ReadExactly(body);
                return body;
            }
            catch (Exception ex) when (ex is IOException or SocketException)
            {
                throw new SshAgentException($"The SSH agent at {Address} dropped the connection: {ex.Message}", ex);
            }
        }
    }

    private static string StartHint => OperatingSystem.IsWindows()
        ? "Start the \"OpenSSH Authentication Agent\" Windows service and add your key with ssh-add."
        : "Check that SSH_AUTH_SOCK points at a running agent and add your key with ssh-add.";

    private static SshAgentException Unexpected(byte type) =>
        new($"The SSH agent sent an unexpected reply (message type {type}).");

    private static Stream ConnectPipe(string name)
    {
        var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut);
        try
        {
            pipe.Connect(PipeConnectTimeoutMs);
            return pipe;
        }
        catch
        {
            pipe.Dispose();
            throw;
        }
    }

    private static Stream ConnectSocket(string path)
    {
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            socket.Connect(new UnixDomainSocketEndPoint(path));
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static void WriteUInt32(Stream stream, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void WriteString(Stream stream, ReadOnlySpan<byte> value)
    {
        WriteUInt32(stream, (uint)value.Length);
        stream.Write(value);
    }

    /// <summary>SSH wire-format reader (RFC 4251 §5) that fails with <see cref="SshAgentException"/>, never out of range.</summary>
    private sealed class Reader(byte[] data)
    {
        private int _position;

        public byte ReadByte()
        {
            Require(1);
            return data[_position++];
        }

        public uint ReadUInt32()
        {
            Require(4);
            var value = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(_position));
            _position += 4;
            return value;
        }

        public byte[] ReadString()
        {
            var length = ReadUInt32();
            if (length > int.MaxValue)
            {
                throw Malformed();
            }

            Require((int)length);
            var value = data.AsSpan(_position, (int)length).ToArray();
            _position += (int)length;
            return value;
        }

        private void Require(int count)
        {
            if (count < 0 || data.Length - _position < count)
            {
                throw Malformed();
            }
        }

        private static SshAgentException Malformed() => new("The SSH agent sent a malformed reply.");
    }
}
