using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace PgNimbus.Core.Connections;

/// <summary>What a <c>known_hosts</c> file says about one host's key.</summary>
public enum KnownHostsResult
{
    /// <summary>No entry names this host with a key of this type.</summary>
    Unknown,

    /// <summary>An entry names this host with exactly this key.</summary>
    Match,

    /// <summary>
    /// An entry names this host with a <em>different</em> key of the same
    /// type: what <c>ssh</c> reports as "REMOTE HOST IDENTIFICATION HAS
    /// CHANGED".
    /// </summary>
    Mismatch,

    /// <summary>An <c>@revoked</c> entry names exactly this key.</summary>
    Revoked,
}

/// <summary>One line of a <c>known_hosts</c> file that carries a key.</summary>
/// <param name="Marker"><c>@revoked</c> or <c>@cert-authority</c>, or null for an ordinary entry.</param>
/// <param name="HostField">The comma-separated host patterns, or a <c>|1|salt|hash</c> hashed host.</param>
/// <param name="KeyType">The key's wire name: <c>ssh-ed25519</c>, <c>ssh-rsa</c>, <c>ecdsa-sha2-nistp256</c>, …</param>
/// <param name="Key">The public key blob (what the file holds in base64).</param>
/// <param name="LineNumber">1-based, for the message that tells the user which line to remove.</param>
public sealed record KnownHostEntry(string? Marker, string HostField, string KeyType, byte[] Key, int LineNumber)
{
    public const string RevokedMarker = "@revoked";
    public const string CertAuthorityMarker = "@cert-authority";

    public bool IsHashed => HostField.StartsWith(KnownHosts.HashPrefix, StringComparison.Ordinal);

    /// <summary>The key's <c>SHA256:…</c> fingerprint, as <c>ssh</c> prints it.</summary>
    public string Fingerprint => KnownHosts.Fingerprint(Key);

    /// <summary>Whether this line names <paramref name="host"/>:<paramref name="port"/>, the way OpenSSH decides it.</summary>
    public bool MatchesHost(string host, int port) => KnownHosts.HostFieldMatches(HostField, host, port);
}

/// <summary>What a lookup found: the verdict, and the line it rests on (for a match, a mismatch or a revocation).</summary>
public sealed record KnownHostsAnswer(KnownHostsResult Result, KnownHostEntry? Entry, string? Path)
{
    public static readonly KnownHostsAnswer Unknown = new(KnownHostsResult.Unknown, null, null);
}

/// <summary>
/// Reads and writes OpenSSH's <c>known_hosts</c> format, so the tunnel can
/// honour the <c>~/.ssh/known_hosts</c> a user already has and keep its own
/// list in the same shape (<c>ssh-keygen -F</c>/<c>-R</c> work on it).
/// <para>
/// What it understands, per <c>sshd(8)</c>'s SSH_KNOWN_HOSTS FILE FORMAT:
/// comment and blank lines; the <c>@revoked</c> and <c>@cert-authority</c>
/// markers; comma-separated host patterns with <c>*</c>, <c>?</c> and a
/// leading <c>!</c> for negation; the <c>[host]:port</c> form a non-22 port is
/// written in; hashed hosts (<c>|1|salt|hash</c>, HMAC-SHA1 of the host token
/// keyed by the salt); then the key type, the base64 key and an optional
/// comment. A line it cannot read is skipped, never thrown on: the file is the
/// user's, and one odd line must not turn every connection away.
/// </para>
/// <para>
/// Not understood, on purpose: host certificates (a <c>@cert-authority</c>
/// line is skipped rather than consulted), the ancient RSA1 layout, and the
/// <c>%d</c>-style tokens of <c>ssh_config</c>. A certificate-bearing host key
/// is therefore treated like any unknown key.
/// </para>
/// Core-pure and unit-tested (<c>KnownHostsTests</c>).
/// </summary>
public static class KnownHosts
{
    public const string HashPrefix = "|1|";

    /// <summary>The port an entry names by the bare host alone.</summary>
    public const int DefaultPort = 22;

    /// <summary>
    /// The host as a <c>known_hosts</c> line spells it: <c>host</c> on port 22,
    /// <c>[host]:port</c> on any other, lower-cased the way <c>ssh</c> folds
    /// host names before it looks them up or hashes them.
    /// </summary>
    public static string HostToken(string host, int port)
    {
        var folded = host.Trim().ToLowerInvariant();
        return port == DefaultPort ? folded : $"[{folded}]:{port}";
    }

    /// <summary>The <c>SHA256:</c> fingerprint <c>ssh</c> prints: unpadded base64 of the SHA-256 of the key blob.</summary>
    public static string Fingerprint(ReadOnlySpan<byte> key) =>
        "SHA256:" + Convert.ToBase64String(SHA256.HashData(key)).TrimEnd('=');

    /// <summary>
    /// The key type a public key blob carries in its first field
    /// (<c>ssh-rsa</c>, <c>ssh-ed25519</c>, …), or null when the blob does not
    /// start with one. This, not the negotiated signature algorithm, is what a
    /// <c>known_hosts</c> line stores: SSH.NET names an RSA host key by the
    /// algorithm it signed with (<c>rsa-sha2-512</c>), while the file says
    /// <c>ssh-rsa</c>, and comparing those two strings would report every RSA
    /// host as unknown.
    /// </summary>
    public static string? KeyTypeOf(ReadOnlySpan<byte> key)
    {
        if (key.Length < 4)
        {
            return null;
        }

        var length = BinaryPrimitives.ReadUInt32BigEndian(key);
        if (length == 0 || length > 64 || key.Length < 4 + length)
        {
            return null;
        }

        var name = key.Slice(4, (int)length);
        foreach (var b in name)
        {
            // A type name is printable ASCII with no whitespace; anything else
            // is a blob that merely starts with a small number.
            if (b <= ' ' || b > '~')
            {
                return null;
            }
        }

        return Encoding.ASCII.GetString(name);
    }

    /// <summary>Reads one line; null for a comment, a blank line, or a line that is not a key entry.</summary>
    public static KnownHostEntry? ParseLine(string line, int lineNumber)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0 || trimmed[0] == '#')
        {
            return null;
        }

        var fields = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var index = 0;
        string? marker = null;
        if (fields[0].StartsWith('@'))
        {
            marker = fields[0];
            if (marker is not (KnownHostEntry.RevokedMarker or KnownHostEntry.CertAuthorityMarker))
            {
                return null;
            }

            index = 1;
        }

        // hosts, key type, key; a comment may follow and is ignored.
        if (fields.Length < index + 3)
        {
            return null;
        }

        var hostField = fields[index];
        var keyType = fields[index + 1];
        byte[] key;
        try
        {
            key = Convert.FromBase64String(fields[index + 2]);
        }
        catch (FormatException)
        {
            return null;
        }

        // The blob names its own type; a line whose two disagree is not one
        // ssh-keyscan wrote, and the blob is the half a lookup compares.
        if (key.Length == 0 || KeyTypeOf(key) is not { } blobType || blobType != keyType)
        {
            return null;
        }

        return new KnownHostEntry(marker, hostField, keyType, key, lineNumber);
    }

    /// <summary>Every key entry in <paramref name="lines"/>, in file order.</summary>
    public static IReadOnlyList<KnownHostEntry> Parse(IEnumerable<string> lines)
    {
        var entries = new List<KnownHostEntry>();
        var number = 0;
        foreach (var line in lines)
        {
            number++;
            if (ParseLine(line, number) is { } entry)
            {
                entries.Add(entry);
            }
        }

        return entries;
    }

    /// <summary>
    /// What <paramref name="entries"/> say about <paramref name="key"/> for
    /// <paramref name="host"/>:<paramref name="port"/>, with OpenSSH's
    /// precedence: a revocation wins over everything, a match over a mismatch,
    /// and a host known only with keys of other types is unknown (that is what
    /// <c>ssh</c> does too: it warns that the host is known by a different key
    /// type and treats the new one as new).
    /// </summary>
    public static KnownHostsAnswer Lookup(IEnumerable<KnownHostEntry> entries, string host, int port, string keyType, byte[] key, string? path = null)
    {
        KnownHostEntry? mismatch = null;
        KnownHostEntry? match = null;

        foreach (var entry in entries)
        {
            if (!entry.MatchesHost(host, port))
            {
                continue;
            }

            if (entry.Marker == KnownHostEntry.RevokedMarker)
            {
                if (entry.Key.AsSpan().SequenceEqual(key))
                {
                    return new KnownHostsAnswer(KnownHostsResult.Revoked, entry, path);
                }

                continue;
            }

            if (entry.Marker is not null || entry.KeyType != keyType)
            {
                continue;
            }

            if (entry.Key.AsSpan().SequenceEqual(key))
            {
                match ??= entry;
            }
            else
            {
                mismatch ??= entry;
            }
        }

        if (match is not null)
        {
            return new KnownHostsAnswer(KnownHostsResult.Match, match, path);
        }

        return mismatch is not null
            ? new KnownHostsAnswer(KnownHostsResult.Mismatch, mismatch, path)
            : KnownHostsAnswer.Unknown;
    }

    /// <summary>
    /// <see cref="Lookup"/> over the file at <paramref name="path"/>. A file
    /// that does not exist or cannot be read answers <see cref="KnownHostsResult.Unknown"/>:
    /// the user's own <c>~/.ssh/known_hosts</c> is consulted, never required.
    /// </summary>
    public static KnownHostsAnswer LookupFile(string? path, string host, int port, string keyType, byte[] key)
    {
        if (string.IsNullOrEmpty(path))
        {
            return KnownHostsAnswer.Unknown;
        }

        string[] lines;
        try
        {
            if (!File.Exists(path))
            {
                return KnownHostsAnswer.Unknown;
            }

            lines = File.ReadAllLines(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return KnownHostsAnswer.Unknown;
        }

        return Lookup(Parse(lines), host, port, keyType, key, path);
    }

    /// <summary>The line <c>ssh</c> would write for this host and key: unhashed, <c>[host]:port</c> for a non-22 port.</summary>
    public static string FormatEntry(string host, int port, string keyType, byte[] key) =>
        $"{HostToken(host, port)} {keyType} {Convert.ToBase64String(key)}";

    /// <summary>
    /// Appends an entry for this host and key to the file at <paramref name="path"/>,
    /// creating the directory and the file as needed. Written the way <c>ssh</c>
    /// writes a newly accepted key, so <c>ssh-keygen -R host -f path</c> removes
    /// it again.
    /// </summary>
    public static void Append(string path, string host, int port, string keyType, byte[] key)
    {
        if (Path.GetDirectoryName(path) is { Length: > 0 } directory)
        {
            Directory.CreateDirectory(directory);
        }

        // A file whose last line has no newline would otherwise get the new
        // entry glued onto it, which corrupts both lines.
        var prefix = string.Empty;
        if (File.Exists(path))
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (stream.Length > 0)
            {
                stream.Seek(-1, SeekOrigin.End);
                if (stream.ReadByte() != '\n')
                {
                    prefix = "\n";
                }
            }
        }

        File.AppendAllText(path, prefix + FormatEntry(host, port, keyType, key) + "\n");
    }

    /// <summary>Whether a line's host field names <paramref name="host"/>:<paramref name="port"/>.</summary>
    public static bool HostFieldMatches(string hostField, string host, int port)
    {
        var token = HostToken(host, port);

        if (hostField.StartsWith(HashPrefix, StringComparison.Ordinal))
        {
            return HashedHostMatches(hostField, token);
        }

        // OpenSSH: a negated pattern that matches vetoes the line; otherwise
        // any positive pattern that matches accepts it.
        var matched = false;
        foreach (var raw in hostField.Split(','))
        {
            var pattern = raw.Trim();
            if (pattern.Length == 0)
            {
                continue;
            }

            var negated = pattern[0] == '!';
            if (negated)
            {
                pattern = pattern[1..];
            }

            if (!PatternMatches(pattern, token) && !(port == DefaultPort && PatternMatches(pattern, $"[{token}]:{DefaultPort}")))
            {
                continue;
            }

            if (negated)
            {
                return false;
            }

            matched = true;
        }

        return matched;
    }

    /// <summary>
    /// <c>|1|base64(salt)|base64(HMAC-SHA1(key: salt, data: host token))</c>,
    /// the form <c>ssh-keygen -H</c> and <c>HashKnownHosts yes</c> write.
    /// </summary>
    public static bool HashedHostMatches(string hashedField, string token)
    {
        var parts = hashedField.Split('|');
        if (parts.Length != 4 || parts[1] != "1")
        {
            return false;
        }

        byte[] salt;
        byte[] expected;
        try
        {
            salt = Convert.FromBase64String(parts[2]);
            expected = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException)
        {
            return false;
        }

        var actual = HMACSHA1.HashData(salt, Encoding.UTF8.GetBytes(token));
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>Writes the hashed form of a host token, for tests and for anyone who wants their entries hashed.</summary>
    public static string HashHost(string token, byte[] salt) =>
        $"{HashPrefix}{Convert.ToBase64String(salt)}|{Convert.ToBase64String(HMACSHA1.HashData(salt, Encoding.UTF8.GetBytes(token)))}";

    /// <summary>OpenSSH's host pattern: <c>*</c> any run, <c>?</c> one character, everything else literal, case-insensitive.</summary>
    public static bool PatternMatches(string pattern, string text)
    {
        // Iterative glob with one backtrack point, the classic shape: a '*'
        // records where it was and where in the text it started matching, and
        // a later miss retries from one character further on.
        int p = 0, t = 0, starP = -1, starT = -1;
        while (t < text.Length)
        {
            if (p < pattern.Length && pattern[p] == '*')
            {
                starP = p++;
                starT = t;
            }
            else if (p < pattern.Length && (pattern[p] == '?' || char.ToLowerInvariant(pattern[p]) == char.ToLowerInvariant(text[t])))
            {
                p++;
                t++;
            }
            else if (starP >= 0)
            {
                p = starP + 1;
                t = ++starT;
            }
            else
            {
                return false;
            }
        }

        while (p < pattern.Length && pattern[p] == '*')
        {
            p++;
        }

        return p == pattern.Length;
    }
}
