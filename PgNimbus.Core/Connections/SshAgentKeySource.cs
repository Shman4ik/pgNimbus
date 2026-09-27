using Renci.SshNet;
using Renci.SshNet.Security;

namespace PgNimbus.Core.Connections;

/// <summary>
/// Plugs the agent's keys into SSH.NET's ordinary public-key auth: each key
/// becomes a <see cref="HostAlgorithm"/> whose public data is the agent's blob
/// and whose <see cref="HostAlgorithm.Sign"/> is a sign request to the agent.
/// SSH.NET only asks for a signature once the server has said it would accept
/// the key, so the agent is not asked to sign for keys the server rejects.
/// </summary>
public sealed class SshAgentKeySource : IPrivateKeySource
{
    public SshAgentKeySource(SshAgentClient agent, IEnumerable<SshAgentIdentity> identities)
    {
        HostKeyAlgorithms = identities
            .SelectMany(identity => SignatureAlgorithms(identity.KeyType)
                .Select(algorithm => new AgentHostAlgorithm(agent, identity, algorithm.Name, algorithm.Flags)))
            .ToList<HostAlgorithm>();
    }

    public IReadOnlyCollection<HostAlgorithm> HostKeyAlgorithms { get; }

    /// <summary>
    /// The signature algorithms to offer for a key type, most preferred first.
    /// An RSA key is offered as SHA-2 only: <c>ssh-rsa</c> (SHA-1) signatures are
    /// refused by OpenSSH 8.8 and later, and the agent picks the hash from the
    /// request flag. Every other type signs under its own name.
    /// </summary>
    public static IReadOnlyList<(string Name, uint Flags)> SignatureAlgorithms(string keyType) => keyType switch
    {
        "ssh-rsa" =>
        [
            ("rsa-sha2-512", SshAgentClient.RsaSha2_512),
            ("rsa-sha2-256", SshAgentClient.RsaSha2_256),
        ],
        "ssh-rsa-cert-v01@openssh.com" =>
        [
            ("rsa-sha2-512-cert-v01@openssh.com", SshAgentClient.RsaSha2_512),
            ("rsa-sha2-256-cert-v01@openssh.com", SshAgentClient.RsaSha2_256),
        ],
        _ => [(keyType, 0)],
    };

    private sealed class AgentHostAlgorithm(SshAgentClient agent, SshAgentIdentity identity, string name, uint flags)
        : HostAlgorithm(name)
    {
        public override byte[] Data => identity.PublicKeyBlob;

        public override byte[] Sign(byte[] data) => agent.Sign(identity.PublicKeyBlob, data, flags);

        // Only ever used to check a server's host-key signature, which this never is.
        public override bool VerifySignature(byte[] data, byte[] signature) =>
            throw new NotSupportedException("An agent key is only used to sign.");
    }
}
