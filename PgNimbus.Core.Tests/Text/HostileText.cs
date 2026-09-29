namespace PgNimbus.Core.Tests.Text;

/// <summary>
/// The inputs the "any text at all" tests feed a parser that runs on the UI
/// thread: every printable ASCII character (plus the ones the lexer treats
/// specially) at every position of a few SQL shapes, seeded random strings,
/// and nesting deep enough to find a recursion per paren. Shared so each
/// parser's test draws the same hostility rather than its own smaller sample —
/// the audit of 2026-09 found the generative tests that existed capped their
/// random text at 30 words, which can never reach a depth cap.
/// </summary>
public static class HostileText
{
    /// <summary>Every printable ASCII character, a newline, a tab, and two non-ASCII letters.</summary>
    public static readonly char[] Alphabet = [.. Enumerable.Range(32, 95).Select(c => (char)c), .. "\n\tЖ€"];

    /// <summary>A few statement shapes with the tricky lexical forms in them.</summary>
    public static readonly string[] Shapes =
    [
        "SELECT E'a\\'b', (SELECT 1) FROM t x JOIN u ON x.id = u.id WHERE a IN (1, 2) -- c\n",
        "WITH q AS ($t$x$t$) INSERT INTO t (a) VALUES (1) ON CONFLICT (a) DO UPDATE SET a = excluded.a RETURNING *;",
        "UPDATE t SET a = coalesce(b, ARRAY[1, /* /* */ */ 2]) WHERE \"q\"\"q\" = $1::int;",
        "EXPLAIN (ANALYZE, FORMAT JSON) SELECT count(*) FILTER (WHERE x) OVER (), CASE WHEN a THEN b END FROM t GROUP BY 1",
        "",
    ];

    /// <summary>Each shape with each character of the alphabet inserted at each position.</summary>
    public static IEnumerable<string> EveryCharAtEveryPosition()
    {
        foreach (var shape in Shapes)
        {
            for (var at = 0; at <= shape.Length; at++)
            {
                foreach (var c in Alphabet)
                {
                    yield return shape.Insert(at, c.ToString());
                }
            }
        }
    }

    /// <summary><paramref name="count"/> random strings of the alphabet, up to <paramref name="maxLength"/> characters, from a fixed seed.</summary>
    public static IEnumerable<string> Random(int seed, int count, int maxLength = 60)
    {
        var random = new Random(seed);
        for (var n = 0; n < count; n++)
        {
            var chars = new char[random.Next(0, maxLength + 1)];
            for (var i = 0; i < chars.Length; i++)
            {
                chars[i] = Alphabet[random.Next(Alphabet.Length)];
            }

            yield return new string(chars);
        }
    }

    /// <summary><paramref name="count"/> random strings of SQL words, up to <paramref name="maxWords"/> long, from a fixed seed.</summary>
    public static IEnumerable<string> RandomWords(int seed, int count, int maxWords = 40)
    {
        const string alphabet = "SELECT FROM WHERE WITH UNION JOIN LATERAL ( ) [ ] , . * \"q\" 'x' AS ON USING RETURNING VALUES INSERT INTO UPDATE SET DELETE MERGE WHEN THEN CASE END OVER FILTER GROUP BY ORDER AND OR NOT IN EXISTS CAST :: = a b t u ; -- /* */ $1 1 'a''b'";
        var words = alphabet.Split(' ');
        var random = new Random(seed);
        for (var n = 0; n < count; n++)
        {
            yield return string.Join(' ', Enumerable.Range(0, random.Next(1, maxWords + 1)).Select(_ => words[random.Next(words.Length)]));
        }
    }

    /// <summary>The audit's paste: <c>SELECT </c>, <paramref name="depth"/> open parens, <c>1</c>, as many close parens.</summary>
    public static string DeepParens(int depth) => "SELECT " + new string('(', depth) + "1" + new string(')', depth);

    /// <summary>The same nesting, never closed: what the editor holds while the parens are being typed.</summary>
    public static string DeepOpenParens(int depth) => "SELECT " + new string('(', depth);

    /// <summary>A FROM whose join tree nests <paramref name="depth"/> parentheses deep.</summary>
    public static string DeepJoinTree(int depth) =>
        "SELECT * FROM " + new string('(', depth) + "t JOIN u ON t.id = u.id" + new string(')', depth);

    /// <summary>The nesting inputs, plus brackets and a subquery per level.</summary>
    public static IEnumerable<string> DeepNesting(int depth) =>
    [
        DeepParens(depth),
        DeepOpenParens(depth),
        DeepJoinTree(depth),
        "SELECT " + new string('[', depth),
        "SELECT " + string.Concat(Enumerable.Repeat("(SELECT ", depth)),
        "SELECT " + string.Concat(Enumerable.Repeat("coalesce(", depth)),
        "SELECT 1 WHERE " + string.Concat(Enumerable.Repeat("a IN (", depth)),
    ];

    /// <summary>
    /// Runs <paramref name="work"/> on a thread with a <paramref name="stackBytes"/> stack
    /// and fails the test if it does not finish within <paramref name="timeout"/>. A
    /// stack overflow kills the test process outright, so the stack is deliberately
    /// smaller than the 1 MB every production thread has: a parser that fits here has
    /// headroom there. The exception, if any, is rethrown on the calling thread.
    /// </summary>
    public static void RunBounded(Action work, TimeSpan timeout, int stackBytes = 256 * 1024)
    {
        Exception? failure = null;
        var thread = new Thread(
            () =>
            {
                try
                {
                    work();
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
            },
            stackBytes)
        { IsBackground = true };
        thread.Start();
        if (!thread.Join(timeout))
        {
            throw new TimeoutException($"The parser did not finish within {timeout.TotalSeconds:F0} s.");
        }

        if (failure is not null)
        {
            throw new InvalidOperationException("The parser threw.", failure);
        }
    }
}
