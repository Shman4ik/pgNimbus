namespace PgNimbus.Core.Text;

/// <summary>The kind of object a slot of a DDL or utility statement takes.</summary>
public enum SqlObjectKind
{
    /// <summary>Only the advice's keywords.</summary>
    None,
    /// <summary>Any relation: table, view, materialized view, foreign or partitioned table.</summary>
    Relation,
    Table,
    View,
    MaterializedView,
    Index,
    Sequence,
    Function,
    Procedure,
    Schema,
    Type,
    Role,
    /// <summary>An extension that is installed (ALTER, DROP).</summary>
    Extension,
    /// <summary>An extension the server has and the database hasn't installed yet (CREATE EXTENSION).</summary>
    AvailableExtension,
    /// <summary>A server setting (SET, SHOW, RESET).</summary>
    Setting,
    /// <summary>A value of <see cref="SqlCommandAdvice.Setting"/> (SET x TO |).</summary>
    SettingValue,
    /// <summary>A column of <see cref="SqlCommandAdvice.Relation"/>.</summary>
    Column,
    /// <summary>An index access method (CREATE INDEX … USING |).</summary>
    IndexMethod,
    /// <summary>A LISTEN/NOTIFY channel.</summary>
    Channel,
    /// <summary>A procedural language (LANGUAGE |).</summary>
    Language,
}

/// <summary>
/// What a DDL or utility statement takes at the caret: the keywords that fit,
/// in rank order, and the kind of object that does (sql-completion-audit-2.md
/// D01, D02). <see cref="NewName"/> when the caret names something being made.
/// </summary>
public sealed record SqlCommandAdvice(IReadOnlyList<string> Keywords, SqlObjectKind Objects = SqlObjectKind.None)
{
    /// <summary>For <see cref="SqlObjectKind.Column"/>: the relation's dotted name, folded like the server folds it.</summary>
    public IReadOnlyList<string>? Relation { get; init; }

    /// <summary>For <see cref="SqlObjectKind.SettingValue"/>: the setting being set.</summary>
    public string? Setting { get; init; }

    /// <summary>The caret names something new: nothing in the catalog is it.</summary>
    public bool NewName { get; init; }
}

/// <summary>
/// The slot grammar of the statements <see cref="SqlKeywordGrammar"/> leaves
/// alone — CREATE, ALTER, DROP, COMMENT, GRANT/REVOKE, TRUNCATE, VACUUM,
/// ANALYZE, REINDEX, CLUSTER, REFRESH, SET/SHOW/RESET, COPY, LISTEN, LOCK,
/// BEGIN … and EXPLAIN's option list. Each command is a sequence of keywords
/// and typed slots (a relation, a column of that relation, a type, a role, a
/// setting …), read from the tokens left of the word being typed; the caret is
/// always at the end of what is read, so every rule answers "what comes next
/// here". Where it can't tell, it answers null and completion keeps its
/// general list — it never guesses.
/// </summary>
public static class SqlCommandGrammar
{
    // The statements the query grammars read; EXPLAIN only for its "(…)" options.
    private static readonly HashSet<string> QueryCommands = new(StringComparer.Ordinal)
    {
        "select", "with", "insert", "update", "delete", "values", "table", "merge", "call", "do",
    };

    private static readonly string[] CreateKinds =
    [
        "TABLE", "INDEX", "VIEW", "UNIQUE INDEX", "MATERIALIZED VIEW", "FUNCTION", "OR REPLACE FUNCTION", "OR REPLACE VIEW",
        "PROCEDURE", "SCHEMA", "EXTENSION", "TYPE", "DOMAIN", "SEQUENCE", "TRIGGER", "ROLE", "USER", "TEMPORARY TABLE",
        "UNLOGGED TABLE", "DATABASE", "POLICY", "PUBLICATION", "SUBSCRIPTION", "AGGREGATE", "CAST", "COLLATION",
        "OPERATOR", "RULE", "SERVER", "TABLESPACE", "TEXT SEARCH", "FOREIGN TABLE", "EVENT TRIGGER",
    ];

    private static readonly string[] AlterKinds =
    [
        "TABLE", "VIEW", "MATERIALIZED VIEW", "INDEX", "SEQUENCE", "FUNCTION", "PROCEDURE", "SCHEMA", "TYPE", "DOMAIN",
        "ROLE", "USER", "DATABASE", "EXTENSION", "SYSTEM", "DEFAULT PRIVILEGES", "TRIGGER", "POLICY", "PUBLICATION",
        "SUBSCRIPTION", "TABLESPACE", "FOREIGN TABLE",
    ];

    private static readonly string[] DropKinds =
    [
        "TABLE", "VIEW", "INDEX", "MATERIALIZED VIEW", "FUNCTION", "PROCEDURE", "SCHEMA", "SEQUENCE", "TYPE", "DOMAIN",
        "EXTENSION", "TRIGGER", "ROLE", "USER", "DATABASE", "POLICY", "OWNED BY", "PUBLICATION", "SUBSCRIPTION",
        "TABLESPACE", "FOREIGN TABLE", "AGGREGATE", "CAST", "OPERATOR", "RULE", "SERVER",
    ];

    // The kinds a DROP / ALTER / COMMENT ON names, with the objects each takes.
    private static readonly Dictionary<string, SqlObjectKind> ObjectKinds = new(StringComparer.Ordinal)
    {
        ["table"] = SqlObjectKind.Table,
        ["view"] = SqlObjectKind.View,
        ["materialized view"] = SqlObjectKind.MaterializedView,
        ["foreign table"] = SqlObjectKind.Table,
        ["index"] = SqlObjectKind.Index,
        ["sequence"] = SqlObjectKind.Sequence,
        ["function"] = SqlObjectKind.Function,
        ["procedure"] = SqlObjectKind.Procedure,
        ["aggregate"] = SqlObjectKind.Function,
        ["routine"] = SqlObjectKind.Function,
        ["schema"] = SqlObjectKind.Schema,
        ["type"] = SqlObjectKind.Type,
        ["domain"] = SqlObjectKind.Type,
        ["extension"] = SqlObjectKind.Extension,
        ["role"] = SqlObjectKind.Role,
        ["user"] = SqlObjectKind.Role,
        ["group"] = SqlObjectKind.Role,
        ["column"] = SqlObjectKind.Relation,
    };

    private static readonly string[] AlterTableActions =
    [
        "ADD COLUMN", "DROP COLUMN", "ALTER COLUMN", "RENAME COLUMN", "RENAME TO", "ADD CONSTRAINT", "DROP CONSTRAINT",
        "OWNER TO", "SET SCHEMA", "ADD", "DROP", "ALTER", "RENAME", "SET", "RESET", "ENABLE TRIGGER", "DISABLE TRIGGER",
        "ENABLE ROW LEVEL SECURITY", "DISABLE ROW LEVEL SECURITY", "ATTACH PARTITION", "DETACH PARTITION",
        "VALIDATE CONSTRAINT", "CLUSTER ON", "INHERIT", "NO INHERIT",
    ];

    private static readonly string[] ColumnConstraints =
    [
        "NOT NULL", "PRIMARY KEY", "REFERENCES", "DEFAULT", "UNIQUE", "CHECK", "GENERATED ALWAYS AS IDENTITY",
        "GENERATED BY DEFAULT AS IDENTITY", "GENERATED ALWAYS AS", "NULL", "CONSTRAINT", "COLLATE",
    ];

    private static readonly string[] TableConstraintStarts = ["CONSTRAINT", "PRIMARY KEY", "FOREIGN KEY", "UNIQUE", "CHECK", "LIKE", "EXCLUDE"];

    private static readonly HashSet<string> TableConstraintWords = new(StringComparer.Ordinal)
    {
        "constraint", "primary", "foreign", "unique", "check", "like", "exclude",
    };

    private static readonly string[] ReferentialActions = ["CASCADE", "RESTRICT", "SET NULL", "SET DEFAULT", "NO ACTION"];

    private static readonly string[] Privileges =
    [
        "SELECT", "INSERT", "UPDATE", "DELETE", "ALL PRIVILEGES", "ALL", "TRUNCATE", "REFERENCES", "TRIGGER", "USAGE",
        "EXECUTE", "CREATE", "CONNECT", "TEMPORARY", "MAINTAIN",
    ];

    private static readonly string[] GrantTargets =
    [
        "TABLE", "ALL TABLES IN SCHEMA", "SCHEMA", "SEQUENCE", "ALL SEQUENCES IN SCHEMA", "FUNCTION",
        "ALL FUNCTIONS IN SCHEMA", "PROCEDURE", "ALL PROCEDURES IN SCHEMA", "DATABASE", "TYPE", "DOMAIN",
    ];

    private static readonly string[] ExplainOptions =
    [
        "ANALYZE", "BUFFERS", "VERBOSE", "COSTS", "SETTINGS", "WAL", "TIMING", "SUMMARY", "FORMAT", "GENERIC_PLAN",
        "SERIALIZE", "MEMORY",
    ];

    private static readonly string[] VacuumOptions =
    [
        "ANALYZE", "VERBOSE", "FULL", "FREEZE", "DISABLE_PAGE_SKIPPING", "SKIP_LOCKED", "INDEX_CLEANUP", "PROCESS_MAIN",
        "PROCESS_TOAST", "TRUNCATE", "PARALLEL", "BUFFER_USAGE_LIMIT",
    ];

    private static readonly string[] CopyOptions =
    [
        "FORMAT", "HEADER", "DELIMITER", "NULL", "QUOTE", "ESCAPE", "ENCODING", "FREEZE", "FORCE_QUOTE", "FORCE_NOT_NULL",
        "FORCE_NULL", "DEFAULT", "ON_ERROR",
    ];

    private static readonly string[] LockModes =
    [
        "ACCESS EXCLUSIVE MODE", "ACCESS SHARE MODE", "ROW SHARE MODE", "ROW EXCLUSIVE MODE", "SHARE UPDATE EXCLUSIVE MODE",
        "SHARE MODE", "SHARE ROW EXCLUSIVE MODE", "EXCLUSIVE MODE",
    ];

    /// <summary>
    /// The advice for the word at <paramref name="caret"/> in
    /// <paramref name="statement"/> (the text of one statement); null when the
    /// statement isn't one this grammar reads, or it can't tell.
    /// </summary>
    public static SqlCommandAdvice? At(string statement, int caret)
    {
        caret = Math.Clamp(caret, 0, statement.Length);
        var wordStart = caret;
        while (wordStart > 0 && SqlLexer.IsIdentPart(statement[wordStart - 1]))
        {
            wordStart--;
        }

        var tokens = new List<SqlToken>();
        foreach (var token in SqlLexer.Tokenize(statement, 0, wordStart))
        {
            if (token.IsProse && (token.IsIncomplete || token.Kind == SqlTokenKind.LineComment))
            {
                return null; // inside a string or a comment
            }

            if (!token.IsTrivia)
            {
                tokens.Add(token);
            }
        }

        // A qualified name being typed ("ALTER TABLE saas.|"): member completion's.
        if (tokens.Count == 0 || tokens[^1].Kind is SqlTokenKind.Dot or SqlTokenKind.DoubleColon
            || tokens[^1] is { Kind: SqlTokenKind.QuotedIdentifier, IsIncomplete: true })
        {
            return null;
        }

        var c = new Cursor(statement, tokens);
        var first = c.Word(0);
        if (first is null || (QueryCommands.Contains(first)))
        {
            return null;
        }

        return first switch
        {
            "create" => Create(c),
            "alter" => Alter(c),
            "drop" => Drop(c),
            "comment" => Comment(c),
            "grant" => Grant(c, "to"),
            "revoke" => Grant(c, "from"),
            "truncate" => Truncate(c),
            "vacuum" => Vacuum(c),
            "analyze" or "analyse" => Analyze(c),
            "reindex" => Reindex(c),
            "cluster" => Cluster(c),
            "refresh" => Refresh(c),
            "set" => Set(c),
            "reset" => Reset(c),
            "show" => Show(c),
            "explain" => Explain(c),
            "copy" => Copy(c),
            "listen" or "notify" or "unlisten" => Listen(c, first),
            "lock" => Lock(c),
            "begin" or "start" => Begin(c, first),
            "discard" => c.Count == 1 ? Keywords("ALL", "PLANS", "SEQUENCES", "TEMP") : null,
            "reassign" => Reassign(c),
            "commit" or "rollback" or "end" or "abort" => c.Count == 1 ? Keywords("AND CHAIN", "TO SAVEPOINT") : null,
            _ => null,
        };
    }

    private static SqlCommandAdvice Keywords(params string[] keywords) => new(keywords);

    private static SqlCommandAdvice Objects(SqlObjectKind kind, params string[] keywords) => new(keywords, kind);

    private static SqlCommandAdvice NewName(params string[] keywords) => new(keywords) { NewName = true };

    // The name of an object being created: new, but its schema part is one
    // that exists ("CREATE TABLE saas.|").
    private static SqlCommandAdvice CreatedName(params string[] keywords) => new(keywords, SqlObjectKind.Schema) { NewName = true };

    // --- CREATE ---

    private static SqlCommandAdvice? Create(Cursor c)
    {
        var i = 1;
        if (c.AtEnd(i))
        {
            return Keywords(CreateKinds);
        }

        if (c.Is(i, "or"))
        {
            if (c.AtEnd(i + 1))
            {
                return Keywords("REPLACE");
            }

            if (!c.Is(i + 1, "replace"))
            {
                return null;
            }

            i += 2;
            if (c.AtEnd(i))
            {
                return Keywords("FUNCTION", "VIEW", "PROCEDURE", "TRIGGER", "RULE", "AGGREGATE", "TEMPORARY VIEW");
            }
        }

        while (c.Word(i) is "temp" or "temporary" or "unlogged" or "global" or "local" or "recursive")
        {
            i++;
            if (c.AtEnd(i))
            {
                return Keywords("TABLE", "VIEW", "SEQUENCE");
            }
        }

        if (c.Is(i, "unique"))
        {
            i++;
            if (c.AtEnd(i))
            {
                return Keywords("INDEX");
            }
        }

        if (c.Is(i, "materialized"))
        {
            i++;
            if (c.AtEnd(i))
            {
                return Keywords("VIEW");
            }

            return c.Is(i, "view") ? CreateView(c, i + 1) : null;
        }

        return c.Word(i) switch
        {
            "table" => CreateTable(c, i + 1),
            "index" => CreateIndex(c, i + 1),
            "view" => CreateView(c, i + 1),
            "function" or "procedure" => CreateFunction(c, i + 1),
            "schema" => CreateSchema(c, i + 1),
            "extension" => CreateExtension(c, i + 1),
            "sequence" => CreateSequence(c, i + 1),
            "domain" or "type" => CreateType(c, i + 1, c.Word(i)!),
            "trigger" => CreateTrigger(c, i + 1),
            "role" or "user" or "group" => CreateRole(c, i + 1),
            _ => null,
        };
    }

    private static SqlCommandAdvice? CreateTable(Cursor c, int i)
    {
        if (c.IfNotExists(ref i) is { } partial)
        {
            return partial;
        }

        if (c.AtEnd(i))
        {
            return CreatedName("IF NOT EXISTS");
        }

        if (!c.Name(ref i, out _))
        {
            return null;
        }

        if (c.AtEnd(i))
        {
            return Keywords("AS", "PARTITION OF", "OF");
        }

        if (c.Is(i, "partition") && c.Is(i + 1, "of"))
        {
            return c.AtEnd(i + 2) ? Objects(SqlObjectKind.Table) : null;
        }

        return c.Is(i, SqlTokenKind.OpenParen) && c.InsideFrom(i) ? ColumnDefinitions(c, i + 1) : null;
    }

    // Inside CREATE TABLE t ( … : the element being written after the last
    // top-level comma — a column (name, type, constraints) or a table constraint.
    private static SqlCommandAdvice? ColumnDefinitions(Cursor c, int start)
    {
        var elementStart = c.LastTopLevelComma(start) is { } comma ? comma + 1 : start;
        return ColumnDefinition(c, elementStart, allowTableConstraint: true);
    }

    // One column definition from token `s` to the caret: "name", "name type",
    // "name type constraint …". With allowTableConstraint, a table constraint.
    private static SqlCommandAdvice? ColumnDefinition(Cursor c, int s, bool allowTableConstraint)
    {
        if (c.AtEnd(s))
        {
            return allowTableConstraint ? NewName(TableConstraintStarts) : NewName();
        }

        if (allowTableConstraint && c.Word(s) is { } word && TableConstraintWords.Contains(word))
        {
            return TableConstraint(c, s);
        }

        // The column's name; then its type.
        var i = s + 1;
        if (c.AtEnd(i))
        {
            return Objects(SqlObjectKind.Type);
        }

        // A multi-word type still being written.
        switch (c.Word(c.Count - 1))
        {
            case "double" when c.Count - 1 == i:
                return Keywords("PRECISION");
            case "character" or "bit" when c.Count - 1 == i:
                return Keywords("VARYING");
            case "timestamp" or "time" when c.Count - 1 == i:
                return Keywords([.. new[] { "WITH TIME ZONE", "WITHOUT TIME ZONE" }, .. ColumnConstraints]);
        }

        return ConstraintTail(c, i);
    }

    // After a column's type: its constraints, whichever one is being written.
    private static SqlCommandAdvice? ConstraintTail(Cursor c, int from)
    {
        var last = c.Count - 1;
        if (c.OpenParen(from) is { } open)
        {
            // "REFERENCES t (|": the referenced table's columns; any other
            // parentheses (numeric(10, |), CHECK (…), DEFAULT f(…)) aren't this grammar's.
            return c.ReferencesRelationEndingAt(from, open) is { } referenced
                ? new SqlCommandAdvice([], SqlObjectKind.Column) { Relation = referenced }
                : null;
        }

        return c.Word(last) switch
        {
            "references" => Objects(SqlObjectKind.Table),
            "not" => Keywords("NULL", "DEFERRABLE"),
            "primary" or "foreign" => Keywords("KEY"),
            "generated" => Keywords("ALWAYS AS IDENTITY", "BY DEFAULT AS IDENTITY", "ALWAYS AS"),
            "delete" or "update" when c.Is(last - 1, "on") => Keywords(ReferentialActions),
            "on" => Keywords("DELETE", "UPDATE"),
            "default" or "collate" or "check" or "constraint" or "as" => null,
            // "REFERENCES t |", "REFERENCES t (id) |": its actions, or the next constraint.
            _ when c.ReferencesRelationEndingAt(from, c.Count) is not null =>
                Keywords([.. new[] { "ON DELETE", "ON UPDATE" }, .. ColumnConstraints]),
            _ => Keywords(ColumnConstraints),
        };
    }

    private static SqlCommandAdvice? TableConstraint(Cursor c, int s)
    {
        var last = c.Count - 1;
        if (c.OpenParen(s) is { } open)
        {
            return c.ReferencesRelationEndingAt(s, open) is { } referenced
                ? new SqlCommandAdvice([], SqlObjectKind.Column) { Relation = referenced }
                : null; // the key's own columns: named in this very statement
        }

        return c.Word(last) switch
        {
            "constraint" => NewName(),
            "primary" or "foreign" => Keywords("KEY"),
            "references" => Objects(SqlObjectKind.Table),
            "on" => Keywords("DELETE", "UPDATE"),
            "delete" or "update" when c.Is(last - 1, "on") => Keywords(ReferentialActions),
            _ when c.Word(s) == "constraint" && last == s + 1 => Keywords("PRIMARY KEY", "FOREIGN KEY", "UNIQUE", "CHECK", "EXCLUDE"),
            _ when c.ReferencesRelationEndingAt(s, c.Count) is not null => Keywords("ON DELETE", "ON UPDATE", "MATCH FULL", "DEFERRABLE"),
            _ when c.Word(s) is "foreign" or "constraint" && c.Is(last, SqlTokenKind.CloseParen) => Keywords("REFERENCES"),
            _ => null,
        };
    }

    private static SqlCommandAdvice? CreateIndex(Cursor c, int i)
    {
        if (c.Is(i, "concurrently"))
        {
            i++;
        }

        if (c.IfNotExists(ref i) is { } partial)
        {
            return partial;
        }

        if (c.AtEnd(i))
        {
            return NewName("ON", "CONCURRENTLY", "IF NOT EXISTS");
        }

        if (!c.Is(i, "on"))
        {
            if (!c.Name(ref i, out _))
            {
                return null;
            }

            if (c.AtEnd(i))
            {
                return Keywords("ON");
            }

            if (!c.Is(i, "on"))
            {
                return null;
            }
        }

        i++;
        if (c.Is(i, "only"))
        {
            i++;
        }

        if (c.AtEnd(i))
        {
            return Objects(SqlObjectKind.Relation, "ONLY");
        }

        var relationStart = i;
        if (!c.Name(ref i, out var relation))
        {
            return null;
        }

        if (c.AtEnd(i))
        {
            return Keywords("USING");
        }

        if (c.Is(i, "using"))
        {
            i++;
            if (c.AtEnd(i))
            {
                return Objects(SqlObjectKind.IndexMethod);
            }

            i++;
        }

        if (c.Is(i, SqlTokenKind.OpenParen))
        {
            if (c.InsideFrom(i))
            {
                // The columns: after "(" or a comma, a column; after one, its options.
                return c.Is(c.Count - 1, SqlTokenKind.OpenParen) || c.Is(c.Count - 1, SqlTokenKind.Comma)
                    ? new SqlCommandAdvice([], SqlObjectKind.Column) { Relation = relation }
                    : Keywords("ASC", "DESC", "NULLS FIRST", "NULLS LAST");
            }

            return Keywords("INCLUDE", "WHERE", "WITH", "TABLESPACE", "NULLS NOT DISTINCT");
        }

        _ = relationStart;
        return null;
    }

    private static SqlCommandAdvice? CreateView(Cursor c, int i)
    {
        if (c.IfNotExists(ref i) is { } partial)
        {
            return partial;
        }

        if (c.AtEnd(i))
        {
            return CreatedName("IF NOT EXISTS");
        }

        if (!c.Name(ref i, out _))
        {
            return null;
        }

        if (c.AtEnd(i))
        {
            return Keywords("AS", "WITH");
        }

        return c.Is(i, "as") && c.AtEnd(i + 1) ? Keywords("SELECT", "WITH", "VALUES", "TABLE") : null;
    }

    private static SqlCommandAdvice? CreateFunction(Cursor c, int i)
    {
        if (c.AtEnd(i))
        {
            return CreatedName();
        }

        if (!c.Name(ref i, out _))
        {
            return null;
        }

        if (c.AtEnd(i) || !c.Is(i, SqlTokenKind.OpenParen))
        {
            return null;
        }

        if (c.InsideFrom(i))
        {
            // An argument: "name type [DEFAULT …]", its mode first when it has one.
            var s = c.LastTopLevelComma(i + 1) is { } comma ? comma + 1 : i + 1;
            if (c.Word(s) is "in" or "out" or "inout" or "variadic")
            {
                s++;
            }

            return c.AtEnd(s) ? NewName("IN", "OUT", "INOUT", "VARIADIC")
                : c.AtEnd(s + 1) ? Objects(SqlObjectKind.Type)
                : c.AtEnd(s + 2) ? Keywords("DEFAULT")
                : null;
        }

        var last = c.Word(c.Count - 1);
        return last switch
        {
            "returns" => Objects(SqlObjectKind.Type, "TABLE", "SETOF", "TRIGGER", "VOID"),
            "setof" => Objects(SqlObjectKind.Type),
            "language" => Objects(SqlObjectKind.Language),
            "security" => Keywords("DEFINER", "INVOKER"),
            "parallel" => Keywords("SAFE", "RESTRICTED", "UNSAFE"),
            "as" or "table" => null,
            _ when c.Is(c.Count - 1, SqlTokenKind.CloseParen) || c.Word(c.Count - 2) is "returns" or "language" or "setof" =>
                Keywords("RETURNS", "LANGUAGE", "AS", "IMMUTABLE", "STABLE", "VOLATILE", "STRICT", "SECURITY DEFINER", "PARALLEL SAFE",
                    "SET", "COST", "ROWS", "BEGIN ATOMIC"),
            _ => null,
        };
    }

    private static SqlCommandAdvice? CreateSchema(Cursor c, int i)
    {
        if (c.IfNotExists(ref i) is { } partial)
        {
            return partial;
        }

        if (c.AtEnd(i))
        {
            return NewName("IF NOT EXISTS", "AUTHORIZATION");
        }

        if (c.Is(i, "authorization"))
        {
            return c.AtEnd(i + 1) ? Objects(SqlObjectKind.Role) : null;
        }

        if (!c.Name(ref i, out _))
        {
            return null;
        }

        return c.AtEnd(i) ? Keywords("AUTHORIZATION")
            : c.Is(i, "authorization") && c.AtEnd(i + 1) ? Objects(SqlObjectKind.Role)
            : null;
    }

    private static SqlCommandAdvice? CreateExtension(Cursor c, int i)
    {
        if (c.IfNotExists(ref i) is { } partial)
        {
            return partial;
        }

        if (c.AtEnd(i))
        {
            return Objects(SqlObjectKind.AvailableExtension, "IF NOT EXISTS");
        }

        if (!c.Name(ref i, out _))
        {
            return null;
        }

        if (c.Is(i, "with"))
        {
            i++;
        }

        return c.AtEnd(i) ? Keywords("SCHEMA", "VERSION", "CASCADE", "WITH SCHEMA")
            : c.Word(c.Count - 1) == "schema" ? Objects(SqlObjectKind.Schema)
            : null;
    }

    private static SqlCommandAdvice? CreateSequence(Cursor c, int i)
    {
        if (c.IfNotExists(ref i) is { } partial)
        {
            return partial;
        }

        if (c.AtEnd(i))
        {
            return CreatedName("IF NOT EXISTS");
        }

        if (!c.Name(ref i, out _))
        {
            return null;
        }

        return c.Word(c.Count - 1) switch
        {
            "as" => Objects(SqlObjectKind.Type),
            "by" when c.Word(c.Count - 2) == "owned" => Objects(SqlObjectKind.Table, "NONE"),
            _ when c.AtEnd(i) || c.Is(c.Count - 1, SqlTokenKind.Number) =>
                Keywords("AS", "INCREMENT BY", "START WITH", "MINVALUE", "MAXVALUE", "CACHE", "CYCLE", "NO CYCLE", "OWNED BY"),
            _ => null,
        };
    }

    private static SqlCommandAdvice? CreateType(Cursor c, int i, string kind)
    {
        if (c.AtEnd(i))
        {
            return CreatedName();
        }

        if (!c.Name(ref i, out _))
        {
            return null;
        }

        if (c.AtEnd(i))
        {
            return Keywords("AS");
        }

        return c.Is(i, "as") && c.AtEnd(i + 1)
            ? kind == "domain" ? Objects(SqlObjectKind.Type) : Keywords("ENUM", "RANGE")
            : null;
    }

    private static SqlCommandAdvice? CreateTrigger(Cursor c, int i)
    {
        if (c.AtEnd(i))
        {
            return NewName();
        }

        var last = c.Word(c.Count - 1);
        return last switch
        {
            _ when c.AtEnd(i + 1) => Keywords("BEFORE", "AFTER", "INSTEAD OF"),
            "before" or "after" or "of" or "or" => Keywords("INSERT", "UPDATE", "DELETE", "TRUNCATE"),
            "on" => Objects(SqlObjectKind.Table),
            "execute" => Keywords("FUNCTION", "PROCEDURE"),
            "function" or "procedure" when c.Word(c.Count - 2) == "execute" => Objects(SqlObjectKind.Function),
            "for" => Keywords("EACH ROW", "EACH STATEMENT"),
            "insert" or "update" or "delete" or "truncate" => Keywords("ON", "OR", "OF"),
            _ when c.IndexOf(i, "on") is { } on && on == c.Count - 2 => Keywords("FOR EACH ROW", "FOR EACH STATEMENT", "WHEN", "EXECUTE FUNCTION", "REFERENCING"),
            "row" or "statement" => Keywords("WHEN", "EXECUTE FUNCTION"),
            _ => null,
        };
    }

    private static SqlCommandAdvice? CreateRole(Cursor c, int i)
    {
        if (c.AtEnd(i))
        {
            return NewName();
        }

        return c.Word(c.Count - 1) switch
        {
            "role" when c.Word(c.Count - 2) == "in" => Objects(SqlObjectKind.Role),
            "in" => Keywords("ROLE"),
            _ => Keywords("WITH", "LOGIN", "NOLOGIN", "PASSWORD", "SUPERUSER", "CREATEDB", "CREATEROLE", "INHERIT",
                "NOINHERIT", "REPLICATION", "BYPASSRLS", "CONNECTION LIMIT", "VALID UNTIL", "IN ROLE"),
        };
    }

    // --- ALTER ---

    private static SqlCommandAdvice? Alter(Cursor c)
    {
        var i = 1;
        if (c.AtEnd(i))
        {
            return Keywords(AlterKinds);
        }

        var kind = c.Word(i);
        i++;
        if (kind is "materialized" or "foreign" or "default")
        {
            if (c.AtEnd(i))
            {
                return Keywords(kind == "default" ? "PRIVILEGES" : kind == "foreign" ? "TABLE" : "VIEW");
            }

            i++;
        }

        switch (kind)
        {
            case "table" or "foreign":
                return AlterTable(c, i);
            case "system":
                return c.AtEnd(i) ? Keywords("SET", "RESET")
                    : c.AtEnd(i + 1) ? Objects(SqlObjectKind.Setting, "ALL")
                    : SettingTail(c, i + 1);
            case "default":
                return null;
        }

        var objects = kind switch
        {
            "materialized" => SqlObjectKind.MaterializedView,
            "view" => SqlObjectKind.View,
            "index" => SqlObjectKind.Index,
            "sequence" => SqlObjectKind.Sequence,
            "function" or "procedure" or "routine" => SqlObjectKind.Function,
            "schema" => SqlObjectKind.Schema,
            "type" or "domain" => SqlObjectKind.Type,
            "role" or "user" or "group" => SqlObjectKind.Role,
            "extension" => SqlObjectKind.Extension,
            _ => SqlObjectKind.None,
        };
        if (objects == SqlObjectKind.None)
        {
            return null;
        }

        if (c.IfExists(ref i) is { } partial)
        {
            return partial with { Objects = objects };
        }

        if (c.AtEnd(i))
        {
            return Objects(objects, "IF EXISTS");
        }

        if (!c.Name(ref i, out _))
        {
            return null;
        }

        var generic = objects is SqlObjectKind.Role
            ? new[] { "WITH", "RENAME TO", "SET", "RESET", "LOGIN", "NOLOGIN", "PASSWORD", "SUPERUSER", "CREATEDB", "CREATEROLE", "VALID UNTIL" }
            : objects is SqlObjectKind.Extension ? new[] { "UPDATE", "UPDATE TO", "SET SCHEMA", "ADD", "DROP" }
            : ["RENAME TO", "OWNER TO", "SET SCHEMA", "SET", "RESET", "RENAME COLUMN"];
        return c.Word(c.Count - 1) switch
        {
            _ when c.AtEnd(i) => Keywords(generic),
            "to" when c.Word(c.Count - 2) == "owner" => Objects(SqlObjectKind.Role, "CURRENT_USER", "SESSION_USER"),
            "to" when c.Word(c.Count - 2) == "rename" => NewName(),
            "schema" when c.Word(c.Count - 2) == "set" => Objects(SqlObjectKind.Schema),
            "owner" => Keywords("TO"),
            "rename" => Keywords("TO"),
            _ => null,
        };
    }

    private static SqlCommandAdvice? AlterTable(Cursor c, int i)
    {
        if (c.IfExists(ref i) is { } partial)
        {
            return partial with { Objects = SqlObjectKind.Table };
        }

        if (c.Is(i, "only"))
        {
            i++;
        }

        if (c.AtEnd(i))
        {
            return Objects(SqlObjectKind.Table, "IF EXISTS", "ONLY");
        }

        if (!c.Name(ref i, out var relation))
        {
            return null;
        }

        if (c.Is(i, SqlTokenKind.Operator) && c.Text(i) == "*")
        {
            i++;
        }

        // The action being written: after the last top-level comma.
        var s = c.LastTopLevelComma(i) is { } comma ? comma + 1 : i;
        if (c.AtEnd(s))
        {
            return Keywords(AlterTableActions);
        }

        var columns = new SqlCommandAdvice([], SqlObjectKind.Column) { Relation = relation };
        var action = c.Word(s);
        var n = c.Count - s; // words of the action written so far
        switch (action)
        {
            case "add":
                if (n == 1)
                {
                    return NewName("COLUMN", "CONSTRAINT", "PRIMARY KEY", "FOREIGN KEY", "UNIQUE", "CHECK", "COLUMN IF NOT EXISTS");
                }

                if (c.Word(s + 1) is { } second && TableConstraintWords.Contains(second))
                {
                    return TableConstraint(c, s + 1) is { Objects: SqlObjectKind.Column, Relation: null } ? null : TableConstraint(c, s + 1);
                }

                var definition = s + 1;
                if (c.Is(definition, "column"))
                {
                    definition++;
                    if (c.IfNotExists(ref definition) is { } notExists)
                    {
                        return notExists with { NewName = true };
                    }
                }

                return ColumnDefinition(c, definition, allowTableConstraint: false);

            case "drop":
                if (n == 1)
                {
                    return columns with { Keywords = ["COLUMN", "CONSTRAINT", "COLUMN IF EXISTS"] };
                }

                if (c.Is(s + 1, "constraint"))
                {
                    return n == 2 ? Keywords("IF EXISTS") : null;
                }

                var dropped = c.Is(s + 1, "column") ? s + 2 : s + 1;
                if (c.Is(dropped, "if") && c.Is(dropped + 1, "exists"))
                {
                    dropped += 2;
                }
                else if (c.Is(dropped, "if"))
                {
                    return c.AtEnd(dropped + 1) ? Keywords("EXISTS") : null;
                }

                return c.AtEnd(dropped) ? columns with { Keywords = dropped == s + 2 ? ["IF EXISTS"] : [] }
                    : c.AtEnd(dropped + 1) ? Keywords("CASCADE", "RESTRICT")
                    : null;

            case "alter":
                if (n == 1)
                {
                    return columns with { Keywords = ["COLUMN", "CONSTRAINT"] };
                }

                var altered = c.Is(s + 1, "column") ? s + 2 : s + 1;
                if (c.AtEnd(altered))
                {
                    return columns;
                }

                return c.Word(c.Count - 1) switch
                {
                    _ when c.AtEnd(altered + 1) => Keywords("TYPE", "SET NOT NULL", "DROP NOT NULL", "SET DEFAULT", "DROP DEFAULT",
                        "SET DATA TYPE", "ADD GENERATED ALWAYS AS IDENTITY", "DROP IDENTITY", "SET STATISTICS", "SET STORAGE"),
                    "type" => Objects(SqlObjectKind.Type),
                    "set" => Keywords("NOT NULL", "DEFAULT", "DATA TYPE", "STATISTICS", "STORAGE"),
                    "drop" => Keywords("NOT NULL", "DEFAULT", "IDENTITY", "EXPRESSION"),
                    "not" => Keywords("NULL"),
                    "data" => Keywords("TYPE"),
                    _ => null,
                };

            case "rename":
                if (n == 1)
                {
                    return columns with { Keywords = ["COLUMN", "TO", "CONSTRAINT"] };
                }

                if (c.Is(s + 1, "to"))
                {
                    return n == 2 ? NewName() : null;
                }

                var renamed = c.Is(s + 1, "column") || c.Is(s + 1, "constraint") ? s + 2 : s + 1;
                return c.AtEnd(renamed) ? (c.Is(s + 1, "constraint") ? null : columns)
                    : c.AtEnd(renamed + 1) ? Keywords("TO")
                    : c.AtEnd(renamed + 2) && c.Is(renamed + 1, "to") ? NewName()
                    : null;

            case "owner":
                return n == 1 ? Keywords("TO")
                    : n == 2 ? Objects(SqlObjectKind.Role, "CURRENT_USER", "SESSION_USER")
                    : null;

            case "set":
                return n == 1 ? Keywords("SCHEMA", "TABLESPACE", "LOGGED", "UNLOGGED", "WITHOUT CLUSTER", "ACCESS METHOD")
                    : n == 2 && c.Is(s + 1, "schema") ? Objects(SqlObjectKind.Schema)
                    : null;

            case "enable" or "disable":
                return n == 1 ? Keywords("TRIGGER", "ROW LEVEL SECURITY", "RULE", "TRIGGER ALL", "TRIGGER USER")
                    : n == 2 && c.Is(s + 1, "trigger") ? Keywords("ALL", "USER")
                    : null;

            case "attach" or "detach":
                return n == 1 ? Keywords("PARTITION")
                    : n == 2 ? Objects(SqlObjectKind.Table)
                    : n == 3 && action == "attach" ? Keywords("FOR VALUES", "DEFAULT")
                    : null;

            case "validate":
                return n == 1 ? Keywords("CONSTRAINT") : null;
        }

        return null;
    }

    // --- DROP ---

    private static SqlCommandAdvice? Drop(Cursor c)
    {
        var i = 1;
        if (c.AtEnd(i))
        {
            return Keywords(DropKinds);
        }

        var kind = c.Word(i);
        i++;
        if (kind is "materialized" or "foreign")
        {
            if (c.AtEnd(i))
            {
                return Keywords(kind == "foreign" ? "TABLE" : "VIEW");
            }

            kind = kind == "foreign" ? "foreign table" : "materialized view";
            i++;
        }

        if (kind == "owned")
        {
            return c.AtEnd(i) ? Keywords("BY") : c.AtEnd(i + 1) ? Objects(SqlObjectKind.Role, "CURRENT_USER") : Keywords("CASCADE", "RESTRICT");
        }

        if (kind is null || !ObjectKinds.TryGetValue(kind, out var objects) || kind == "column")
        {
            return null;
        }

        if (kind == "index" && c.Is(i, "concurrently"))
        {
            i++;
        }

        if (c.IfExists(ref i) is { } partial)
        {
            return partial with { Objects = objects };
        }

        if (c.AtEnd(i))
        {
            return Objects(objects, "IF EXISTS");
        }

        // One or more names: after a comma another, after a name the options.
        return c.Is(c.Count - 1, SqlTokenKind.Comma) ? Objects(objects)
            : c.Is(c.Count - 1, SqlTokenKind.Word, SqlTokenKind.QuotedIdentifier, SqlTokenKind.CloseParen) && c.Word(c.Count - 1) is not ("cascade" or "restrict")
                ? Keywords("CASCADE", "RESTRICT")
                : null;
    }

    // --- COMMENT ON ---

    private static SqlCommandAdvice? Comment(Cursor c)
    {
        if (c.AtEnd(1))
        {
            return Keywords("ON");
        }

        if (!c.Is(1, "on"))
        {
            return null;
        }

        if (c.AtEnd(2))
        {
            return Keywords("TABLE", "COLUMN", "VIEW", "MATERIALIZED VIEW", "FUNCTION", "SCHEMA", "INDEX", "SEQUENCE", "TYPE",
                "EXTENSION", "ROLE", "DATABASE", "CONSTRAINT", "TRIGGER", "PROCEDURE", "DOMAIN");
        }

        var i = 3;
        var kind = c.Word(2);
        if (kind == "materialized")
        {
            if (c.AtEnd(3))
            {
                return Keywords("VIEW");
            }

            kind = "materialized view";
            i = 4;
        }

        if (kind is null || !ObjectKinds.TryGetValue(kind, out var objects))
        {
            return null;
        }

        if (c.AtEnd(i))
        {
            return Objects(objects);
        }

        return c.Word(c.Count - 1) == "is" ? null : Keywords("IS");
    }

    // --- GRANT / REVOKE ---

    private static SqlCommandAdvice? Grant(Cursor c, string toWord)
    {
        var on = c.IndexOf(1, "on");
        var to = c.IndexOf(1, toWord);
        var last = c.Word(c.Count - 1);
        if (to is { } t)
        {
            // "… TO |", "… TO a, |": roles; after one, the grant option.
            return c.AtEnd(t + 1) || c.Is(c.Count - 1, SqlTokenKind.Comma)
                ? Objects(SqlObjectKind.Role, "PUBLIC", "CURRENT_USER")
                : Keywords(toWord == "to" ? ["WITH GRANT OPTION", "GRANTED BY"] : ["CASCADE", "RESTRICT", "GRANTED BY"]);
        }

        if (on is not { } o)
        {
            // The privileges (or a role being granted).
            if (c.AtEnd(1))
            {
                return Objects(SqlObjectKind.Role, toWord == "from" ? [.. Privileges, "GRANT OPTION FOR"] : Privileges);
            }

            return c.Is(c.Count - 1, SqlTokenKind.Comma) ? Keywords(Privileges)
                : last is "all" ? Keywords("PRIVILEGES", "ON")
                : Keywords("ON", toWord.ToUpperInvariant(), "(");
        }

        // After ON: the kind of object, then the object, then TO/FROM.
        if (c.AtEnd(o + 1))
        {
            return Objects(SqlObjectKind.Relation, GrantTargets);
        }

        var target = c.Word(o + 1);
        var j = o + 2;
        if (target == "all")
        {
            return c.Word(c.Count - 1) switch
            {
                "all" => Keywords("TABLES IN SCHEMA", "SEQUENCES IN SCHEMA", "FUNCTIONS IN SCHEMA", "PROCEDURES IN SCHEMA", "ROUTINES IN SCHEMA"),
                "tables" or "sequences" or "functions" or "procedures" or "routines" => Keywords("IN SCHEMA"),
                "in" => Keywords("SCHEMA"),
                "schema" => Objects(SqlObjectKind.Schema),
                _ => Keywords(toWord.ToUpperInvariant()),
            };
        }

        var objects = target switch
        {
            "table" => SqlObjectKind.Relation,
            "schema" => SqlObjectKind.Schema,
            "sequence" => SqlObjectKind.Sequence,
            "function" or "procedure" or "routine" => SqlObjectKind.Function,
            "type" or "domain" => SqlObjectKind.Type,
            "database" => SqlObjectKind.None,
            _ => (SqlObjectKind?)null,
        };
        if (objects is null)
        {
            // "ON name |" — a table without the TABLE keyword.
            j = o + 1;
            objects = SqlObjectKind.Relation;
        }

        return c.AtEnd(j) || c.Is(c.Count - 1, SqlTokenKind.Comma) ? Objects(objects.Value) : Keywords(toWord.ToUpperInvariant());
    }

    // --- Maintenance ---

    private static SqlCommandAdvice? Truncate(Cursor c)
    {
        var i = 1;
        if (c.Is(i, "table"))
        {
            i++;
        }

        if (c.Is(i, "only"))
        {
            i++;
        }

        if (c.AtEnd(i) || c.Is(c.Count - 1, SqlTokenKind.Comma))
        {
            return Objects(SqlObjectKind.Table, "TABLE", "ONLY");
        }

        return Keywords("RESTART IDENTITY", "CONTINUE IDENTITY", "CASCADE", "RESTRICT");
    }

    private static SqlCommandAdvice? Vacuum(Cursor c)
    {
        if (c.OpenParen(1) is { } open && open == 1)
        {
            return c.Is(c.Count - 1, SqlTokenKind.OpenParen) || c.Is(c.Count - 1, SqlTokenKind.Comma) ? Keywords(VacuumOptions) : null;
        }

        var last = c.Word(c.Count - 1);
        return c.Count == 1 || last is "full" or "freeze" or "verbose" or "analyze" or "analyse" || c.Is(c.Count - 1, SqlTokenKind.CloseParen, SqlTokenKind.Comma)
            ? Objects(SqlObjectKind.Relation, c.Count == 1 ? ["FULL", "VERBOSE", "ANALYZE", "FREEZE"] : [])
            : null;
    }

    private static SqlCommandAdvice? Analyze(Cursor c)
    {
        if (c.OpenParen(1) is { } open && open == 1)
        {
            return c.Is(c.Count - 1, SqlTokenKind.OpenParen) || c.Is(c.Count - 1, SqlTokenKind.Comma) ? Keywords("VERBOSE", "SKIP_LOCKED", "BUFFER_USAGE_LIMIT") : null;
        }

        return c.Count == 1 || c.Word(c.Count - 1) is "verbose" || c.Is(c.Count - 1, SqlTokenKind.CloseParen, SqlTokenKind.Comma)
            ? Objects(SqlObjectKind.Relation, c.Count == 1 ? ["VERBOSE"] : [])
            : null;
    }

    private static SqlCommandAdvice? Reindex(Cursor c)
    {
        var i = 1;
        if (c.Is(i, "concurrently"))
        {
            i++;
        }

        if (c.AtEnd(1))
        {
            return Keywords("TABLE", "INDEX", "SCHEMA", "DATABASE", "SYSTEM", "CONCURRENTLY", "(");
        }

        var kind = c.Word(c.Count - 1);
        return kind switch
        {
            "table" => Objects(SqlObjectKind.Table, "CONCURRENTLY"),
            "index" => Objects(SqlObjectKind.Index, "CONCURRENTLY"),
            "schema" => Objects(SqlObjectKind.Schema, "CONCURRENTLY"),
            "concurrently" when c.Word(c.Count - 2) is "table" => Objects(SqlObjectKind.Table),
            "concurrently" when c.Word(c.Count - 2) is "index" => Objects(SqlObjectKind.Index),
            "concurrently" when c.Word(c.Count - 2) is "schema" => Objects(SqlObjectKind.Schema),
            "concurrently" => Keywords("TABLE", "INDEX", "SCHEMA", "DATABASE"),
            _ => null,
        };
    }

    private static SqlCommandAdvice? Cluster(Cursor c)
    {
        var i = 1;
        if (c.Is(i, "verbose"))
        {
            i++;
        }

        if (c.AtEnd(i))
        {
            return Objects(SqlObjectKind.Table, i == 1 ? ["VERBOSE"] : []);
        }

        if (!c.Name(ref i, out _))
        {
            return null;
        }

        return c.AtEnd(i) ? Keywords("USING") : c.Is(i, "using") && c.AtEnd(i + 1) ? Objects(SqlObjectKind.Index) : null;
    }

    private static SqlCommandAdvice? Refresh(Cursor c)
    {
        return c.Count switch
        {
            1 => Keywords("MATERIALIZED VIEW"),
            2 when c.Is(1, "materialized") => Keywords("VIEW"),
            3 when c.Is(2, "view") => Objects(SqlObjectKind.MaterializedView, "CONCURRENTLY"),
            4 when c.Is(3, "concurrently") => Objects(SqlObjectKind.MaterializedView),
            _ when c.Is(2, "view") && c.Word(c.Count - 1) is not ("with" or "no") => Keywords("WITH DATA", "WITH NO DATA"),
            _ when c.Word(c.Count - 1) == "with" => Keywords("DATA", "NO DATA"),
            _ => null,
        };
    }

    private static SqlCommandAdvice? Lock(Cursor c)
    {
        var i = 1;
        if (c.Is(i, "table"))
        {
            i++;
        }

        if (c.Is(i, "only"))
        {
            i++;
        }

        if (c.AtEnd(i) || c.Is(c.Count - 1, SqlTokenKind.Comma))
        {
            return Objects(SqlObjectKind.Table, "TABLE", "ONLY");
        }

        return c.Word(c.Count - 1) switch
        {
            "in" => Keywords(LockModes),
            "mode" or "nowait" => null,
            _ when c.IndexOf(1, "in") is null => Keywords("IN", "NOWAIT"),
            _ => null,
        };
    }

    // --- Settings ---

    private static SqlCommandAdvice? Set(Cursor c)
    {
        var i = 1;
        if (c.AtEnd(i))
        {
            return Objects(SqlObjectKind.Setting, "SESSION", "LOCAL", "ROLE", "TIME ZONE", "TRANSACTION", "SESSION AUTHORIZATION", "CONSTRAINTS");
        }

        if (c.Word(i) is "session" or "local")
        {
            i++;
            if (c.AtEnd(i))
            {
                return Objects(SqlObjectKind.Setting, "ROLE", "TIME ZONE", "AUTHORIZATION", "CHARACTERISTICS AS TRANSACTION");
            }
        }

        switch (c.Word(i))
        {
            case "role":
                return c.AtEnd(i + 1) ? Objects(SqlObjectKind.Role, "NONE") : null;
            case "time":
                return c.AtEnd(i + 1) ? Keywords("ZONE") : c.AtEnd(i + 2) ? Keywords("LOCAL", "DEFAULT", "'UTC'") : null;
            case "transaction":
                return c.Word(c.Count - 1) switch
                {
                    "transaction" or "," => Keywords("ISOLATION LEVEL", "READ ONLY", "READ WRITE", "DEFERRABLE", "NOT DEFERRABLE"),
                    "level" => Keywords("READ COMMITTED", "REPEATABLE READ", "SERIALIZABLE", "READ UNCOMMITTED"),
                    "isolation" => Keywords("LEVEL"),
                    _ => null,
                };
            case "constraints":
                return c.AtEnd(i + 1) ? Keywords("ALL") : Keywords("DEFERRED", "IMMEDIATE");
        }

        return SettingTail(c, i);
    }

    // "name |", "name TO |", "name = |": the setting's value comes after TO / =.
    private static SqlCommandAdvice? SettingTail(Cursor c, int i)
    {
        var start = i;
        if (!c.Name(ref i, out var name))
        {
            return null;
        }

        if (c.AtEnd(i))
        {
            return Keywords("TO", "=");
        }

        var setting = string.Join('.', name);
        if (c.Is(i, "to") || (c.Is(i, SqlTokenKind.Operator) && c.Text(i) == "="))
        {
            // A list setting (search_path) takes one value after another.
            return c.AtEnd(i + 1) || c.Is(c.Count - 1, SqlTokenKind.Comma)
                ? new SqlCommandAdvice(["DEFAULT"], SqlObjectKind.SettingValue) { Setting = setting }
                : null;
        }

        _ = start;
        return null;
    }

    private static SqlCommandAdvice? Reset(Cursor c) =>
        c.Count == 1 ? Objects(SqlObjectKind.Setting, "ALL", "ROLE", "SESSION AUTHORIZATION") : null;

    private static SqlCommandAdvice? Show(Cursor c) =>
        c.Count == 1 ? Objects(SqlObjectKind.Setting, "ALL") : null;

    // --- EXPLAIN's options; the statement after them is the query grammars'. ---

    private static SqlCommandAdvice? Explain(Cursor c)
    {
        if (!c.Is(1, SqlTokenKind.OpenParen) || !c.InsideFrom(1))
        {
            return null;
        }

        return c.Word(c.Count - 1) switch
        {
            "format" => Keywords("TEXT", "JSON", "YAML", "XML"),
            "serialize" => Keywords("NONE", "TEXT", "BINARY"),
            _ when c.Is(c.Count - 1, SqlTokenKind.OpenParen) || c.Is(c.Count - 1, SqlTokenKind.Comma) => Keywords(ExplainOptions),
            _ => Keywords("TRUE", "FALSE", "ON", "OFF"),
        };
    }

    private static SqlCommandAdvice? Copy(Cursor c)
    {
        var i = 1;
        if (c.AtEnd(i))
        {
            return Objects(SqlObjectKind.Relation, "(");
        }

        if (c.Is(i, SqlTokenKind.OpenParen))
        {
            return null; // COPY (query) TO …: the query grammars'
        }

        if (!c.Name(ref i, out var relation))
        {
            return null;
        }

        if (c.AtEnd(i))
        {
            return Keywords("FROM", "TO", "(");
        }

        if (c.Is(i, SqlTokenKind.OpenParen) && c.InsideFrom(i))
        {
            return c.Is(c.Count - 1, SqlTokenKind.OpenParen) || c.Is(c.Count - 1, SqlTokenKind.Comma)
                ? new SqlCommandAdvice([], SqlObjectKind.Column) { Relation = relation }
                : null;
        }

        if (c.IndexOf(i, "with") is { } with && c.OpenParen(with + 1) is { } options && options == with + 1)
        {
            return c.Word(c.Count - 1) == "format" ? Keywords("CSV", "TEXT", "BINARY")
                : c.Is(c.Count - 1, SqlTokenKind.OpenParen) || c.Is(c.Count - 1, SqlTokenKind.Comma) ? Keywords(CopyOptions)
                : null;
        }

        return c.Word(c.Count - 1) switch
        {
            "from" => Keywords("STDIN", "PROGRAM"),
            "to" => Keywords("STDOUT", "PROGRAM"),
            _ when c.Is(c.Count - 1, SqlTokenKind.String) || c.Word(c.Count - 1) is "stdin" or "stdout" => Keywords("WITH", "(", "WHERE"),
            _ => null,
        };
    }

    private static SqlCommandAdvice? Listen(Cursor c, string command) =>
        c.Count == 1 ? Objects(SqlObjectKind.Channel, command == "unlisten" ? ["*"] : []) : null;

    private static SqlCommandAdvice? Begin(Cursor c, string command)
    {
        return c.Word(c.Count - 1) switch
        {
            _ when c.Count == 1 => command == "start" ? Keywords("TRANSACTION")
                : Keywords("TRANSACTION", "WORK", "ISOLATION LEVEL", "READ ONLY", "READ WRITE"),
            "transaction" or "work" => Keywords("ISOLATION LEVEL", "READ ONLY", "READ WRITE", "DEFERRABLE"),
            "isolation" => Keywords("LEVEL"),
            "level" => Keywords("READ COMMITTED", "REPEATABLE READ", "SERIALIZABLE", "READ UNCOMMITTED"),
            _ => null,
        };
    }

    private static SqlCommandAdvice? Reassign(Cursor c)
    {
        return c.Word(c.Count - 1) switch
        {
            "reassign" => Keywords("OWNED BY"),
            "owned" => Keywords("BY"),
            "by" or "to" => Objects(SqlObjectKind.Role, "CURRENT_USER"),
            _ when c.IndexOf(1, "to") is null => Keywords("TO", ","),
            _ => null,
        };
    }

    /// <summary>A read-only view of one statement's significant tokens, the caret after the last.</summary>
    private sealed class Cursor(string sql, List<SqlToken> tokens)
    {
        public int Count => tokens.Count;

        /// <summary>True when token <paramref name="i"/> is where the caret is (past the last token).</summary>
        public bool AtEnd(int i) => i >= tokens.Count;

        public string? Word(int i) =>
            i >= 0 && i < tokens.Count && tokens[i].Kind == SqlTokenKind.Word ? SqlLexer.FoldCase(sql.AsSpan(tokens[i].Start, tokens[i].Length)) : null;

        public string Text(int i) => sql[tokens[i].Start..tokens[i].End];

        public bool Is(int i, string word) => Word(i) == word;

        public bool Is(int i, params SqlTokenKind[] kinds) => i >= 0 && i < tokens.Count && kinds.Contains(tokens[i].Kind);

        // A dotted name at `i`: advances past it, with its folded parts.
        public bool Name(ref int i, out IReadOnlyList<string> parts)
        {
            var list = new List<string>();
            parts = list;
            while (i < tokens.Count && tokens[i].Kind is SqlTokenKind.Word or SqlTokenKind.QuotedIdentifier && !tokens[i].IsIncomplete)
            {
                list.Add(SqlLexer.IdentifierName(sql, tokens[i]));
                i++;
                if (i < tokens.Count && tokens[i].Kind == SqlTokenKind.Dot)
                {
                    i++;
                    continue;
                }

                break;
            }

            return list.Count > 0;
        }

        // IF NOT EXISTS at `i`: advances past it. A partial one ("IF |",
        // "IF NOT |") is the advice itself.
        public SqlCommandAdvice? IfNotExists(ref int i)
        {
            if (!Is(i, "if"))
            {
                return null;
            }

            if (AtEnd(i + 1))
            {
                return new SqlCommandAdvice(["NOT EXISTS"]);
            }

            if (AtEnd(i + 2))
            {
                return new SqlCommandAdvice(["EXISTS"]);
            }

            i += 3;
            return AtEnd(i) ? new SqlCommandAdvice([]) { NewName = true } : null;
        }

        public SqlCommandAdvice? IfExists(ref int i)
        {
            if (!Is(i, "if"))
            {
                return null;
            }

            if (AtEnd(i + 1))
            {
                return new SqlCommandAdvice(["EXISTS"]);
            }

            i += 2;
            return null;
        }

        // True when the "(" at `open` is still open at the caret.
        public bool InsideFrom(int open)
        {
            var depth = 0;
            for (var j = open; j < tokens.Count; j++)
            {
                if (tokens[j].Kind == SqlTokenKind.OpenParen)
                {
                    depth++;
                }
                else if (tokens[j].Kind == SqlTokenKind.CloseParen && --depth == 0)
                {
                    return false;
                }
            }

            return depth > 0;
        }

        // The "(" still open at the caret that was opened at or after `from`,
        // the innermost one; null when every one opened since is closed.
        public int? OpenParen(int from)
        {
            var open = new Stack<int>();
            for (var j = Math.Max(from, 0); j < tokens.Count; j++)
            {
                if (tokens[j].Kind == SqlTokenKind.OpenParen)
                {
                    open.Push(j);
                }
                else if (tokens[j].Kind == SqlTokenKind.CloseParen && open.Count > 0)
                {
                    open.Pop();
                }
            }

            return open.Count > 0 ? open.Peek() : null;
        }

        // The last comma at the depth of token `from` (and after it), or null.
        public int? LastTopLevelComma(int from)
        {
            int? comma = null;
            var depth = 0;
            for (var j = from; j < tokens.Count; j++)
            {
                switch (tokens[j].Kind)
                {
                    case SqlTokenKind.OpenParen:
                        depth++;
                        break;
                    case SqlTokenKind.CloseParen:
                        depth--;
                        break;
                    case SqlTokenKind.Comma when depth == 0:
                        comma = j;
                        break;
                }
            }

            return comma;
        }

        // The first `word` at or after `from`, or null.
        public int? IndexOf(int from, string word)
        {
            for (var j = from; j < tokens.Count; j++)
            {
                if (Word(j) == word)
                {
                    return j;
                }
            }

            return null;
        }

        // The relation after the last REFERENCES in [from, end), when that
        // relation — and a closed column list after it, if any — runs exactly
        // up to `end`: "REFERENCES t |", "REFERENCES t (id) |", "REFERENCES t (|".
        public IReadOnlyList<string>? ReferencesRelationEndingAt(int from, int end)
        {
            for (var j = Math.Min(end, tokens.Count) - 1; j >= from; j--)
            {
                if (Word(j) != "references")
                {
                    continue;
                }

                var k = j + 1;
                if (!Name(ref k, out var parts))
                {
                    return null;
                }

                if (k < end && tokens[k].Kind == SqlTokenKind.OpenParen)
                {
                    var depth = 0;
                    for (; k < end; k++)
                    {
                        if (tokens[k].Kind == SqlTokenKind.OpenParen)
                        {
                            depth++;
                        }
                        else if (tokens[k].Kind == SqlTokenKind.CloseParen && --depth == 0)
                        {
                            k++;
                            break;
                        }
                    }
                }

                return k == end ? parts : null;
            }

            return null;
        }
    }
}
