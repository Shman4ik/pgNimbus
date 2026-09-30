using System.Text;
using PgNimbus.Core.Security;

namespace PgNimbus.Core.Tests.Security;

/// <summary>
/// Pins the client-side SCRAM-SHA-256 verifier to vectors computed outside
/// .NET, and the SASLprep subset to the RFC 4013 rules it claims to follow.
///
/// <para>The pinned strings were produced with Python's standard library,
/// independently of any code in this repository:</para>
/// <code>
/// python3 -c "
/// import hashlib, hmac, base64
/// pw = b'secret'; salt = bytes(range(16)); it = 4096
/// sp = hashlib.pbkdf2_hmac('sha256', pw, salt, it)
/// ck = hmac.new(sp, b'Client Key', hashlib.sha256).digest()
/// sk = hmac.new(sp, b'Server Key', hashlib.sha256).digest()
/// print('SCRAM-SHA-256$%d:%s$%s:%s' % (it, base64.b64encode(salt).decode(),
///       base64.b64encode(hashlib.sha256(ck).digest()).decode(), base64.b64encode(sk).decode()))"
/// </code>
/// <para>The same script with <c>pw = 'pässwörd'.encode()</c>,
/// <c>pw = 'pä\x07'.encode()</c> and <c>pw = b'fi'</c> gives the other three.
/// The salt is the bytes 0x00 to 0x0F, <c>AAECAwQFBgcICQoLDA0ODw==</c> in
/// base64. The live half of this, a role created with a verifier that a fresh
/// connection then authenticates against, is <see cref="ScramPasswordServerTests"/>.</para>
/// </summary>
public class ScramSha256VerifierTests
{
    private static readonly byte[] Salt = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();

    private const string SecretVector =
        "SCRAM-SHA-256$4096:AAECAwQFBgcICQoLDA0ODw==$THoPhoTAuqyoQsK4dUHncUzgfD8fdmhsgKZhWVqNP5U=:7YiHMMi2OcXGRogub03Ek06JRZ9bkhTOdCzHa5iPLiQ=";

    private const string UmlautVector =
        "SCRAM-SHA-256$4096:AAECAwQFBgcICQoLDA0ODw==$HV4TtNKIt8oOQWZCIedFYDVMOGn3/uurWVfN15VjjP4=:bGoGbn5l5XEF6vc5q836UvsaNZ/n1s4zNZ+A5nNQQv8=";

    private const string ControlCharVector =
        "SCRAM-SHA-256$4096:AAECAwQFBgcICQoLDA0ODw==$G+PxZ1BpzyfrNSDQvT+/oybo7AMNp16sMZQLEiN63yQ=:aebfIq9ZtEC+sCZ4sIGc5ee53Zkkmlqq2fHP17w/NJs=";

    private const string FiVector =
        "SCRAM-SHA-256$4096:AAECAwQFBgcICQoLDA0ODw==$x6nq8LA0Qk9abZjQbYF/y7lcZluQpRNm1FlCqqrr4YQ=:PYuLFFfzXxUgm56XrRWR1Wuzs++CzNOn+clyBtHLbyI=";

    [Test]
    public async Task A_known_salt_gives_the_vector_python_computes()
    {
        await Assert.That(ScramSha256Verifier.Build("secret", Salt)).IsEqualTo(SecretVector);
    }

    [Test]
    public async Task A_non_ascii_password_is_hashed_as_utf8()
    {
        // ä and ö are the same in NFC and NFKC, so this pins the UTF-8 encoding
        // of the password rather than the normalisation.
        await Assert.That(ScramSha256Verifier.Build("p\u00E4ssw\u00F6rd", Salt)).IsEqualTo(UmlautVector);
    }

    [Test]
    public async Task The_verifier_parses_back_into_its_four_parts()
    {
        await Assert.That(ScramSha256Verifier.TryParse(SecretVector, out var secret)).IsTrue();
        await Assert.That(secret!.Iterations).IsEqualTo(4096);
        await Assert.That(secret.Salt).IsEquivalentTo(Salt);
        await Assert.That(secret.StoredKey).Count().IsEqualTo(32);
        await Assert.That(secret.ServerKey).Count().IsEqualTo(32);
        await Assert.That(Convert.ToBase64String(secret.StoredKey)).IsEqualTo("THoPhoTAuqyoQsK4dUHncUzgfD8fdmhsgKZhWVqNP5U=");
        await Assert.That(Convert.ToBase64String(secret.ServerKey)).IsEqualTo("7YiHMMi2OcXGRogub03Ek06JRZ9bkhTOdCzHa5iPLiQ=");
    }

    [Test]
    [Arguments("")]
    [Arguments("hunter2")]
    [Arguments("md5abcdef")]
    [Arguments("SCRAM-SHA-256$4096:AAECAwQFBgcICQoLDA0ODw==")]
    [Arguments("SCRAM-SHA-256$4096:AAECAwQFBgcICQoLDA0ODw==$THoPhoTAuqyoQsK4dUHncUzgfD8fdmhsgKZhWVqNP5U=")]
    [Arguments("SCRAM-SHA-256$abc:AAECAwQFBgcICQoLDA0ODw==$THoPhoTAuqyoQsK4dUHncUzgfD8fdmhsgKZhWVqNP5U=:7YiHMMi2OcXGRogub03Ek06JRZ9bkhTOdCzHa5iPLiQ=")]
    [Arguments("SCRAM-SHA-256$4096:AAECAwQFBgcICQoLDA0ODw==$c2hvcnQ=:7YiHMMi2OcXGRogub03Ek06JRZ9bkhTOdCzHa5iPLiQ=")]
    [Arguments("SCRAM-SHA-256$4096:not base64!$THoPhoTAuqyoQsK4dUHncUzgfD8fdmhsgKZhWVqNP5U=:7YiHMMi2OcXGRogub03Ek06JRZ9bkhTOdCzHa5iPLiQ=")]
    public async Task Anything_that_is_not_a_secret_does_not_parse(string text)
    {
        await Assert.That(ScramSha256Verifier.TryParse(text, out _)).IsFalse();
        await Assert.That(ScramSha256Verifier.TryParse(null, out _)).IsFalse();
    }

    [Test]
    public async Task Two_verifiers_for_one_password_differ_in_salt_and_keys()
    {
        var first = ScramSha256Verifier.Build("hunter2");
        var second = ScramSha256Verifier.Build("hunter2");

        await Assert.That(first).IsNotEqualTo(second);
        await Assert.That(ScramSha256Verifier.TryParse(first, out var a)).IsTrue();
        await Assert.That(ScramSha256Verifier.TryParse(second, out var b)).IsTrue();
        await Assert.That(a!.Salt).Count().IsEqualTo(ScramSha256Verifier.SaltLength);
        await Assert.That(a.Salt).IsNotEquivalentTo(b!.Salt);
        await Assert.That(a.StoredKey).IsNotEquivalentTo(b.StoredKey);
        await Assert.That(a.ServerKey).IsNotEquivalentTo(b.ServerKey);
        await Assert.That(a.Iterations).IsEqualTo(ScramSha256Verifier.DefaultIterations);
    }

    [Test]
    public async Task The_verifier_never_contains_the_password_or_a_quote()
    {
        // RoleScriptBuilder single-quotes this and the audit's finding 13 says
        // standard_conforming_strings cannot be assumed on: base64, digits, $
        // and : are safe under either setting, so the literal is too.
        var verifier = ScramSha256Verifier.Build("it's a \\ password");

        await Assert.That(verifier).DoesNotContain("password");
        await Assert.That(verifier.All(c => char.IsAsciiLetterOrDigit(c) || c is '+' or '/' or '=' or '$' or ':' or '-')).IsTrue();
    }

    [Test]
    public async Task The_iteration_count_is_written_into_the_verifier()
    {
        var verifier = ScramSha256Verifier.Build("secret", Salt, iterations: 10000);

        await Assert.That(verifier).StartsWith("SCRAM-SHA-256$10000:");
        await Assert.That(verifier).IsNotEqualTo(SecretVector);
    }

    [Test]
    public async Task This_test_run_can_normalise()
    {
        // The App runs with InvariantGlobalization, where NFKC is the identity
        // and the tests below would pass for the wrong reason; the test project
        // does not, and this pins that the NFKC tests are testing something.
        await Assert.That(ScramSha256Verifier.NormalizationAvailable).IsTrue();
    }

    [Test]
    public async Task Saslprep_nfkc_normalises_a_non_ascii_password()
    {
        // LATIN SMALL LIGATURE FI decomposes to "fi" under NFKC, and the vector
        // for b"fi" is what comes out.
        await Assert.That(ScramSha256Verifier.TrySaslprep("\uFB01", out var prepared)).IsTrue();
        await Assert.That(prepared).IsEqualTo("fi");
        await Assert.That(ScramSha256Verifier.Build("\uFB01", Salt)).IsEqualTo(FiVector);

        // A decomposed accent is composed: e + COMBINING ACUTE becomes é.
        await Assert.That(ScramSha256Verifier.TrySaslprep("cafe\u0301", out prepared)).IsTrue();
        await Assert.That(prepared).IsEqualTo("caf\u00E9");
    }

    [Test]
    public async Task Saslprep_maps_before_it_normalises()
    {
        // B.1 map-to-nothing: a soft hyphen disappears. C.1.2: a no-break space
        // becomes an ordinary one. Both leave a password that is then ASCII.
        await Assert.That(ScramSha256Verifier.TrySaslprep("se\u00ADcret", out var prepared)).IsTrue();
        await Assert.That(prepared).IsEqualTo("secret");
        await Assert.That(ScramSha256Verifier.Build("se\u00ADcret", Salt)).IsEqualTo(SecretVector);

        await Assert.That(ScramSha256Verifier.TrySaslprep("a\u00A0b", out prepared)).IsTrue();
        await Assert.That(prepared).IsEqualTo("a b");

        // A zero-width joiner is both B.1 and C.2.2; mapping runs first, so it
        // is dropped rather than prohibited.
        await Assert.That(ScramSha256Verifier.TrySaslprep("x\u200Dy\u00E9", out prepared)).IsTrue();
        await Assert.That(prepared).IsEqualTo("xy\u00E9");
    }

    [Test]
    public async Task An_ascii_password_is_used_as_typed()
    {
        // libpq's shortcut: pure ASCII skips the profile entirely, control
        // characters included.
        await Assert.That(ScramSha256Verifier.TrySaslprep("tab\there", out var prepared)).IsTrue();
        await Assert.That(prepared).IsEqualTo("tab\there");
    }

    [Test]
    [Arguments("p\u00E4\u0007", "C.2.1 ASCII control, once the string is not pure ASCII")]
    [Arguments("p\u00E4\u0085", "C.2.2 non-ASCII control")]
    [Arguments("p\u00E4\u2028", "C.2.2 line separator")]
    [Arguments("p\uE000", "C.3 private use")]
    [Arguments("p\U000F0001", "C.3 supplementary private use")]
    [Arguments("p\uFDD0", "C.4 non-character")]
    [Arguments("p\U0001FFFE", "C.4 non-character in an astral plane")]
    [Arguments("p\uFFFD", "C.6 replacement character")]
    [Arguments("p\u2FF0", "C.7 ideographic description")]
    [Arguments("p\u202E", "C.8 right-to-left override")]
    [Arguments("p\U000E0001", "C.9 language tag")]
    [Arguments("p\uD800", "C.5 unpaired surrogate")]
    public async Task A_prohibited_character_fails_the_profile_and_the_password_is_kept_as_typed(string password, string why)
    {
        await Assert.That(ScramSha256Verifier.TrySaslprep(password, out var prepared)).IsFalse().Because(why);
        await Assert.That(prepared).IsEqualTo(password);
    }

    [Test]
    public async Task A_rejected_password_is_hashed_raw_as_libpq_does()
    {
        // pg_saslprep answers SASLPREP_INVALID for the BEL, and both libpq and
        // the server then hash the bytes as typed rather than refuse. The
        // vector is python's for the raw UTF-8 of "pä\x07".
        await Assert.That(ScramSha256Verifier.Build("p\u00E4\u0007", Salt)).IsEqualTo(ControlCharVector);
    }

    [Test]
    public async Task The_bidi_rule_rejects_a_mixed_direction_password()
    {
        // Hebrew alone passes; Hebrew with a Latin letter fails; Hebrew that
        // does not start and end with a right-to-left character fails.
        await Assert.That(ScramSha256Verifier.TrySaslprep("\u05E9\u05DC\u05D5\u05DD", out var prepared)).IsTrue();
        await Assert.That(prepared).IsEqualTo("\u05E9\u05DC\u05D5\u05DD");

        await Assert.That(ScramSha256Verifier.TrySaslprep("\u05E9a\u05DC", out _)).IsFalse();
        await Assert.That(ScramSha256Verifier.TrySaslprep("\u05E9\u05DC1", out _)).IsFalse();
        await Assert.That(ScramSha256Verifier.TrySaslprep("1\u05E9\u05DC", out _)).IsFalse();

        // Digits are neither L nor R, so a right-to-left password may hold them
        // in the middle.
        await Assert.That(ScramSha256Verifier.TrySaslprep("\u05E9 1 \u05DC", out _)).IsTrue();
    }

    [Test]
    public async Task Build_rejects_an_empty_salt_and_a_null_password()
    {
        var emptySalt = Array.Empty<byte>();
        await Assert.That(() => { _ = ScramSha256Verifier.Build("x", emptySalt); }).Throws<ArgumentException>();
        await Assert.That(() => { _ = ScramSha256Verifier.Build(null!, Salt); }).Throws<ArgumentNullException>();
        await Assert.That(() => { _ = ScramSha256Verifier.Build("x", Salt, iterations: 0); }).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task An_empty_password_still_builds_a_well_formed_verifier()
    {
        // The role editor does not send one, but the server accepts PASSWORD ''
        // and so does the algorithm.
        var verifier = ScramSha256Verifier.Build("", Salt);

        await Assert.That(ScramSha256Verifier.TryParse(verifier, out _)).IsTrue();
        await Assert.That(Encoding.UTF8.GetByteCount("")).IsEqualTo(0);
    }
}
