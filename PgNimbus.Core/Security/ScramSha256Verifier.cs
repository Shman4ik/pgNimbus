using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace PgNimbus.Core.Security;

/// <summary>
/// The four parts of a PostgreSQL SCRAM-SHA-256 secret, as
/// <c>pg_authid.rolpassword</c> stores it:
/// <c>SCRAM-SHA-256$&lt;iterations&gt;:&lt;salt&gt;$&lt;StoredKey&gt;:&lt;ServerKey&gt;</c>.
/// </summary>
public sealed record ScramSha256Secret(int Iterations, byte[] Salt, byte[] StoredKey, byte[] ServerKey);

/// <summary>
/// Computes a PostgreSQL SCRAM-SHA-256 password verifier on the client, the way
/// psql's <c>\password</c> does through libpq's <c>PQencryptPasswordConn</c>.
///
/// <para><b>Why this exists.</b> <c>CREATE ROLE … PASSWORD 'hunter2'</c> is
/// the one statement pgNimbus cannot parameterize (Postgres has no parameter
/// form for it), and a cleartext literal in statement text reaches places the
/// client never sees: the server log on any failure
/// (<c>log_min_error_statement</c> writes <c>STATEMENT: …</c>, and "permission
/// denied to create role" is the ordinary failure on managed Postgres), every
/// run under <c>log_statement = ddl</c> or pgaudit, <c>pg_stat_activity</c>
/// while it runs, and <c>pg_stat_statements</c> before PG 16. What the server
/// would do with the cleartext is compute exactly this verifier and store it,
/// so computing it here and sending <c>PASSWORD 'SCRAM-SHA-256$4096:…'</c>
/// gives the server the same stored secret while the cleartext never leaves
/// the machine. Postgres recognises a SCRAM secret in a <c>PASSWORD</c> literal
/// and stores it as-is whatever <c>password_encryption</c> says, and an
/// <c>md5</c> line in <c>pg_hba.conf</c> authenticates a SCRAM-stored password
/// by negotiating SCRAM, so nothing about the server's setup has to change.
/// A server older than 10 has no SCRAM and would store the verifier as the
/// password itself; pgNimbus does not support those.</para>
///
/// <para><b>The algorithm</b> (RFC 5802 / RFC 7677, as libpq's
/// <c>scram_build_secret</c> applies it): SASLprep the password, draw a
/// random 16-byte salt, <c>SaltedPassword = PBKDF2-HMAC-SHA-256(password,
/// salt, 4096)</c>, <c>ClientKey = HMAC(SaltedPassword, "Client Key")</c>,
/// <c>StoredKey = SHA-256(ClientKey)</c>, <c>ServerKey = HMAC(SaltedPassword,
/// "Server Key")</c>; the verifier carries the iteration count, the salt, the
/// StoredKey and the ServerKey, each base64. Only
/// <c>System.Security.Cryptography</c> is used: no new package.</para>
///
/// <para><b>SASLprep</b> (RFC 4013) is applied as libpq's <c>pg_saslprep</c>
/// applies it, including its two shortcuts: an all-ASCII password is used as
/// typed, and a password the profile rejects (a control character, a private-use
/// or unassigned-plane code point, a right-to-left string that also holds
/// left-to-right letters) is used <em>raw</em> rather than refused, which is
/// what both libpq and the server do on <c>SASLPREP_INVALID</c>. What is
/// covered: the RFC 3454 B.1 "map to nothing" characters are dropped, the C.1.2
/// non-ASCII spaces become U+0020, the result is NFKC-normalised, the C.1.2,
/// C.2.1, C.2.2, C.3, C.4, C.5, C.6, C.7, C.8 and C.9 prohibited tables are
/// checked against it, and the section 6 bidi rule is applied. Two departures
/// from a full transcription, both documented on purpose: unassigned code
/// points are not rejected (libpq skips that check too), and the LCat table
/// for the bidi rule is derived from Unicode general categories rather than
/// copied from RFC 3454 D.2, so a right-to-left password that also carries an
/// unusual left-to-right symbol may be judged differently from libpq. A wrong
/// answer there only changes which of two texts is hashed, the prepared one or
/// the raw one, and those differ only when the mapping or normalisation above
/// changed something, so the intersection is small and stated here rather than
/// hidden.</para>
///
/// <para><b>NFKC needs the runtime's normalisation tables.</b> Under
/// <c>InvariantGlobalization</c>, which the shipped app runs with,
/// <see cref="string.Normalize(NormalizationForm)"/> returns its input and
/// <see cref="string.IsNormalized(NormalizationForm)"/> answers true, so a
/// non-ASCII password there is hashed as typed rather than in NFKC form.
/// <see cref="NormalizationAvailable"/> says which. The practical difference is
/// confined to passwords holding compatibility characters (ligatures, full-width
/// forms, superscripts) or decomposed accents, which keyboards do not produce;
/// and it is not a regression: Npgsql's own SCRAM client normalises through the
/// same call, so a password like that already could not log in from this app.
/// The unit tests run without invariant mode and pin the NFKC path.</para>
/// </summary>
public static class ScramSha256Verifier
{
    /// <summary>The prefix every SCRAM-SHA-256 secret starts with.</summary>
    public const string Prefix = "SCRAM-SHA-256$";

    /// <summary>
    /// libpq's <c>SCRAM_SHA_256_DEFAULT_ITERATIONS</c>; PG 16 made it a setting
    /// (<c>scram_iterations</c>) whose default is still this.
    /// </summary>
    public const int DefaultIterations = 4096;

    /// <summary>libpq's <c>SCRAM_DEFAULT_SALT_LEN</c>.</summary>
    public const int SaltLength = 16;

    private const int KeyLength = 32;

    private static readonly byte[] ClientKeyLabel = "Client Key"u8.ToArray();
    private static readonly byte[] ServerKeyLabel = "Server Key"u8.ToArray();

    /// <summary>
    /// Whether the runtime can NFKC-normalise at all: false under
    /// <c>InvariantGlobalization</c>, where <see cref="string.Normalize(NormalizationForm)"/>
    /// is the identity. Probed once with a character NFKC always rewrites (OHM
    /// SIGN to GREEK CAPITAL LETTER OMEGA).
    /// </summary>
    public static bool NormalizationAvailable { get; } = "\u2126".Normalize(NormalizationForm.FormKC) == "\u03A9";

    /// <summary>
    /// The verifier for <paramref name="password"/> with a fresh random salt.
    /// Two calls for the same password give two different verifiers, which is
    /// the point of the salt.
    /// </summary>
    public static string Build(string password)
    {
        Span<byte> salt = stackalloc byte[SaltLength];
        RandomNumberGenerator.Fill(salt);
        return Build(password, salt);
    }

    /// <summary>
    /// The verifier for <paramref name="password"/> with the given salt and
    /// iteration count. Deterministic, which is what lets a known vector be
    /// pinned in a test; production goes through <see cref="Build(string)"/>.
    /// </summary>
    public static string Build(string password, ReadOnlySpan<byte> salt, int iterations = DefaultIterations)
    {
        ArgumentNullException.ThrowIfNull(password);
        if (salt.IsEmpty)
        {
            throw new ArgumentException("A SCRAM salt cannot be empty.", nameof(salt));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(iterations, 1);

        // libpq's rule: SASLPREP_SUCCESS hashes the prepared text, SASLPREP_INVALID
        // hashes what was typed. Never a refusal.
        var prepared = TrySaslprep(password, out var output) ? output : password;
        var passwordBytes = Encoding.UTF8.GetBytes(prepared);
        var saltedPassword = new byte[KeyLength];
        var clientKey = new byte[KeyLength];

        try
        {
            Rfc2898DeriveBytes.Pbkdf2(passwordBytes, salt, saltedPassword, iterations, HashAlgorithmName.SHA256);
            HMACSHA256.HashData(saltedPassword, ClientKeyLabel, clientKey);
            var storedKey = SHA256.HashData(clientKey);
            var serverKey = HMACSHA256.HashData(saltedPassword, ServerKeyLabel);

            return string.Concat(
                Prefix,
                iterations.ToString(CultureInfo.InvariantCulture),
                ":",
                Convert.ToBase64String(salt),
                "$",
                Convert.ToBase64String(storedKey),
                ":",
                Convert.ToBase64String(serverKey));
        }
        finally
        {
            // The salted password and the client key are what an attacker
            // needs to impersonate the client; the verifier itself is not.
            CryptographicOperations.ZeroMemory(passwordBytes);
            CryptographicOperations.ZeroMemory(saltedPassword);
            CryptographicOperations.ZeroMemory(clientKey);
        }
    }

    /// <summary>
    /// Splits a verifier back into its four parts, the way the server's
    /// <c>parse_scram_secret</c> reads <c>pg_authid.rolpassword</c>. False for
    /// anything that is not a well-formed SCRAM-SHA-256 secret.
    /// </summary>
    public static bool TryParse(string? verifier, [NotNullWhen(true)] out ScramSha256Secret? secret)
    {
        secret = null;
        if (verifier is null || !verifier.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var rest = verifier.AsSpan(Prefix.Length);
        var keysAt = rest.IndexOf('$');
        if (keysAt < 0)
        {
            return false;
        }

        var saltPart = rest[..keysAt];
        var keysPart = rest[(keysAt + 1)..];
        var saltColon = saltPart.IndexOf(':');
        var keysColon = keysPart.IndexOf(':');
        if (saltColon < 0 || keysColon < 0)
        {
            return false;
        }

        if (!int.TryParse(saltPart[..saltColon], NumberStyles.None, CultureInfo.InvariantCulture, out var iterations)
            || iterations < 1
            || !TryBase64(saltPart[(saltColon + 1)..], out var salt)
            || salt.Length == 0
            || !TryBase64(keysPart[..keysColon], out var storedKey)
            || storedKey.Length != KeyLength
            || !TryBase64(keysPart[(keysColon + 1)..], out var serverKey)
            || serverKey.Length != KeyLength)
        {
            return false;
        }

        secret = new ScramSha256Secret(iterations, salt, storedKey, serverKey);
        return true;
    }

    /// <summary>
    /// RFC 4013 SASLprep as libpq's <c>pg_saslprep</c> applies it (see the type
    /// summary for exactly what is covered). True with the prepared text on
    /// success; false, with <paramref name="prepared"/> set to the input, when
    /// the profile prohibits the password, which callers treat as "use it as
    /// typed" rather than as an error.
    /// </summary>
    public static bool TrySaslprep(string password, out string prepared)
    {
        ArgumentNullException.ThrowIfNull(password);
        prepared = password;

        // libpq's shortcut: an ASCII string needs no processing (it does not
        // even reject ASCII control characters on this path, and neither does
        // this; the output is the input either way).
        if (Ascii.IsValid(password))
        {
            return true;
        }

        // Step 1, mapping: drop B.1, turn C.1.2 spaces into U+0020. Decode by
        // code point so astral-plane characters are seen whole; an unpaired
        // surrogate is C.5 and fails the string.
        var mapped = new StringBuilder(password.Length);
        var remaining = password.AsSpan();
        while (!remaining.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(remaining, out var rune, out var consumed) != OperationStatus.Done)
            {
                return false;
            }

            remaining = remaining[consumed..];
            var cp = rune.Value;
            if (IsMappedToNothing(cp))
            {
                continue;
            }

            mapped.Append(IsNonAsciiSpace(cp) ? new Rune(' ') : rune);
        }

        // Step 2, normalisation. Identity under InvariantGlobalization (see
        // NormalizationAvailable); the tables are the runtime's.
        var normalized = mapped.ToString().Normalize(NormalizationForm.FormKC);

        // Steps 3 and 4, prohibited output and the bidi rule.
        var sawRandAl = false;
        var sawL = false;
        var firstIsRandAl = false;
        var lastIsRandAl = false;
        var first = true;
        remaining = normalized.AsSpan();
        while (!remaining.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(remaining, out var rune, out var consumed) != OperationStatus.Done)
            {
                return false;
            }

            remaining = remaining[consumed..];
            var cp = rune.Value;
            if (IsProhibited(cp))
            {
                return false;
            }

            var randAl = IsRandALCat(cp);
            var l = !randAl && IsLCat(rune);
            sawRandAl |= randAl;
            sawL |= l;
            if (first)
            {
                firstIsRandAl = randAl;
                first = false;
            }

            lastIsRandAl = randAl;
        }

        if (sawRandAl && (sawL || !firstIsRandAl || !lastIsRandAl))
        {
            return false;
        }

        prepared = normalized;
        return true;
    }

    private static bool TryBase64(ReadOnlySpan<char> text, out byte[] bytes)
    {
        bytes = [];
        if (text.IsEmpty)
        {
            return false;
        }

        var buffer = new byte[text.Length];
        if (!Convert.TryFromBase64Chars(text, buffer, out var written))
        {
            return false;
        }

        bytes = buffer[..written];
        return true;
    }

    /// <summary>RFC 3454 B.1, "commonly mapped to nothing".</summary>
    private static bool IsMappedToNothing(int cp) => cp is 0x00AD or 0x034F or 0x1806
        or (>= 0x180B and <= 0x180D) or (>= 0x200B and <= 0x200D) or 0x2060
        or (>= 0xFE00 and <= 0xFE0F) or 0xFEFF;

    /// <summary>RFC 3454 C.1.2, non-ASCII space characters.</summary>
    private static bool IsNonAsciiSpace(int cp) => cp is 0x00A0 or 0x1680
        or (>= 0x2000 and <= 0x200B) or 0x202F or 0x205F or 0x3000;

    /// <summary>
    /// The RFC 4013 prohibited output: C.1.2, C.2.1, C.2.2, C.3, C.4, C.5, C.6,
    /// C.7, C.8 and C.9 of RFC 3454. The C.2.2 ranges are the ones libpq's
    /// <c>saslprep.c</c> carries.
    /// </summary>
    private static bool IsProhibited(int cp) =>
        IsNonAsciiSpace(cp)
        // C.2.1 ASCII control characters
        || cp is (>= 0x0000 and <= 0x001F) or 0x007F
        // C.2.2 non-ASCII control characters
        || cp is (>= 0x0080 and <= 0x009F) or 0x06DD or 0x070F or 0x180E or 0x200C or 0x200D
            or 0x2028 or 0x2029 or (>= 0x2060 and <= 0x2063) or (>= 0x206A and <= 0x206F)
            or 0xFEFF or (>= 0xFFF9 and <= 0xFFFB) or (>= 0x1D173 and <= 0x1D17A)
        // C.3 private use
        || cp is (>= 0xE000 and <= 0xF8FF) or (>= 0xF0000 and <= 0xFFFFD) or (>= 0x100000 and <= 0x10FFFD)
        // C.4 non-character code points
        || cp is (>= 0xFDD0 and <= 0xFDEF) || (cp & 0xFFFE) == 0xFFFE
        // C.5 surrogate codes (an unpaired one never decodes to a Rune, but keep the table complete)
        || cp is (>= 0xD800 and <= 0xDFFF)
        // C.6 inappropriate for plain text
        || cp is (>= 0xFFF9 and <= 0xFFFD)
        // C.7 inappropriate for canonical representation
        || cp is (>= 0x2FF0 and <= 0x2FFB)
        // C.8 change display properties or are deprecated
        || cp is 0x0340 or 0x0341 or 0x200E or 0x200F or (>= 0x202A and <= 0x202E) or (>= 0x206A and <= 0x206F)
        // C.9 tagging characters
        || cp is 0xE0001 or (>= 0xE0020 and <= 0xE007F);

    /// <summary>RFC 3454 D.1, characters with bidirectional property R or AL.</summary>
    private static bool IsRandALCat(int cp) => cp is 0x05BE or 0x05C0 or 0x05C3
        or (>= 0x05D0 and <= 0x05EA) or (>= 0x05F0 and <= 0x05F4) or 0x061B or 0x061F
        or (>= 0x0621 and <= 0x063A) or (>= 0x0640 and <= 0x064A) or (>= 0x066D and <= 0x066F)
        or (>= 0x0671 and <= 0x06D5) or 0x06DD or 0x06E5 or 0x06E6 or (>= 0x06FA and <= 0x06FE)
        or (>= 0x0700 and <= 0x070D) or 0x0710 or (>= 0x0712 and <= 0x072C)
        or (>= 0x0780 and <= 0x07A5) or 0x07B1 or 0x200F or 0xFB1D
        or (>= 0xFB1F and <= 0xFB28) or (>= 0xFB2A and <= 0xFB36) or (>= 0xFB38 and <= 0xFB3C) or 0xFB3E
        or (>= 0xFB40 and <= 0xFB41) or (>= 0xFB43 and <= 0xFB44) or (>= 0xFB46 and <= 0xFBB1)
        or (>= 0xFBD3 and <= 0xFD3D) or (>= 0xFD50 and <= 0xFD8F) or (>= 0xFD92 and <= 0xFDC7)
        or (>= 0xFDF0 and <= 0xFDFC) or (>= 0xFE70 and <= 0xFE74) or (>= 0xFE76 and <= 0xFEFC);

    /// <summary>
    /// RFC 3454 D.2 (bidirectional property L), approximated by general
    /// category: letters, letter-numbers and spacing combining marks that are
    /// not in D.1. See the type summary for what that approximation costs.
    /// </summary>
    private static bool IsLCat(Rune rune) => Rune.GetUnicodeCategory(rune) is
        UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter
        or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter or UnicodeCategory.LetterNumber
        or UnicodeCategory.SpacingCombiningMark;
}
