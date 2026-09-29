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
    public async Task AStatementInsideAStringIsReadWithItsQuotesDecoded()
    {
        // This used to be left alone, on the grounds that a word inside a
        // literal is not a keyword. But that is exactly how EXECUTE carries a
        // statement, and the audit found passwords reaching history that way.
        // The literal's own '' escapes are decoded to read it and encoded again
        // to write the replacement back, so the string stays well-formed.
        const string sql = "SELECT 'set PASSWORD ''x''' AS hint;";

        await Assert.That(SecretRedactor.Redact(sql)).IsEqualTo("SELECT 'set PASSWORD ''<redacted>''' AS hint;");
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

    // --- Security audit 2026-09, finding 8: the shapes that reached history.json ---

    [Test]
    [Arguments("DO $$ BEGIN IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'app') THEN CREATE ROLE app LOGIN PASSWORD 's3cret'; END IF; END $$;")]
    [Arguments("DO $do$\nBEGIN\n  CREATE ROLE app LOGIN PASSWORD 's3cret';\nEND\n$do$;")]
    [Arguments("CREATE FUNCTION mk() RETURNS void LANGUAGE plpgsql AS $fn$ BEGIN ALTER ROLE app PASSWORD E's3cret'; END $fn$;")]
    public async Task APasswordInsideADollarQuotedBodyIsRedacted(string sql)
    {
        var redacted = SecretRedactor.Redact(sql);

        await Assert.That(redacted).DoesNotContain("s3cret");
        await Assert.That(redacted).Contains("PASSWORD '<redacted>'");
    }

    [Test]
    public async Task TheDoBlockKeepsEverythingButTheSecret()
    {
        await Assert.That(SecretRedactor.Redact("DO $$ BEGIN IF true THEN CREATE ROLE app LOGIN PASSWORD 's3cret'; END IF; END $$;"))
            .IsEqualTo("DO $$ BEGIN IF true THEN CREATE ROLE app LOGIN PASSWORD '<redacted>'; END IF; END $$;");
    }

    [Test]
    public async Task AStatementExecutedFromAStringIsRedacted()
    {
        await Assert.That(SecretRedactor.Redact("DO $$ BEGIN EXECUTE 'ALTER ROLE app PASSWORD ''s3cret'''; END $$;"))
            .IsEqualTo("DO $$ BEGIN EXECUTE 'ALTER ROLE app PASSWORD ''<redacted>'''; END $$;");
    }

    [Test]
    public async Task EveryLiteralAfterAFormatPlaceholderForThePasswordIsRedacted()
    {
        // format() fills %L from its arguments, so the secret is one of the
        // literals that follow. Which one can't be told, so all of them go.
        const string sql = "DO $$ BEGIN EXECUTE format('ALTER ROLE %I PASSWORD %L', 'app', 's3cret'); END $$;";

        await Assert.That(SecretRedactor.Redact(sql))
            .IsEqualTo("DO $$ BEGIN EXECUTE format('ALTER ROLE %I PASSWORD %L', '<redacted>', '<redacted>'); END $$;");
    }

    [Test]
    public async Task ALiteralConcatenatedOntoAHangingPasswordIsRedacted()
    {
        const string sql = "EXECUTE 'ALTER ROLE app PASSWORD ' || quote_literal('s3cret'); SELECT 'kept';";

        await Assert.That(SecretRedactor.Redact(sql))
            .IsEqualTo("EXECUTE 'ALTER ROLE app PASSWORD ' || quote_literal('<redacted>'); SELECT 'kept';");
    }

    [Test]
    public async Task AConninfoPasswordInsideAStringIsRedactedAndTheRestKept()
    {
        await Assert.That(SecretRedactor.Redact(
                "CREATE SUBSCRIPTION sub CONNECTION 'host=db1 port=5432 user=rep password=s3cret dbname=app' PUBLICATION pub;"))
            .IsEqualTo("CREATE SUBSCRIPTION sub CONNECTION 'host=db1 port=5432 user=rep password=<redacted> dbname=app' PUBLICATION pub;");
    }

    [Test]
    public async Task AQuotedConninfoValueIsRedactedToItsClosingQuote()
    {
        // Inside the SQL string the conninfo's own quotes are doubled.
        await Assert.That(SecretRedactor.Redact("SELECT dblink_connect('c', 'host=x password = ''s3 cret'' dbname=y');"))
            .IsEqualTo("SELECT dblink_connect('c', 'host=x password = <redacted> dbname=y');");
    }

    [Test]
    [Arguments("ALTER SUBSCRIPTION sub CONNECTION 'password=s3cret host=db1';")]
    [Arguments("SELECT dblink_connect('dbname=app PASSWORD=s3cret');")]
    [Arguments("SELECT dblink('host=db sslpassword=s3cret', 'SELECT 1');")]
    [Arguments("SELECT dblink_connect('postgresql://rep:s3cret@db1:5432/app');")]
    [Arguments("CREATE SERVER s FOREIGN DATA WRAPPER postgres_fdw OPTIONS (host 'db', password 's3cret');")]
    [Arguments("SELECT create_login(name => 'app', password => 's3cret');")]
    public async Task ConnectionSecretsInLiteralsAreRedacted(string sql)
    {
        var redacted = SecretRedactor.Redact(sql);

        await Assert.That(redacted).DoesNotContain("s3cret");
        await Assert.That(redacted).Contains("<redacted>");
    }

    [Test]
    [Arguments("-- ALTER ROLE app PASSWORD 'old'\nSELECT 1;", "-- ALTER ROLE app PASSWORD '<redacted>'\nSELECT 1;")]
    [Arguments("/* CREATE ROLE app PASSWORD 'old' */ SELECT 1;", "/* CREATE ROLE app PASSWORD '<redacted>' */ SELECT 1;")]
    [Arguments("-- don't run this: ALTER ROLE app PASSWORD 'old'", "-- don't run this: ALTER ROLE app PASSWORD '<redacted>'")]
    [Arguments("SELECT 1; -- was: CONNECTION 'host=x password=old'", "SELECT 1; -- was: CONNECTION 'host=x password=<redacted>'")]
    public async Task APasswordInACommentIsRedactedLikeOneOutsideIt(string sql, string expected)
    {
        await Assert.That(SecretRedactor.Redact(sql)).IsEqualTo(expected);
    }

    [Test]
    [Arguments("SET password_encryption = 'scram-sha-256';")]
    [Arguments("SELECT 'password';")]
    [Arguments("SELECT 'Enter your password:' AS prompt, 'x' AS y;")]
    [Arguments("COMMENT ON COLUMN users.password IS 'bcrypt hash of the password';")]
    [Arguments("SELECT * FROM users WHERE password IS NULL;")]
    [Arguments("DO $$ BEGIN RAISE NOTICE 'password reset done'; END $$;")]
    [Arguments("SELECT B'1010', X'1F';")]
    public async Task OrdinarySqlAroundTheWordIsLeftAlone(string sql)
    {
        await Assert.That(SecretRedactor.ContainsSecret(sql)).IsFalse();
        await Assert.That(SecretRedactor.Redact(sql)).IsEqualTo(sql);
    }

    [Test]
    [Arguments("ALTER ROLE app PASSWORD 'p';")]
    [Arguments("SELECT 'set PASSWORD ''x''';")]
    [Arguments("DO $$ BEGIN EXECUTE format('ALTER ROLE %I PASSWORD %L', 'app', 'p'); END $$;")]
    [Arguments("CREATE SUBSCRIPTION s CONNECTION 'host=a password=''p q''' PUBLICATION p;")]
    [Arguments("-- ALTER ROLE x PASSWORD E'p'")]
    [Arguments("SELECT dblink_connect('postgres://u:p@h/d');")]
    public async Task RedactingTwiceChangesNothingMore(string sql)
    {
        // The history store scrubs on every load, so a redacted entry must
        // read as clean, or the file would be rewritten every time.
        var once = SecretRedactor.Redact(sql);

        await Assert.That(once).IsNotEqualTo(sql);
        await Assert.That(SecretRedactor.Redact(once)).IsEqualTo(once);
        await Assert.That(SecretRedactor.ContainsSecret(once)).IsFalse();
    }

    [Test]
    public async Task DeeplyNestedCommentsFinishAndStillRedact()
    {
        var sql = string.Concat(Enumerable.Repeat("/*", 2000)) + " PASSWORD 'p' " + string.Concat(Enumerable.Repeat("*/", 2000));

        await Assert.That(SecretRedactor.Redact(sql)).Contains("PASSWORD '<redacted>'");
    }

    [Test]
    public async Task EmptyInputIsSafe()
    {
        await Assert.That(SecretRedactor.Redact("")).IsEqualTo("");
        await Assert.That(SecretRedactor.ContainsSecret("")).IsFalse();
    }
}
