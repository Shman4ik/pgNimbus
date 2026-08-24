using PgNimbus.Core.Security;

namespace PgNimbus.Core.Tests.Security;

/// <summary>
/// The failure mode this class exists to prevent is a password sitting in
/// <c>query-history.json</c> or <c>pgnimbus.log</c> in plain text, so the tests
/// lean on inputs that a naive matcher gets wrong: escaped quotes, dollar
/// quoting, the word PASSWORD inside a string, and more than one statement.
/// </summary>
public class SecretRedactorTests
{
    [Test]
    public async Task PlainPasswordIsRedacted()
    {
        await Assert.That(SecretRedactor.Redact("CREATE ROLE app WITH LOGIN PASSWORD 'hunter2';"))
            .IsEqualTo("CREATE ROLE app WITH LOGIN PASSWORD '<redacted>';");
    }

    [Test]
    [Arguments("ALTER ROLE app PASSWORD 'p';")]
    [Arguments("ALTER ROLE app ENCRYPTED PASSWORD 'p';")]
    [Arguments("ALTER ROLE app UNENCRYPTED PASSWORD 'p';")]
    [Arguments("ALTER ROLE app PaSsWoRd 'p';")]
    [Arguments("ALTER ROLE app password 'p';")]
    [Arguments("ALTER ROLE app PASSWORD  \n  'p';")]
    public async Task EveryKeywordSpellingIsCaught(string sql)
    {
        await Assert.That(SecretRedactor.ContainsSecret(sql)).IsTrue();
        await Assert.That(SecretRedactor.Redact(sql)).DoesNotContain("'p'");
        await Assert.That(SecretRedactor.Redact(sql)).Contains("'<redacted>'");
    }

    [Test]
    public async Task DoubledQuotesInsideTheLiteralDoNotEndItEarly()
    {
        // 'hun''ter2' is one literal. A matcher that stops at the first inner
        // quote leaves "ter2'" behind — half a password, still on disk.
        await Assert.That(SecretRedactor.Redact("ALTER ROLE app PASSWORD 'hun''ter2';"))
            .IsEqualTo("ALTER ROLE app PASSWORD '<redacted>';");
    }

    [Test]
    public async Task EscapeStringLiteralsAreRedactedIncludingTheirPrefix()
    {
        await Assert.That(SecretRedactor.Redact(@"ALTER ROLE app PASSWORD E'hun\'ter2';"))
            .IsEqualTo("ALTER ROLE app PASSWORD '<redacted>';");
    }

    [Test]
    [Arguments("ALTER ROLE app PASSWORD $$hunter2$$;")]
    [Arguments("ALTER ROLE app PASSWORD $pw$hunter2$pw$;")]
    public async Task DollarQuotedPasswordsAreRedacted(string sql)
    {
        await Assert.That(SecretRedactor.Redact(sql)).IsEqualTo("ALTER ROLE app PASSWORD '<redacted>';");
    }

    [Test]
    public async Task PasswordNullIsLeftAlone()
    {
        // Not a secret — the removal of one. Rewriting it would change what the
        // history says happened.
        const string sql = "ALTER ROLE app PASSWORD NULL;";

        await Assert.That(SecretRedactor.ContainsSecret(sql)).IsFalse();
        await Assert.That(SecretRedactor.Redact(sql)).IsEqualTo(sql);
    }

    [Test]
    public async Task EveryOccurrenceInAScriptIsRedacted()
    {
        var sql = string.Join('\n',
            "CREATE ROLE a WITH PASSWORD 'one';",
            "CREATE ROLE b WITH PASSWORD 'two';");

        var redacted = SecretRedactor.Redact(sql);

        await Assert.That(redacted).DoesNotContain("one");
        await Assert.That(redacted).DoesNotContain("two");
        await Assert.That(redacted.Split("'<redacted>'").Length).IsEqualTo(3);
    }

    [Test]
    public async Task SqlWithNoPasswordComesBackUnchanged()
    {
        const string sql = "SELECT * FROM users WHERE name = 'PASSWORD';";

        await Assert.That(SecretRedactor.ContainsSecret(sql)).IsFalse();
        await Assert.That(SecretRedactor.Redact(sql)).IsEqualTo(sql);
    }

    [Test]
    public async Task TheWordInsideALiteralIsNotAKeyword()
    {
        // The scanner has to know it is inside a string. A regex here is where
        // this class of bug lives.
        const string sql = "SELECT 'set PASSWORD ''x''' AS hint;";

        await Assert.That(SecretRedactor.Redact(sql)).IsEqualTo(sql);
    }

    [Test]
    public async Task AQuotedIdentifierNamedPasswordIsNotAKeyword()
    {
        const string sql = """SELECT "password" FROM accounts;""";

        await Assert.That(SecretRedactor.Redact(sql)).IsEqualTo(sql);
    }

    [Test]
    public async Task AColumnNamedPasswordIsNotAKeyword()
    {
        const string sql = "SELECT password FROM accounts WHERE id = 1;";

        await Assert.That(SecretRedactor.Redact(sql)).IsEqualTo(sql);
    }

    [Test]
    public async Task AParameterPlaceholderIsNotALiteral()
    {
        const string sql = "ALTER ROLE app PASSWORD $1;";

        // $1 is a positional parameter, not the start of a dollar quote — and
        // there is no secret in the text to strip.
        await Assert.That(SecretRedactor.Redact(sql)).IsEqualTo(sql);
    }

    [Test]
    public async Task AnUnterminatedLiteralIsSwallowedRatherThanTrusted()
    {
        // Truncated statement text (a crash log capture). Err toward redacting.
        await Assert.That(SecretRedactor.Redact("CREATE ROLE app PASSWORD 'hunter2"))
            .IsEqualTo("CREATE ROLE app PASSWORD '<redacted>'");
    }

    [Test]
    public async Task RedactionSurvivesACommentBetweenKeywordAndLiteral()
    {
        // Everything up to the literal is preserved verbatim — only the secret
        // is replaced.
        await Assert.That(SecretRedactor.Redact("ALTER ROLE app PASSWORD /* nested /* */ */ 'hunter2';"))
            .IsEqualTo("ALTER ROLE app PASSWORD /* nested /* */ */ '<redacted>';");
    }

    [Test]
    public async Task EmptyInputIsSafe()
    {
        await Assert.That(SecretRedactor.Redact("")).IsEqualTo("");
        await Assert.That(SecretRedactor.ContainsSecret("")).IsFalse();
    }
}
