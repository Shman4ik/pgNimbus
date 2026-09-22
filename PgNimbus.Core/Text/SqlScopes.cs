namespace PgNimbus.Core.Text;

/// <summary>Where a query sits relative to the block that contains it — which decides what it can see outside itself.</summary>
public enum SqlQueryRole
{
    /// <summary>The statement itself.</summary>
    Statement,
    /// <summary>A subquery in FROM: sees the levels above its block, never its FROM siblings.</summary>
    Derived,
    /// <summary>A <c>LATERAL</c> subquery in FROM: also sees the FROM items before it.</summary>
    Lateral,
    /// <summary>A subquery inside an expression (<c>EXISTS (…)</c>, <c>IN (…)</c>, a scalar subquery): sees every level around it.</summary>
    Expression,
    /// <summary>A WITH body: sees what the query owning the WITH sees, plus the CTEs it may reference.</summary>
    Cte,
}

/// <summary>What statement a block is.</summary>
public enum SqlBlockKind
{
    Select,
    Values,
    Insert,
    Update,
    Delete,
    Merge,
}

/// <summary>How a FROM item was attached to the one before it.</summary>
public enum SqlJoinKind
{
    /// <summary>The first item of a FROM list, or a DML target.</summary>
    None,
    Comma,
    Inner,
    Left,
    Right,
    Full,
    Cross,
    /// <summary>Any <c>NATURAL … JOIN</c>: merges every shared column name.</summary>
    Natural,
}

/// <summary>A range of offsets in the parsed text; the caret is in it at either end.</summary>
public readonly record struct SqlSpan(int Start, int End)
{
    public bool Contains(int caret) => Start <= caret && caret <= End;
}

/// <summary>A clause keyword at a block's top level (<c>where</c>, <c>order</c>, <c>set</c>, <c>conflict</c> …) and where it ends.</summary>
public readonly record struct SqlClauseMark(string Keyword, int End);

/// <summary>
/// One select-list (or RETURNING) item: its output name when one can be known
/// without running it, or a star. <see cref="RefQualifier"/> /
/// <see cref="RefColumn"/> name the column a bare reference item passes
/// through, so its type can be looked up.
/// </summary>
public sealed record SqlOutputItem(string? Name, bool IsStar = false, string? StarQualifier = null, string? RefQualifier = null, string? RefColumn = null);

/// <summary>A FROM item (or DML target): a relation, a derived table, or a set-returning function.</summary>
public sealed class SqlSource
{
    public required string Schema { get; init; }

    /// <summary>The relation name — folded or exact like every other name here; empty for a derived table.</summary>
    public required string Name { get; init; }

    public string? Alias { get; init; }

    /// <summary>The subquery (or VALUES) when this item is a derived table.</summary>
    public SqlQuery? Derived { get; init; }

    /// <summary>A function in FROM (<c>generate_series(…)</c>): its columns are unknown unless aliased.</summary>
    public bool IsFunction { get; init; }

    /// <summary>A column alias list (<c>AS q(a, b)</c>) renaming the leading output columns.</summary>
    public IReadOnlyList<string>? ColumnAliases { get; init; }

    public SqlJoinKind Join { get; init; }

    /// <summary>The columns a <c>JOIN … USING (…)</c> on this item merges.</summary>
    public IReadOnlyList<string> UsingColumns { get; internal set; } = [];

    /// <summary>The inside of this item's <c>USING (…)</c> list, when it has one.</summary>
    public SqlSpan? UsingSpan { get; internal set; }

    /// <summary>The target of an INSERT / UPDATE / DELETE / MERGE.</summary>
    public bool IsTarget { get; init; }

    /// <summary>Offset of the item's first character in the parsed text.</summary>
    public int Start { get; init; }

    /// <summary>The name the item is addressed by in the rest of the block.</summary>
    public string Label => Alias ?? Name;
}

/// <summary>A CTE: its name, optional declared column list and its body.</summary>
public sealed class SqlCte
{
    public required string Name { get; init; }

    public IReadOnlyList<string>? DeclaredColumns { get; init; }

    public required SqlQuery Body { get; init; }

    /// <summary>The query whose WITH defines it.</summary>
    public required SqlQuery Owner { get; init; }
}

/// <summary>
/// A query expression: an optional WITH list and one or more set-operation
/// branches. <see cref="IsOpaque"/> marks one that was not read at all
/// (nested past <see cref="SqlScopeModel.MaxDepth"/>): nothing inside it is known.
/// </summary>
public sealed class SqlQuery
{
    public SqlQueryRole Role { get; init; }

    /// <summary>The block containing this query (null for the statement and for WITH bodies).</summary>
    public SqlBlock? Owner { get; init; }

    /// <summary>For a WITH body: the query whose WITH it belongs to.</summary>
    public SqlQuery? WithOwner { get; init; }

    public int Start { get; init; }

    public int End { get; init; }

    public bool Recursive { get; internal set; }

    public bool IsOpaque { get; init; }

    public List<SqlCte> Ctes { get; } = [];

    public List<SqlBlock> Branches { get; } = [];
}

/// <summary>One SELECT / VALUES / DML block: its sources, its output, and the queries nested in it.</summary>
public sealed class SqlBlock
{
    public required SqlQuery Query { get; init; }

    public SqlBlockKind Kind { get; init; }

    public int Start { get; init; }

    public int End { get; init; }

    public List<SqlSource> Sources { get; } = [];

    /// <summary>The select list — or, for a DML statement, its RETURNING list; VALUES yields column1…columnN.</summary>
    public List<SqlOutputItem> Output { get; } = [];

    public List<SqlQuery> Nested { get; } = [];

    /// <summary>The top-level clause keywords, in order — see <see cref="ClauseAt"/>.</summary>
    public List<SqlClauseMark> Clauses { get; } = [];

    /// <summary>The inside of an INSERT's target column list, <c>INSERT INTO t (…)</c>.</summary>
    public SqlSpan? InsertColumns { get; internal set; }

    /// <summary>The inside of <c>ON CONFLICT (…)</c>.</summary>
    public SqlSpan? ConflictTarget { get; internal set; }

    /// <summary>Where <c>ON CONFLICT … DO</c> ends: from here to RETURNING, <c>excluded</c> names the proposed row.</summary>
    public int? ConflictActionStart { get; internal set; }

    /// <summary>The DML target, if this block has one.</summary>
    public SqlSource? Target => Sources.FirstOrDefault(s => s.IsTarget);

    /// <summary>The last top-level clause keyword that ends at or before <paramref name="caret"/> (folded), or null.</summary>
    public string? ClauseAt(int caret)
    {
        string? clause = null;
        foreach (var mark in Clauses)
        {
            if (mark.End > caret)
            {
                break;
            }

            clause = mark.Keyword;
        }

        return clause;
    }

    /// <summary>True when <c>excluded</c> (the row ON CONFLICT proposed) can be named at <paramref name="caret"/>.</summary>
    public bool SeesExcluded(int caret) =>
        Kind == SqlBlockKind.Insert && ConflictActionStart is { } start && caret >= start && ClauseAt(caret) != "returning";
}

/// <summary>
/// The scope tree of one statement, read from the shared <see cref="SqlLexer"/>
/// tokens: which SELECT/DML block the caret is in, what sources that block and
/// the blocks around it contribute, and which CTEs are in reach. This is what
/// keeps an <c>EXISTS (…)</c>'s tables out of the outer WHERE and lets
/// <c>q.</c> after <c>(SELECT id AS customer_id …) q</c> offer
/// <c>customer_id</c>.
/// </summary>
/// <remarks>
/// Visibility follows PostgreSQL: a subquery inside an expression may reference
/// every level around it; a subquery in FROM may not reference the other items
/// of its own FROM list unless it is <c>LATERAL</c>, and then only the ones before
/// it; a WITH body sees what its owning query sees plus the CTEs defined before
/// it (all of them, itself included, under <c>WITH RECURSIVE</c>). The reader is
/// tolerant — an unclosed paren runs to the end, a half-typed clause yields what
/// it has — and never guesses: what it cannot read is reported as unknown, not
/// resolved to something plausible.
/// </remarks>
public sealed class SqlScopeModel
{
    /// <summary>How deep queries may nest before the rest is left unread (and reported unknown).</summary>
    public const int MaxDepth = 32;

    private SqlScopeModel(SqlQuery? root) => Root = root;

    /// <summary>The statement's top query, or null when the statement holds no query this model reads (DDL, SET …).</summary>
    public SqlQuery? Root { get; }

    /// <summary>Reads the scope tree of <paramref name="statement"/> (one statement; offsets are relative to it).</summary>
    public static SqlScopeModel Parse(string statement) => new(new Reader(statement).ReadStatement());

    /// <summary>
    /// The innermost block containing <paramref name="caret"/>. Null both when
    /// no block contains it and when the caret is inside a query that was not
    /// read — <paramref name="unknown"/> tells the two apart.
    /// </summary>
    public SqlBlock? BlockAt(int caret, out bool unknown)
    {
        unknown = false;
        if (Root is null)
        {
            return null;
        }

        return Find(Root, caret, ref unknown);
    }

    /// <inheritdoc cref="BlockAt(int, out bool)"/>
    public SqlBlock? BlockAt(int caret) => BlockAt(caret, out _);

    private static SqlBlock? Find(SqlQuery query, int caret, ref bool unknown)
    {
        if (query.IsOpaque)
        {
            unknown = true;
            return null;
        }

        foreach (var cte in query.Ctes)
        {
            if (Contains(cte.Body.Start, cte.Body.End, caret))
            {
                return Find(cte.Body, caret, ref unknown);
            }
        }

        foreach (var branch in query.Branches)
        {
            if (!Contains(branch.Start, branch.End, caret))
            {
                continue;
            }

            foreach (var nested in branch.Nested)
            {
                if (Contains(nested.Start, nested.End, caret))
                {
                    return Find(nested, caret, ref unknown);
                }
            }

            return branch;
        }

        return null;
    }

    private static bool Contains(int start, int end, int caret) => start <= caret && caret <= end;

    /// <summary>
    /// The sources a column reference at <paramref name="block"/> can name,
    /// innermost level first: the block's own FROM items, then each enclosing
    /// level PostgreSQL lets it reference. A name found at an inner level hides
    /// the same name further out.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<SqlSource>> VisibleSources(SqlBlock block)
    {
        var levels = new List<IReadOnlyList<SqlSource>> { block.Sources };
        AddOuterLevels(block.Query, levels, 0);
        return levels;
    }

    private static void AddOuterLevels(SqlQuery query, List<IReadOnlyList<SqlSource>> levels, int guard)
    {
        if (guard > MaxDepth * 2)
        {
            return;
        }

        switch (query.Role)
        {
            case SqlQueryRole.Expression when query.Owner is { } owner:
                levels.Add(owner.Sources);
                AddOuterLevels(owner.Query, levels, guard + 1);
                break;
            case SqlQueryRole.Derived when query.Owner is { } owner:
                AddOuterLevels(owner.Query, levels, guard + 1);
                break;
            case SqlQueryRole.Lateral when query.Owner is { } owner:
                levels.Add([.. owner.Sources.Where(s => s.Start < query.Start && s.Derived != query)]);
                AddOuterLevels(owner.Query, levels, guard + 1);
                break;
            case SqlQueryRole.Cte when query.WithOwner is { } withOwner:
                AddOuterLevels(withOwner, levels, guard + 1);
                break;
        }
    }

    /// <summary>
    /// The CTEs a table reference in <paramref name="block"/> can name,
    /// innermost first (an inner CTE hides a same-named outer one). Inside a
    /// WITH body only the CTEs before it are in reach — all of them, itself
    /// included, under <c>WITH RECURSIVE</c>.
    /// </summary>
    public static IReadOnlyList<SqlCte> VisibleCtes(SqlBlock block)
    {
        var result = new List<SqlCte>();
        var query = block.Query;
        SqlCte? from = null;
        for (var guard = 0; query is not null && guard <= MaxDepth * 2; guard++)
        {
            var index = from is null ? query.Ctes.Count : query.Ctes.IndexOf(from);
            for (var i = 0; i < query.Ctes.Count; i++)
            {
                if (from is null || query.Recursive || i < index)
                {
                    result.Add(query.Ctes[i]);
                }
            }

            if (query.Role == SqlQueryRole.Cte && query.WithOwner is { } withOwner)
            {
                from = withOwner.Ctes.FirstOrDefault(c => c.Body == query);
                query = withOwner;
            }
            else
            {
                from = null;
                query = query.Owner?.Query;
            }
        }

        return result;
    }

    /// <summary>
    /// True when <paramref name="caret"/> sits where a SET clause names the
    /// column being assigned (<c>SET |</c>, <c>SET a = 1, |</c>,
    /// <c>SET (a, |) = …</c>) rather than the value (<c>SET a = |</c>). Only a
    /// target column can go there — unqualified, and nothing else.
    /// </summary>
    public static bool IsAssignmentTarget(string sql, SqlBlock block, int caret)
    {
        var setEnd = -1;
        foreach (var mark in block.Clauses)
        {
            if (mark.End > caret)
            {
                break;
            }

            setEnd = mark.Keyword == "set" ? mark.End : -1;
        }

        if (setEnd < 0)
        {
            return false;
        }

        var wordStart = caret;
        while (wordStart > setEnd && SqlLexer.IsIdentPart(sql[wordStart - 1]))
        {
            wordStart--;
        }

        var depth = 0;
        var assigned = false;
        foreach (var token in SqlLexer.Tokenize(sql, setEnd, wordStart))
        {
            switch (token.Kind)
            {
                case SqlTokenKind.OpenParen or SqlTokenKind.OpenBracket:
                    depth++;
                    break;
                case SqlTokenKind.CloseParen or SqlTokenKind.CloseBracket:
                    depth = Math.Max(0, depth - 1);
                    break;
                case SqlTokenKind.Comma when depth == 0:
                    assigned = false;
                    break;
                case SqlTokenKind.Operator when depth == 0 && sql[token.Start] == '=':
                    assigned = true;
                    break;
            }
        }

        return !assigned;
    }

    // --- The reader ---------------------------------------------------------

    private sealed class Reader
    {
        private readonly string _sql;
        private readonly List<SqlToken> _t;
        // Index of the token closing the paren/bracket opened at each index
        // (_t.Count when it's never closed); -1 for every other token.
        private readonly int[] _close;

        public Reader(string sql)
        {
            _sql = sql;
            _t = [.. SqlLexer.Tokenize(sql).Where(t => !t.IsTrivia)];
            _close = new int[_t.Count];
            Array.Fill(_close, -1);
            var open = new Stack<int>();
            for (var i = 0; i < _t.Count; i++)
            {
                switch (_t[i].Kind)
                {
                    case SqlTokenKind.OpenParen or SqlTokenKind.OpenBracket:
                        open.Push(i);
                        break;
                    case SqlTokenKind.CloseParen or SqlTokenKind.CloseBracket when open.Count > 0:
                        _close[open.Pop()] = i;
                        break;
                }
            }

            while (open.Count > 0)
            {
                _close[open.Pop()] = _t.Count;
            }
        }

        public SqlQuery? ReadStatement()
        {
            // A statement that isn't a query itself may still carry one:
            // EXPLAIN [(…)] SELECT, CREATE VIEW … AS SELECT, CREATE TABLE … AS WITH.
            var i = 0;
            if (!IsQueryStart(i) && !IsDmlStart(i))
            {
                // Only EXPLAIN is followed by a DML statement; after CREATE …,
                // "UPDATE" and "TABLE" are words of the DDL, not a query.
                var explain = Kw(0, "explain");
                i = -1;
                for (var j = 1; j < _t.Count; j = Next(j))
                {
                    if (Kw(j, "select") || Kw(j, "with") || Kw(j, "values") || (explain && IsDmlStart(j)))
                    {
                        i = j;
                        break;
                    }
                }

                if (i < 0)
                {
                    return null;
                }
            }

            return ReadQuery(i, _t.Count, i < _t.Count ? _t[i].Start : _sql.Length, _sql.Length, SqlQueryRole.Statement, null, null, 0);
        }

        // --- tokens ---

        private int Next(int i) => _close[i] >= 0 ? Math.Min(_close[i] + 1, _t.Count) : i + 1;

        private bool Is(int i, SqlTokenKind kind) => i < _t.Count && _t[i].Kind == kind;

        private bool Kw(int i, string word) =>
            i < _t.Count && _t[i].Kind == SqlTokenKind.Word
            && _sql.AsSpan(_t[i].Start, _t[i].Length).Equals(word, StringComparison.OrdinalIgnoreCase);

        private string? WordAt(int i) =>
            i < _t.Count && _t[i].Kind == SqlTokenKind.Word ? SqlLexer.FoldCase(_sql.AsSpan(_t[i].Start, _t[i].Length)) : null;

        private bool IsName(int i) => i < _t.Count && _t[i].Kind is SqlTokenKind.Word or SqlTokenKind.QuotedIdentifier;

        private string NameAt(int i) => SqlLexer.IdentifierName(_sql, _t[i]);

        private bool IsQueryStart(int i) =>
            Kw(i, "select") || Kw(i, "with") || Kw(i, "values") || Kw(i, "table")
            || (Is(i, SqlTokenKind.OpenParen) && IsQueryStart(i + 1));

        private bool IsDmlStart(int i) => Kw(i, "insert") || Kw(i, "update") || Kw(i, "delete") || Kw(i, "merge");

        // Offset where the text inside the group opened at `open` ends.
        private int InnerEnd(int open) => _close[open] < _t.Count ? _t[_close[open]].Start : _sql.Length;

        // --- queries ---

        private SqlQuery ReadQuery(int s, int e, int start, int end, SqlQueryRole role, SqlBlock? owner, SqlQuery? withOwner, int depth)
        {
            if (depth > MaxDepth)
            {
                return new SqlQuery { Role = role, Owner = owner, WithOwner = withOwner, Start = start, End = end, IsOpaque = true };
            }

            var query = new SqlQuery { Role = role, Owner = owner, WithOwner = withOwner, Start = start, End = end };
            var i = s;
            var branchStart = start;
            if (Kw(i, "with"))
            {
                i++;
                if (Kw(i, "recursive"))
                {
                    query.Recursive = true;
                    i++;
                }

                i = ReadCtes(query, i, e, depth, ref branchStart);
            }

            // Set operations split the rest into branches, each its own block.
            var bs = i;
            var j = i;
            while (j < e)
            {
                if (!(Kw(j, "union") || Kw(j, "intersect") || Kw(j, "except")))
                {
                    j = Next(j);
                    continue;
                }

                ReadBranch(query, bs, j, branchStart, _t[j].Start, depth);
                var k = j + 1;
                if (Kw(k, "all") || Kw(k, "distinct"))
                {
                    k++;
                }

                bs = Math.Min(k, e);
                branchStart = _t[k - 1].End;
                j = bs;
            }

            ReadBranch(query, bs, e, branchStart, end, depth);
            return query;
        }

        private int ReadCtes(SqlQuery query, int i, int e, int depth, ref int branchStart)
        {
            while (i < e && IsName(i))
            {
                var name = NameAt(i);
                i++;
                List<string>? declared = null;
                if (Is(i, SqlTokenKind.OpenParen))
                {
                    declared = NamesIn(i);
                    i = Next(i);
                }

                if (!Kw(i, "as"))
                {
                    break;
                }

                i++;
                if (Kw(i, "not"))
                {
                    i++;
                }

                if (Kw(i, "materialized"))
                {
                    i++;
                }

                if (!Is(i, SqlTokenKind.OpenParen))
                {
                    break;
                }

                var body = ReadQuery(i + 1, Math.Min(_close[i], e), _t[i].End, InnerEnd(i), SqlQueryRole.Cte, null, query, depth + 1);
                query.Ctes.Add(new SqlCte { Name = name, DeclaredColumns = declared, Body = body, Owner = query });
                branchStart = _close[i] < _t.Count ? _t[_close[i]].End : _sql.Length;
                i = Next(i);
                if (!Is(i, SqlTokenKind.Comma))
                {
                    break;
                }

                i++;
            }

            return i;
        }

        // The identifiers of a parenthesized name list (a CTE's or alias's column list, USING).
        private List<string> NamesIn(int open)
        {
            var names = new List<string>();
            for (var j = open + 1; j < Math.Min(_close[open], _t.Count); j++)
            {
                if (IsName(j))
                {
                    names.Add(NameAt(j));
                }
            }

            return names;
        }

        private void ReadBranch(SqlQuery query, int s, int e, int start, int end, int depth)
        {
            if (s >= e)
            {
                // "SELECT 1 UNION |" — an empty branch is still where the caret is.
                query.Branches.Add(new SqlBlock { Query = query, Kind = SqlBlockKind.Select, Start = start, End = end });
                return;
            }

            // "(SELECT …) UNION (SELECT …)" — a parenthesized branch is read from inside.
            if (s < e && Is(s, SqlTokenKind.OpenParen) && _close[s] >= e - 1 && IsQueryStart(s + 1))
            {
                var inner = ReadQuery(s + 1, Math.Min(_close[s], e), _t[s].End, InnerEnd(s), query.Role, query.Owner, query.WithOwner, depth + 1);
                foreach (var cte in inner.Ctes)
                {
                    query.Ctes.Add(cte);
                }

                foreach (var branch in inner.Branches)
                {
                    var moved = new SqlBlock { Query = query, Kind = branch.Kind, Start = branch.Start, End = branch.End };
                    moved.Sources.AddRange(branch.Sources);
                    moved.Output.AddRange(branch.Output);
                    moved.Nested.AddRange(branch.Nested);
                    query.Branches.Add(moved);
                }

                return;
            }

            var kind = WordAt(s) switch
            {
                "values" => SqlBlockKind.Values,
                "insert" => SqlBlockKind.Insert,
                "update" => SqlBlockKind.Update,
                "delete" => SqlBlockKind.Delete,
                "merge" => SqlBlockKind.Merge,
                _ => SqlBlockKind.Select,
            };

            var block = new SqlBlock { Query = query, Kind = kind, Start = start, End = end };
            query.Branches.Add(block);
            switch (kind)
            {
                case SqlBlockKind.Values:
                    ReadValues(block, s, e, depth);
                    break;
                case SqlBlockKind.Insert:
                    ReadInsert(block, s, e, depth);
                    break;
                case SqlBlockKind.Update:
                    ReadUpdate(block, s, e, depth);
                    break;
                case SqlBlockKind.Delete:
                    ReadDelete(block, s, e, depth);
                    break;
                case SqlBlockKind.Merge:
                    ReadMerge(block, s, e, depth);
                    break;
                default:
                    ReadSelect(block, s, e, depth);
                    break;
            }
        }

        // --- SELECT ---

        // The clause keywords a block records (SqlBlock.Clauses).
        private static readonly HashSet<string> MarkWords = new(StringComparer.Ordinal)
        {
            "select", "from", "where", "group", "having", "window", "order", "limit", "offset", "fetch",
            "returning", "set", "conflict", "do", "using", "values", "into",
        };

        private void Mark(SqlBlock block, int i)
        {
            if (WordAt(i) is { } word && MarkWords.Contains(word))
            {
                block.Clauses.Add(new SqlClauseMark(word, _t[i].End));
            }
        }

        private static readonly HashSet<string> SelectClauseWords = new(StringComparer.Ordinal)
        {
            "from", "into", "where", "group", "having", "window", "order", "limit", "offset", "fetch", "for",
        };

        private void ReadSelect(SqlBlock block, int s, int e, int depth)
        {
            var i = s;
            if (Kw(i, "table"))
            {
                // TABLE name — shorthand for SELECT * FROM name.
                i++;
                if (i < e && ReadRelationName(ref i) is var (schema, name))
                {
                    block.Sources.Add(new SqlSource { Schema = schema, Name = name, Start = _t[s].Start });
                    block.Output.Add(new SqlOutputItem(null, IsStar: true));
                }

                return;
            }

            if (Kw(i, "select"))
            {
                Mark(block, i);
                i++;
            }

            if (Kw(i, "all"))
            {
                i++;
            }
            else if (Kw(i, "distinct"))
            {
                i++;
                if (Kw(i, "on") && Is(i + 1, SqlTokenKind.OpenParen))
                {
                    ScanExpressions(block, i + 1, Next(i + 1), depth);
                    i = Next(i + 1);
                }
            }

            var listEnd = i;
            while (listEnd < e && !(WordAt(listEnd) is { } w && SelectClauseWords.Contains(w)))
            {
                listEnd = Next(listEnd);
            }

            ReadOutputList(block, i, Math.Min(listEnd, e), depth);
            ReadClauses(block, listEnd, e, depth, fromWords: ["from"], stopWords: SelectClauseWords);
        }

        // Clause by clause from `i`: a FROM list (after any of `fromWords`) is
        // read as sources; every other clause is scanned for nested subqueries.
        private void ReadClauses(SqlBlock block, int i, int e, int depth, string[] fromWords, IReadOnlySet<string> stopWords)
        {
            while (i < e)
            {
                Mark(block, i);
                if (Kw(i, "conflict") && Is(i + 1, SqlTokenKind.OpenParen))
                {
                    block.ConflictTarget = new SqlSpan(_t[i + 1].End, InnerEnd(i + 1));
                }

                if (Kw(i, "do") && block.Kind == SqlBlockKind.Insert)
                {
                    block.ConflictActionStart = _t[i].End;
                }

                if (WordAt(i) is { } word && fromWords.Contains(word))
                {
                    var fromEnd = i + 1;
                    while (fromEnd < e && !(WordAt(fromEnd) is { } w && (stopWords.Contains(w) || w == "returning")))
                    {
                        fromEnd = Next(fromEnd);
                    }

                    ReadFromList(block, i + 1, fromEnd, depth);
                    i = fromEnd;
                    continue;
                }

                if (Kw(i, "returning"))
                {
                    ReadOutputList(block, i + 1, e, depth);
                    return;
                }

                if (Kw(i, "into") && block.Kind == SqlBlockKind.Select)
                {
                    // SELECT … INTO new_table: the name is created, not read.
                    i++;
                    if (Kw(i, "temporary") || Kw(i, "temp") || Kw(i, "unlogged"))
                    {
                        i++;
                    }

                    if (Kw(i, "table"))
                    {
                        i++;
                    }

                    _ = ReadRelationName(ref i);
                    continue;
                }

                if (Is(i, SqlTokenKind.OpenParen) || Is(i, SqlTokenKind.OpenBracket))
                {
                    ScanGroup(block, i, depth);
                }

                i = Next(i);
            }
        }

        // One select-list / RETURNING list, split on its top-level commas.
        private void ReadOutputList(SqlBlock block, int s, int e, int depth)
        {
            var itemStart = s;
            for (var j = s; j <= e; j = j < e ? Next(j) : j + 1)
            {
                if (j < e && !Is(j, SqlTokenKind.Comma))
                {
                    if (Is(j, SqlTokenKind.OpenParen) || Is(j, SqlTokenKind.OpenBracket))
                    {
                        ScanGroup(block, j, depth);
                    }

                    continue;
                }

                if (j > itemStart)
                {
                    block.Output.Add(ReadOutputItem(itemStart, Math.Min(j, e)));
                }

                itemStart = j + 1;
            }
        }

        // The output name of one item, the way the server names it: an alias
        // (AS or implicit), the last part of a plain column reference, the
        // function name of a bare call; null for any other expression.
        private SqlOutputItem ReadOutputItem(int s, int e)
        {
            var last = e - 1;
            if (Is(last, SqlTokenKind.Operator) && _sql[_t[last].Start] == '*')
            {
                if (last == s)
                {
                    return new SqlOutputItem(null, IsStar: true);
                }

                if (Is(last - 1, SqlTokenKind.Dot) && IsName(last - 2) && IsChain(s, last - 1))
                {
                    return new SqlOutputItem(null, IsStar: true, StarQualifier: NameAt(last - 2));
                }
            }

            if (IsName(last))
            {
                var aliasCandidate = !Is(last, SqlTokenKind.Word) || !AliasStopWords.Contains(WordAt(last)!);
                if (last > s && Kw(last - 1, "as"))
                {
                    return new SqlOutputItem(NameAt(last));
                }

                if (IsChain(s, e))
                {
                    var qualifier = last - 2 >= s ? NameAt(last - 2) : null;
                    return new SqlOutputItem(NameAt(last), RefQualifier: qualifier, RefColumn: NameAt(last));
                }

                if (aliasCandidate && last > s && EndsValue(last - 1))
                {
                    return new SqlOutputItem(NameAt(last));
                }
            }

            // f(…) → f; schema.f(…) → f.
            if (Is(last, SqlTokenKind.CloseParen))
            {
                var open = OpenOf(last, s);
                if (open > s && IsName(open - 1) && IsChain(s, open))
                {
                    return new SqlOutputItem(NameAt(open - 1));
                }
            }

            // x::type — named after x.
            for (var j = s; j < e; j = Next(j))
            {
                if (Is(j, SqlTokenKind.DoubleColon) && j > s)
                {
                    return ReadOutputItem(s, j) is { Name: not null, IsStar: false } inner ? inner : new SqlOutputItem(null);
                }
            }

            return new SqlOutputItem(null);
        }

        // The index of the paren opening the group closed at `close`.
        private int OpenOf(int close, int from)
        {
            for (var j = from; j < close; j++)
            {
                if (_close[j] == close)
                {
                    return j;
                }
            }

            return -1;
        }

        // True when [s,e) is name(.name)* and nothing else.
        private bool IsChain(int s, int e)
        {
            if (e <= s || !IsName(s))
            {
                return false;
            }

            for (var j = s + 1; j < e; j += 2)
            {
                if (!Is(j, SqlTokenKind.Dot) || !IsName(j + 1) || j + 1 >= e)
                {
                    return false;
                }
            }

            return true;
        }

        // Whether token `i` can end an expression that an implicit alias follows.
        private bool EndsValue(int i) => i < _t.Count && _t[i].Kind switch
        {
            SqlTokenKind.CloseParen or SqlTokenKind.CloseBracket or SqlTokenKind.QuotedIdentifier
                or SqlTokenKind.Number or SqlTokenKind.String or SqlTokenKind.DollarString or SqlTokenKind.Parameter => true,
            SqlTokenKind.Word => !AliasStopWords.Contains(WordAt(i)!) || WordAt(i) is "end" or "null" or "true" or "false",
            _ => false,
        };

        // Words that end an expression but are never its alias.
        private static readonly HashSet<string> AliasStopWords = new(StringComparer.Ordinal)
        {
            "end", "null", "true", "false", "not", "and", "or", "is", "in", "like", "ilike", "similar",
            "between", "asc", "desc", "over", "filter", "within", "escape", "collate", "interval", "at",
            "time", "zone", "then", "else", "when", "case", "distinct", "all", "as", "select",
        };

        // --- FROM ---

        // Words that end a table reference and are never its alias.
        private static readonly HashSet<string> NotAnAlias = new(StringComparer.Ordinal)
        {
            "on", "using", "where", "group", "order", "having", "limit", "offset", "fetch", "join", "inner",
            "left", "right", "full", "outer", "cross", "natural", "lateral", "union", "intersect", "except",
            "returning", "window", "for", "tablesample", "set", "values", "select", "with", "default", "into",
            "as", "do", "when", "then",
        };

        private void ReadFromList(SqlBlock block, int s, int e, int depth)
        {
            var join = SqlJoinKind.None;
            var expectItem = true;
            var i = s;
            while (i < e)
            {
                if (expectItem)
                {
                    var itemStart = _t[i].Start;
                    var lateral = false;
                    if (Kw(i, "lateral"))
                    {
                        lateral = true;
                        i++;
                    }

                    if (Kw(i, "only"))
                    {
                        i++;
                    }

                    if (i >= e)
                    {
                        break;
                    }

                    SqlSource? source = null;
                    if (Is(i, SqlTokenKind.OpenParen))
                    {
                        if (IsQueryStart(i + 1))
                        {
                            var derived = ReadQuery(i + 1, Math.Min(_close[i], e), _t[i].End, InnerEnd(i),
                                lateral ? SqlQueryRole.Lateral : SqlQueryRole.Derived, block, null, depth + 1);
                            block.Nested.Add(derived);
                            i = Next(i);
                            var (alias, columns) = ReadAlias(ref i, e);
                            source = new SqlSource { Schema = "", Name = "", Alias = alias, Derived = derived, ColumnAliases = columns, Join = join, Start = itemStart };
                        }
                        else
                        {
                            // A parenthesized join tree: its items belong to this block.
                            ReadFromList(block, i + 1, Math.Min(_close[i], e), depth);
                            i = Next(i);
                            _ = ReadAlias(ref i, e);
                        }
                    }
                    else if (IsName(i) && !(WordAt(i) is { } w && NotAnAlias.Contains(w)))
                    {
                        var nameStart = i;
                        var (schema, name) = ReadRelationName(ref i) ?? ("", "");
                        var isFunction = false;
                        if (Is(i, SqlTokenKind.OpenParen))
                        {
                            isFunction = true;
                            ScanGroup(block, i, depth);
                            i = Next(i);
                            if (Kw(i, "with") && Kw(i + 1, "ordinality"))
                            {
                                i += 2;
                            }
                        }

                        var (alias, columns) = ReadAlias(ref i, e);
                        source = new SqlSource
                        {
                            Schema = schema, Name = name, Alias = alias, IsFunction = isFunction, ColumnAliases = columns,
                            Join = join, Start = _t[nameStart].Start,
                        };
                    }
                    else
                    {
                        i = Next(i);
                    }

                    if (source is not null)
                    {
                        block.Sources.Add(source);
                    }

                    expectItem = false;
                    continue;
                }

                if (Is(i, SqlTokenKind.Comma))
                {
                    join = SqlJoinKind.Comma;
                    expectItem = true;
                    i++;
                    continue;
                }

                if (ReadJoinKeyword(ref i, e) is { } kind)
                {
                    join = kind;
                    expectItem = true;
                    continue;
                }

                if (Kw(i, "using") && Is(i + 1, SqlTokenKind.OpenParen) && block.Sources.Count > 0)
                {
                    block.Sources[^1].UsingColumns = NamesIn(i + 1);
                    block.Sources[^1].UsingSpan = new SqlSpan(_t[i + 1].End, InnerEnd(i + 1));
                    i = Next(i + 1);
                    continue;
                }

                if (Is(i, SqlTokenKind.OpenParen) || Is(i, SqlTokenKind.OpenBracket))
                {
                    ScanGroup(block, i, depth);
                }

                i = Next(i);
            }
        }

        // [NATURAL] [INNER | {LEFT | RIGHT | FULL} [OUTER] | CROSS] JOIN
        private SqlJoinKind? ReadJoinKeyword(ref int i, int e)
        {
            var j = i;
            var natural = false;
            SqlJoinKind kind = SqlJoinKind.Inner;
            if (Kw(j, "natural"))
            {
                natural = true;
                j++;
            }

            switch (WordAt(j))
            {
                case "inner":
                    j++;
                    break;
                case "left":
                    kind = SqlJoinKind.Left;
                    j++;
                    break;
                case "right":
                    kind = SqlJoinKind.Right;
                    j++;
                    break;
                case "full":
                    kind = SqlJoinKind.Full;
                    j++;
                    break;
                case "cross":
                    kind = SqlJoinKind.Cross;
                    j++;
                    break;
            }

            if (Kw(j, "outer"))
            {
                j++;
            }

            if (j >= e || !Kw(j, "join"))
            {
                return null;
            }

            i = j + 1;
            return natural ? SqlJoinKind.Natural : kind;
        }

        // [schema.]name (a leading database part is skipped); advances past it.
        private (string Schema, string Name)? ReadRelationName(ref int i)
        {
            if (!IsName(i))
            {
                return null;
            }

            var parts = new List<string> { NameAt(i) };
            i++;
            while (Is(i, SqlTokenKind.Dot) && IsName(i + 1))
            {
                parts.Add(NameAt(i + 1));
                i += 2;
            }

            if (Is(i, SqlTokenKind.Dot))
            {
                i++; // "schema." still being typed
            }

            return parts.Count == 1 ? ("", parts[0]) : (parts[^2], parts[^1]);
        }

        // [AS] alias [(col, …)] after a FROM item.
        private (string? Alias, IReadOnlyList<string>? Columns) ReadAlias(ref int i, int e)
        {
            if (i >= e)
            {
                return (null, null);
            }

            var explicitAs = Kw(i, "as");
            var j = explicitAs ? i + 1 : i;
            if (j >= e || !IsName(j) || (!explicitAs && WordAt(j) is { } w && NotAnAlias.Contains(w)))
            {
                i = j;
                return (null, null);
            }

            var alias = NameAt(j);
            j++;
            IReadOnlyList<string>? columns = null;
            if (Is(j, SqlTokenKind.OpenParen))
            {
                columns = NamesIn(j);
                j = Next(j);
            }

            i = j;
            return (alias, columns);
        }

        // --- expressions ---

        // A paren group in an expression: a subquery, or a group whose inside may hold one.
        private void ScanGroup(SqlBlock block, int open, int depth)
        {
            if (Is(open, SqlTokenKind.OpenParen) && IsQueryStart(open + 1))
            {
                block.Nested.Add(ReadQuery(open + 1, _close[open], _t[open].End, InnerEnd(open), SqlQueryRole.Expression, block, null, depth + 1));
                return;
            }

            ScanExpressions(block, open + 1, Math.Min(_close[open], _t.Count), depth);
        }

        private void ScanExpressions(SqlBlock block, int s, int e, int depth)
        {
            for (var j = s; j < e; j = Next(j))
            {
                if (Is(j, SqlTokenKind.OpenParen) || Is(j, SqlTokenKind.OpenBracket))
                {
                    ScanGroup(block, j, depth);
                }
            }
        }

        // --- VALUES and DML ---

        private void ReadValues(SqlBlock block, int s, int e, int depth)
        {
            var i = s + 1;
            if (Is(i, SqlTokenKind.OpenParen))
            {
                var count = 1;
                for (var j = i + 1; j < Math.Min(_close[i], _t.Count); j = Next(j))
                {
                    if (Is(j, SqlTokenKind.Comma))
                    {
                        count++;
                    }
                }

                for (var n = 1; n <= count; n++)
                {
                    block.Output.Add(new SqlOutputItem($"column{n}"));
                }
            }

            ScanExpressions(block, i, e, depth);
        }

        private SqlSource? ReadTarget(ref int i, int e)
        {
            if (Kw(i, "only"))
            {
                i++;
            }

            if (i >= e || !IsName(i))
            {
                return null;
            }

            var start = _t[i].Start;
            var (schema, name) = ReadRelationName(ref i) ?? ("", "");
            var (alias, _) = ReadTargetAlias(ref i, e);
            return new SqlSource { Schema = schema, Name = name, Alias = alias, IsTarget = true, Start = start };
        }

        // A DML target's alias: never followed by a column list.
        private (string? Alias, object? _) ReadTargetAlias(ref int i, int e)
        {
            var explicitAs = Kw(i, "as");
            var j = explicitAs ? i + 1 : i;
            if (j < e && IsName(j) && (explicitAs || !(WordAt(j) is { } w && NotAnAlias.Contains(w))))
            {
                i = j + 1;
                return (NameAt(j), null);
            }

            return (null, null);
        }

        private void ReadInsert(SqlBlock block, int s, int e, int depth)
        {
            var i = s + 1;
            if (Kw(i, "into"))
            {
                i++;
            }

            if (ReadTarget(ref i, e) is { } target)
            {
                block.Sources.Add(target);
            }

            if (Is(i, SqlTokenKind.OpenParen))
            {
                block.InsertColumns = new SqlSpan(_t[i].End, InnerEnd(i));
                i = Next(i);
            }

            if (Kw(i, "overriding"))
            {
                i += 3;
            }

            // The source rows: a query that can't see the target.
            var sourceEnd = i;
            while (sourceEnd < e && !(Kw(sourceEnd, "on") && Kw(sourceEnd + 1, "conflict")) && !Kw(sourceEnd, "returning"))
            {
                sourceEnd = Next(sourceEnd);
            }

            if (i < sourceEnd && IsQueryStart(i))
            {
                var end = sourceEnd < _t.Count ? _t[sourceEnd].Start : _sql.Length;
                if (sourceEnd >= e)
                {
                    end = block.End;
                }

                block.Nested.Add(ReadQuery(i, sourceEnd, _t[i].Start, end, SqlQueryRole.Derived, block, null, depth + 1));
            }

            ReadClauses(block, sourceEnd, e, depth, fromWords: [], stopWords: new HashSet<string>());
        }

        private static readonly HashSet<string> UpdateStopWords = new(StringComparer.Ordinal) { "where", "returning" };

        private void ReadUpdate(SqlBlock block, int s, int e, int depth)
        {
            var i = s + 1;
            if (ReadTarget(ref i, e) is { } target)
            {
                block.Sources.Add(target);
            }

            ReadClauses(block, i, e, depth, fromWords: ["from"], stopWords: UpdateStopWords);
        }

        private void ReadDelete(SqlBlock block, int s, int e, int depth)
        {
            var i = s + 1;
            if (Kw(i, "from"))
            {
                i++;
            }

            if (ReadTarget(ref i, e) is { } target)
            {
                block.Sources.Add(target);
            }

            ReadClauses(block, i, e, depth, fromWords: ["using"], stopWords: UpdateStopWords);
        }

        private static readonly HashSet<string> MergeStopWords = new(StringComparer.Ordinal) { "on", "when", "returning" };

        private void ReadMerge(SqlBlock block, int s, int e, int depth)
        {
            var i = s + 1;
            if (Kw(i, "into"))
            {
                i++;
            }

            if (ReadTarget(ref i, e) is { } target)
            {
                block.Sources.Add(target);
            }

            ReadClauses(block, i, e, depth, fromWords: ["using"], stopWords: MergeStopWords);
        }
    }
}
