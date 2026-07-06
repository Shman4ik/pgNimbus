using System.Text;
using Npgsql;

namespace PgNimbus.Core.Connections;

/// <summary>Which syntax <see cref="ConnectionStringParser"/> recognized the input as.</summary>
public enum ConnectionStringFormat
{
    /// <summary>postgres:// or postgresql:// URI (Heroku/Supabase/Neon-style).</summary>
    Uri,

    /// <summary>jdbc:postgresql:... string, as copied from Java tooling.</summary>
    Jdbc,

    /// <summary>Semicolon-separated Key=Value pairs (Npgsql/ADO.NET style).</summary>
    KeyValuePairs,

    /// <summary>Space-separated keyword=value pairs (libpq / psql conninfo style).</summary>
    LibpqKeywords,

    /// <summary>A psql invocation ("psql -h host -U user db"), optionally with env-var prefixes like PGPASSWORD=x.</summary>
    PsqlCommand,
}

/// <summary>
/// The connection fields recoverable from a pasted string. Every field is
/// optional — a paste fills in only what it mentions, so callers should
/// overlay these onto their current values rather than reset the rest.
/// </summary>
public sealed record ParsedConnectionString(
    string? Host = null,
    int? Port = null,
    string? Database = null,
    string? Username = null,
    string? Password = null,
    SslMode? SslMode = null);

/// <summary>
/// Accepts a connection string in any of the syntaxes people actually have
/// on their clipboard — postgres:// URIs, JDBC URLs, ADO.NET/Npgsql
/// Key=Value strings, libpq keyword strings, and full psql command lines —
/// and extracts the profile fields from it.
/// </summary>
public static class ConnectionStringParser
{
    public static bool TryParse(string? input, out ParsedConnectionString parsed, out string? error) =>
        TryParse(input, out parsed, out _, out error);

    public static bool TryParse(string? input, out ParsedConnectionString parsed, out ConnectionStringFormat format, out string? error)
    {
        parsed = new ParsedConnectionString();
        format = ConnectionStringFormat.Uri;
        error = null;

        var text = input?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            error = "Nothing to parse.";
            return false;
        }

        if (text.StartsWith("jdbc:postgresql:", StringComparison.OrdinalIgnoreCase))
        {
            format = ConnectionStringFormat.Jdbc;
            return TryParseJdbc(text, out parsed, out error);
        }

        if (text.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
        {
            format = ConnectionStringFormat.Uri;
            return TryParseUri(text, out parsed, out error);
        }

        if (LooksLikePsqlCommand(text))
        {
            format = ConnectionStringFormat.PsqlCommand;
            return TryParsePsqlCommand(text, out parsed, out error);
        }

        if (text.Contains(';'))
        {
            format = ConnectionStringFormat.KeyValuePairs;
            return TryParseKeyValuePairs(text, out parsed, out error);
        }

        if (text.Contains('='))
        {
            // No semicolons but keyword=value pairs: libpq conninfo syntax.
            // (A lone Key=Value pair parses identically under either dialect,
            // since the keyword aliases are shared.)
            format = ConnectionStringFormat.LibpqKeywords;
            return TryParseLibpqKeywords(text, out parsed, out error);
        }

        error = "Unrecognized connection string. Supported formats: postgres:// URI, jdbc:postgresql://, Key=Value;..., libpq \"host=... dbname=...\", or a psql command line.";
        return false;
    }

    /// <summary>
    /// Converts any recognized syntax to an Npgsql connection string. Input
    /// that is already in Npgsql Key=Value form (or that fails to parse) is
    /// returned unchanged so extra Npgsql-specific options survive.
    /// </summary>
    public static string NormalizeToNpgsql(string input)
    {
        if (!TryParse(input, out var parsed, out var format, out _)
            || format == ConnectionStringFormat.KeyValuePairs)
        {
            return input;
        }

        var builder = new NpgsqlConnectionStringBuilder();
        if (parsed.Host is not null)
        {
            builder.Host = parsed.Host;
        }

        if (parsed.Port is { } port)
        {
            builder.Port = port;
        }

        if (parsed.Database is not null)
        {
            builder.Database = parsed.Database;
        }

        if (parsed.Username is not null)
        {
            builder.Username = parsed.Username;
        }

        if (parsed.Password is not null)
        {
            builder.Password = parsed.Password;
        }

        if (parsed.SslMode is { } sslMode)
        {
            builder.SslMode = sslMode.ToNpgsql();
        }

        return builder.ConnectionString;
    }

    // ---- postgres:// URIs --------------------------------------------------

    private static bool TryParseUri(string text, out ParsedConnectionString parsed, out string? error)
    {
        parsed = new ParsedConnectionString();

        var rest = text[(text.IndexOf("://", StringComparison.Ordinal) + 3)..];

        // Split off ?query (and drop any #fragment) before touching the
        // authority, so '@' or '/' inside parameter values can't confuse it.
        var fragmentIndex = rest.IndexOf('#');
        if (fragmentIndex >= 0)
        {
            rest = rest[..fragmentIndex];
        }

        string query = "";
        var queryIndex = rest.IndexOf('?');
        if (queryIndex >= 0)
        {
            query = rest[(queryIndex + 1)..];
            rest = rest[..queryIndex];
        }

        string path = "";
        var authority = rest;
        var pathIndex = rest.IndexOf('/');
        if (pathIndex >= 0)
        {
            path = rest[(pathIndex + 1)..];
            authority = rest[..pathIndex];
        }

        string? user = null, password = null;
        // Last '@' splits userinfo from host: passwords pasted unencoded often
        // contain '@' themselves, and hosts never do.
        var atIndex = authority.LastIndexOf('@');
        if (atIndex >= 0)
        {
            var userInfo = authority[..atIndex];
            authority = authority[(atIndex + 1)..];
            var colonIndex = userInfo.IndexOf(':');
            user = Decode(colonIndex >= 0 ? userInfo[..colonIndex] : userInfo);
            password = colonIndex >= 0 ? Decode(userInfo[(colonIndex + 1)..]) : null;
        }

        // Multi-host URIs (host1:5432,host2:5432) are valid libpq; take the
        // first endpoint since a GUI profile points at one server.
        var commaIndex = authority.IndexOf(',');
        if (commaIndex >= 0)
        {
            authority = authority[..commaIndex];
        }

        if (!TrySplitHostPort(authority, out var host, out var port, out error))
        {
            return false;
        }

        var fields = new ParserFields
        {
            Host = host,
            Port = port,
            Username = string.IsNullOrEmpty(user) ? null : user,
            Password = string.IsNullOrEmpty(password) ? null : password,
            Database = string.IsNullOrEmpty(path) ? null : Decode(path),
        };

        if (!TryApplyQueryParameters(query, fields, out error))
        {
            return false;
        }

        parsed = fields.ToRecord();
        return true;
    }

    // ---- jdbc:postgresql:... -----------------------------------------------

    private static bool TryParseJdbc(string text, out ParsedConnectionString parsed, out string? error)
    {
        var rest = text["jdbc:".Length..];

        if (rest.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
        {
            // Same shape as a postgres URI (host[:port][/db][?params]).
            return TryParseUri(rest, out parsed, out error);
        }

        // Short forms: jdbc:postgresql:dbname and jdbc:postgresql:?params —
        // host/port implicitly local defaults.
        parsed = new ParsedConnectionString();
        rest = rest["postgresql:".Length..];

        string query = "";
        var queryIndex = rest.IndexOf('?');
        if (queryIndex >= 0)
        {
            query = rest[(queryIndex + 1)..];
            rest = rest[..queryIndex];
        }

        var fields = new ParserFields
        {
            Database = string.IsNullOrEmpty(rest) || rest == "/" ? null : Decode(rest.TrimStart('/')),
        };

        if (!TryApplyQueryParameters(query, fields, out error))
        {
            return false;
        }

        parsed = fields.ToRecord();
        return true;
    }

    // ---- Key=Value;... (Npgsql / ADO.NET) ------------------------------------

    private static bool TryParseKeyValuePairs(string text, out ParsedConnectionString parsed, out string? error)
    {
        parsed = new ParsedConnectionString();
        var fields = new ParserFields();

        foreach (var segment in SplitOutsideQuotes(text, ';'))
        {
            var pair = segment.Trim();
            if (pair.Length == 0)
            {
                continue;
            }

            var equalsIndex = pair.IndexOf('=');
            if (equalsIndex <= 0)
            {
                error = $"Malformed segment \"{pair}\" — expected Key=Value.";
                return false;
            }

            var key = pair[..equalsIndex].Trim();
            var value = pair[(equalsIndex + 1)..].Trim().Trim('\'', '"');

            if (!fields.TrySetByKeyword(key, value, out error))
            {
                return false;
            }
        }

        parsed = fields.ToRecord();
        error = null;
        return true;
    }

    /// <summary>Splits on <paramref name="separator"/>, but not inside '...' or "..." (ADO.NET lets quoted values carry semicolons).</summary>
    private static List<string> SplitOutsideQuotes(string text, char separator)
    {
        var segments = new List<string>();
        var start = 0;
        var quote = '\0';

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quote != '\0')
            {
                if (c == quote)
                {
                    quote = '\0';
                }
            }
            else if (c is '\'' or '"')
            {
                quote = c;
            }
            else if (c == separator)
            {
                segments.Add(text[start..i]);
                start = i + 1;
            }
        }

        segments.Add(text[start..]);
        return segments;
    }

    // ---- libpq keyword=value ------------------------------------------------

    private static bool TryParseLibpqKeywords(string text, out ParsedConnectionString parsed, out string? error)
    {
        parsed = new ParsedConnectionString();
        var fields = new ParserFields();
        var i = 0;

        while (i < text.Length)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                i++;
                continue;
            }

            var equalsIndex = text.IndexOf('=', i);
            if (equalsIndex < 0)
            {
                error = $"Malformed libpq segment near \"{text[i..]}\" — expected keyword=value.";
                return false;
            }

            var key = text[i..equalsIndex].Trim();
            i = equalsIndex + 1;

            // libpq allows whitespace around '=' and single-quoted values with
            // \' and \\ escapes (e.g. password='p a''ss').
            while (i < text.Length && char.IsWhiteSpace(text[i]))
            {
                i++;
            }

            string value;
            if (i < text.Length && text[i] == '\'')
            {
                var builder = new StringBuilder();
                i++;
                var closed = false;
                while (i < text.Length)
                {
                    if (text[i] == '\\' && i + 1 < text.Length)
                    {
                        builder.Append(text[i + 1]);
                        i += 2;
                    }
                    else if (text[i] == '\'')
                    {
                        i++;
                        closed = true;
                        break;
                    }
                    else
                    {
                        builder.Append(text[i]);
                        i++;
                    }
                }

                if (!closed)
                {
                    error = $"Unterminated quoted value for \"{key}\".";
                    return false;
                }

                value = builder.ToString();
            }
            else
            {
                var start = i;
                while (i < text.Length && !char.IsWhiteSpace(text[i]))
                {
                    i++;
                }

                value = text[start..i];
            }

            if (!fields.TrySetByKeyword(key, value, out error))
            {
                return false;
            }
        }

        parsed = fields.ToRecord();
        error = null;
        return true;
    }

    // ---- psql command lines ---------------------------------------------------

    private static bool LooksLikePsqlCommand(string text) =>
        TokenizeCommandLine(text).Any(t => t == "psql" || t.EndsWith("/psql", StringComparison.Ordinal) || t.EndsWith("\\psql.exe", StringComparison.OrdinalIgnoreCase) || t.Equals("psql.exe", StringComparison.OrdinalIgnoreCase));

    private static bool TryParsePsqlCommand(string text, out ParsedConnectionString parsed, out string? error)
    {
        parsed = new ParsedConnectionString();
        error = null;
        var fields = new ParserFields();
        var tokens = TokenizeCommandLine(text);

        var index = 0;

        // Environment-variable prefixes: PGPASSWORD=x PGHOST=y psql ...
        while (index < tokens.Count && !IsPsqlToken(tokens[index]))
        {
            var token = tokens[index];
            var equalsIndex = token.IndexOf('=');
            if (equalsIndex > 0)
            {
                var name = token[..equalsIndex];
                var value = token[(equalsIndex + 1)..];
                var applied = name.ToUpperInvariant() switch
                {
                    "PGHOST" => fields.TrySetByKeyword("host", value, out error),
                    "PGPORT" => fields.TrySetByKeyword("port", value, out error),
                    "PGDATABASE" => fields.TrySetByKeyword("dbname", value, out error),
                    "PGUSER" => fields.TrySetByKeyword("user", value, out error),
                    "PGPASSWORD" => fields.TrySetByKeyword("password", value, out error),
                    "PGSSLMODE" => fields.TrySetByKeyword("sslmode", value, out error),
                    _ => true, // unrelated env var (e.g. LANG=C) — skip
                };

                if (!applied)
                {
                    return false;
                }
            }

            index++;
        }

        if (index >= tokens.Count)
        {
            error = "Could not find the psql executable in the command line.";
            return false;
        }

        index++; // skip "psql" itself
        var positionals = new List<string>();

        while (index < tokens.Count)
        {
            var token = tokens[index++];

            if (!token.StartsWith('-') || token == "-")
            {
                positionals.Add(token);
                continue;
            }

            string? inlineValue = null;
            string flag;
            if (token.StartsWith("--", StringComparison.Ordinal))
            {
                var equalsIndex = token.IndexOf('=');
                flag = equalsIndex >= 0 ? token[..equalsIndex] : token;
                inlineValue = equalsIndex >= 0 ? token[(equalsIndex + 1)..] : null;
            }
            else
            {
                // Short options can glue their value on: -hlocalhost, -p5433.
                flag = token[..2];
                inlineValue = token.Length > 2 ? token[2..] : null;
            }

            var keyword = flag switch
            {
                "-h" or "--host" => "host",
                "-p" or "--port" => "port",
                "-U" or "--username" => "user",
                "-d" or "--dbname" => "dbname",
                _ => null,
            };

            if (keyword is null)
            {
                // Value-less switches (-W, -w, -q, --no-psqlrc...) need nothing;
                // value-carrying options we don't map (-c, -f, -o...) consume
                // their argument so it isn't mistaken for the database name.
                if (inlineValue is null && index < tokens.Count && flag is "-c" or "--command" or "-f" or "--file" or "-o" or "--output" or "-v" or "--set" or "--variable" or "-F" or "-R" or "-P" or "-T" or "-L" or "--log-file")
                {
                    index++;
                }

                continue;
            }

            var value = inlineValue;
            if (value is null)
            {
                if (index >= tokens.Count)
                {
                    error = $"Option {flag} is missing its value.";
                    return false;
                }

                value = tokens[index++];
            }

            if (!fields.TrySetByKeyword(keyword, value, out error))
            {
                return false;
            }
        }

        // psql's positionals are [dbname [username]] — and dbname may itself be
        // a URI or conninfo string, which psql (and we) parse recursively.
        if (positionals.Count > 0)
        {
            var dbArgument = positionals[0];
            if (dbArgument.Contains("://") || dbArgument.Contains('='))
            {
                if (!TryParse(dbArgument, out var inner, out error))
                {
                    return false;
                }

                fields.Overlay(inner);
            }
            else if (!fields.TrySetByKeyword("dbname", dbArgument, out error))
            {
                return false;
            }
        }

        if (positionals.Count > 1 && !fields.TrySetByKeyword("user", positionals[1], out error))
        {
            return false;
        }

        parsed = fields.ToRecord();
        error = null;
        return true;
    }

    private static bool IsPsqlToken(string token) =>
        token == "psql"
        || token.EndsWith("/psql", StringComparison.Ordinal)
        || token.Equals("psql.exe", StringComparison.OrdinalIgnoreCase)
        || token.EndsWith("\\psql.exe", StringComparison.OrdinalIgnoreCase);

    private static List<string> TokenizeCommandLine(string text)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        var quote = '\0';

        foreach (var c in text)
        {
            if (quote != '\0')
            {
                if (c == quote)
                {
                    quote = '\0';
                }
                else
                {
                    current.Append(c);
                }
            }
            else if (c is '"' or '\'')
            {
                quote = c;
            }
            else if (char.IsWhiteSpace(c))
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(c);
            }
        }

        if (current.Length > 0)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }

    // ---- shared helpers ---------------------------------------------------------

    /// <summary>Mutable accumulator the per-format parsers write into via common keyword names.</summary>
    private sealed class ParserFields
    {
        public string? Host;
        public int? Port;
        public string? Database;
        public string? Username;
        public string? Password;
        public SslMode? SslMode;

        public ParsedConnectionString ToRecord() => new(Host, Port, Database, Username, Password, SslMode);

        public void Overlay(ParsedConnectionString other)
        {
            Host = other.Host ?? Host;
            Port = other.Port ?? Port;
            Database = other.Database ?? Database;
            Username = other.Username ?? Username;
            Password = other.Password ?? Password;
            SslMode = other.SslMode ?? SslMode;
        }

        /// <summary>
        /// Applies one keyword across every dialect's aliases: libpq (host,
        /// dbname, user), ADO.NET/Npgsql (Server, Database, User ID, PWD...),
        /// and JDBC parameters (ssl, user, password).
        /// </summary>
        public bool TrySetByKeyword(string key, string value, out string? error)
        {
            error = null;
            switch (Normalize(key))
            {
                case "host" or "server" or "datasource" or "address" or "addr" or "networkaddress" or "hostaddr":
                    Host = value;
                    return true;
                case "port":
                    if (!int.TryParse(value, out var port) || port is < 1 or > 65535)
                    {
                        error = $"Invalid port \"{value}\".";
                        return false;
                    }

                    Port = port;
                    return true;
                case "database" or "dbname" or "db" or "initialcatalog":
                    Database = value;
                    return true;
                case "username" or "user" or "userid" or "uid" or "loginid" or "login":
                    Username = value;
                    return true;
                case "password" or "pwd" or "psw":
                    Password = value;
                    return true;
                case "sslmode":
                    if (!TryParseSslMode(value, out var mode))
                    {
                        error = $"Unknown SSL mode \"{value}\".";
                        return false;
                    }

                    SslMode = mode;
                    return true;
                case "ssl": // JDBC's boolean flavor
                    if (value.Equals("true", StringComparison.OrdinalIgnoreCase) || value == "1")
                    {
                        SslMode = Connections.SslMode.Require;
                    }
                    else if (value.Equals("false", StringComparison.OrdinalIgnoreCase) || value == "0")
                    {
                        SslMode = Connections.SslMode.Disable;
                    }

                    return true;
                default:
                    // Unknown keywords (Timeout, application_name, Pooling,
                    // channel_binding...) are legitimate in their dialects —
                    // ignore rather than reject the whole paste.
                    return true;
            }
        }

        private static string Normalize(string key) =>
            key.Replace(" ", "").Replace("_", "").ToLowerInvariant();
    }

    private static bool TryParseSslMode(string value, out SslMode mode)
    {
        switch (value.Replace("-", "").Replace("_", "").ToLowerInvariant())
        {
            case "disable" or "disabled": mode = SslMode.Disable; return true;
            case "allow": mode = SslMode.Allow; return true;
            case "prefer" or "preferred": mode = SslMode.Prefer; return true;
            case "require" or "required": mode = SslMode.Require; return true;
            case "verifyca": mode = SslMode.VerifyCa; return true;
            case "verifyfull": mode = SslMode.VerifyFull; return true;
            default: mode = SslMode.Prefer; return false;
        }
    }

    private static bool TrySplitHostPort(string authority, out string? host, out int? port, out string? error)
    {
        host = null;
        port = null;
        error = null;

        if (authority.Length == 0)
        {
            return true; // postgres:///dbname — host omitted, defaults apply
        }

        string hostPart;
        string portPart = "";

        if (authority.StartsWith('['))
        {
            // IPv6 literal: [::1]:5433
            var closeIndex = authority.IndexOf(']');
            if (closeIndex < 0)
            {
                error = "Unterminated IPv6 address (missing ']').";
                return false;
            }

            hostPart = authority[1..closeIndex];
            var remainder = authority[(closeIndex + 1)..];
            if (remainder.StartsWith(':'))
            {
                portPart = remainder[1..];
            }
        }
        else
        {
            var colonIndex = authority.LastIndexOf(':');
            if (colonIndex >= 0)
            {
                hostPart = authority[..colonIndex];
                portPart = authority[(colonIndex + 1)..];
            }
            else
            {
                hostPart = authority;
            }
        }

        host = string.IsNullOrEmpty(hostPart) ? null : Decode(hostPart);

        if (portPart.Length > 0)
        {
            if (!int.TryParse(portPart, out var parsedPort) || parsedPort is < 1 or > 65535)
            {
                error = $"Invalid port \"{portPart}\".";
                return false;
            }

            port = parsedPort;
        }

        return true;
    }

    private static bool TryApplyQueryParameters(string query, ParserFields fields, out string? error)
    {
        error = null;
        if (string.IsNullOrEmpty(query))
        {
            return true;
        }

        foreach (var pair in query.Split('&'))
        {
            if (pair.Length == 0)
            {
                continue;
            }

            var equalsIndex = pair.IndexOf('=');
            var key = Decode(equalsIndex >= 0 ? pair[..equalsIndex] : pair);
            var value = equalsIndex >= 0 ? Decode(pair[(equalsIndex + 1)..]) : "";

            if (!fields.TrySetByKeyword(key, value, out error))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Percent-decodes, tolerating '+' for space and raw '%' that isn't a valid escape.</summary>
    private static string Decode(string value)
    {
        value = value.Replace('+', ' ');
        try
        {
            return Uri.UnescapeDataString(value);
        }
        catch (FormatException)
        {
            return value;
        }
    }
}
