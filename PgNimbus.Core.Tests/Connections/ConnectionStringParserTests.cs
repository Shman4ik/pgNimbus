using PgNimbus.Core.Connections;

namespace PgNimbus.Core.Tests.Connections;

/// <summary>
/// The paste box's parser. The URI cases at the top are security audit
/// 2026-09, finding 18: userinfo is split at its last '@' before anything else,
/// so a password pasted unencoded keeps its '/', '?' and '#', and no error ever
/// quotes a parsed value (a port error used to quote a password's first letter).
/// </summary>
public sealed class ConnectionStringParserTests
{
    private static ParsedConnectionString Parse(string text)
    {
        if (!ConnectionStringParser.TryParse(text, out var parsed, out var error))
        {
            throw new InvalidOperationException($"did not parse: {error}");
        }

        return parsed;
    }

    [Test]
    public async Task A_slash_in_an_unencoded_password_stays_in_the_password()
    {
        var parsed = Parse("postgres://admin:1234/abcd@db/app");

        await Assert.That(parsed.Username).IsEqualTo("admin");
        await Assert.That(parsed.Password).IsEqualTo("1234/abcd");
        await Assert.That(parsed.Host).IsEqualTo("db");
        await Assert.That(parsed.Port).IsNull();
        await Assert.That(parsed.Database).IsEqualTo("app");
    }

    [Test]
    public async Task A_hash_in_an_unencoded_password_stays_in_the_password()
    {
        var parsed = Parse("postgres://u:p#ss@h");

        await Assert.That(parsed.Username).IsEqualTo("u");
        await Assert.That(parsed.Password).IsEqualTo("p#ss");
        await Assert.That(parsed.Host).IsEqualTo("h");
        await Assert.That(parsed.Database).IsNull();
    }

    [Test]
    public async Task A_question_mark_and_at_signs_in_an_unencoded_password_stay_in_it()
    {
        var parsed = Parse("postgresql://u:p?w@rd@h:5433/db?sslmode=require");

        await Assert.That(parsed.Password).IsEqualTo("p?w@rd");
        await Assert.That(parsed.Host).IsEqualTo("h");
        await Assert.That(parsed.Port).IsEqualTo(5433);
        await Assert.That(parsed.Database).IsEqualTo("db");
        await Assert.That(parsed.SslMode).IsEqualTo(SslMode.Require);
    }

    [Test]
    public async Task An_at_sign_in_a_query_value_is_not_the_userinfo_end()
    {
        var parsed = Parse("postgres://h/db?application_name=me@laptop");

        await Assert.That(parsed.Username).IsNull();
        await Assert.That(parsed.Host).IsEqualTo("h");
        await Assert.That(parsed.Database).IsEqualTo("db");
    }

    [Test]
    public async Task Userinfo_before_a_query_with_an_at_sign_still_parses()
    {
        var parsed = Parse("postgres://u:s3cret@h/db?application_name=me@laptop");

        await Assert.That(parsed.Username).IsEqualTo("u");
        await Assert.That(parsed.Password).IsEqualTo("s3cret");
        await Assert.That(parsed.Host).IsEqualTo("h");
    }

    [Test]
    public async Task Percent_encoded_userinfo_and_ipv6_hosts_still_parse()
    {
        var parsed = Parse("postgres://us%40er:p%2Fw@[::1]:6543/my%20db");

        await Assert.That(parsed.Username).IsEqualTo("us@er");
        await Assert.That(parsed.Password).IsEqualTo("p/w");
        await Assert.That(parsed.Host).IsEqualTo("::1");
        await Assert.That(parsed.Port).IsEqualTo(6543);
        await Assert.That(parsed.Database).IsEqualTo("my db");
    }

    [Test]
    public async Task A_uri_without_userinfo_or_path_parses()
    {
        var parsed = Parse("postgres://db.example.com:5432");

        await Assert.That(parsed.Host).IsEqualTo("db.example.com");
        await Assert.That(parsed.Port).IsEqualTo(5432);
        await Assert.That(parsed.Username).IsNull();
    }

    [Test]
    [Arguments("postgres://user:hunter2@h:hunter2x/db")]
    [Arguments("host=h port=hunter2")]
    [Arguments("Host=h;Port=hunter2")]
    [Arguments("Host=h;hunter2")]
    [Arguments("host=h hunter2")]
    [Arguments("host=h sslmode=hunter2")]
    [Arguments("host=h password='hunter2")]
    public async Task Errors_never_quote_a_parsed_value(string text)
    {
        var ok = ConnectionStringParser.TryParse(text, out _, out var error);

        await Assert.That(ok).IsFalse();
        await Assert.That(error).IsNotNull();
        await Assert.That(error!).DoesNotContain("hunter2");
    }

    [Test]
    public async Task The_other_dialects_still_parse()
    {
        var jdbc = Parse("jdbc:postgresql://h:5433/db?user=u&password=p");
        await Assert.That((jdbc.Host, jdbc.Port, jdbc.Database, jdbc.Username, jdbc.Password)).IsEqualTo(("h", 5433, "db", "u", "p"));

        var adoNet = Parse("Host=h;Port=5433;Database=db;Username=u;Password='a;b'");
        await Assert.That((adoNet.Host, adoNet.Port, adoNet.Database, adoNet.Username, adoNet.Password)).IsEqualTo(("h", 5433, "db", "u", "a;b"));

        var libpq = Parse("host=h port=5433 dbname=db user=u password='p w'");
        await Assert.That((libpq.Host, libpq.Port, libpq.Database, libpq.Username, libpq.Password)).IsEqualTo(("h", 5433, "db", "u", "p w"));
    }
}
