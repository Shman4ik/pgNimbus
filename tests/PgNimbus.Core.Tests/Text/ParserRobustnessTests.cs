using PgNimbus.Core.Query;
using PgNimbus.Core.Text;

namespace PgNimbus.Core.Tests.Text;

/// <summary>
/// Every SQL reader that runs on the UI thread — per keystroke, on Run, or on a
/// paste — answers on any text at all, within a bounded time and a bounded
/// stack (security audit 2026-09, finding 16). The generative tests that existed
/// covered the lexer, the browse parser, the scope model and the command grammar
/// with short random word runs; these draw the same hostility from
/// <see cref="HostileText"/> for every reader, including a hundred thousand
/// nested parentheses, which used to overflow the stack in the scope reader
/// (<c>IsQueryStart</c> recursed per "(") and the keyword grammar
/// (<c>Governing</c> recursed per unclosed group).
/// </summary>
public class ParserRobustnessTests
{
    private const int Depth = 100_000;
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);

    private static readonly int[] CaretSteps = [0, 1, 2, 3, 5, 8, 13];

    /// <summary>The caret positions a probe is run at: the ends, a few from each end, and the middle.</summary>
    private static IEnumerable<int> Carets(string sql)
    {
        yield return sql.Length / 2;
        foreach (var step in CaretSteps)
        {
            if (step <= sql.Length)
            {
                yield return step;
                yield return sql.Length - step;
            }
        }
    }

    private static void Exercise(Action<string, int> probe)
    {
        var inputs = HostileText.EveryCharAtEveryPosition()
            .Concat(HostileText.Random(20260928, 2000))
            .Concat(HostileText.RandomWords(20260928, 2000));
        foreach (var sql in inputs)
        {
            foreach (var caret in Carets(sql))
            {
                probe(sql, caret);
            }
        }
    }

    /// <summary>Runs <paramref name="probe"/> over every deep-nesting input on a small stack; returns how many probes ran.</summary>
    private static int ExerciseDeep(Action<string, int> probe)
    {
        var ran = 0;
        foreach (var sql in HostileText.DeepNesting(Depth))
        {
            HostileText.RunBounded(
                () =>
                {
                    foreach (var caret in Carets(sql))
                    {
                        probe(sql, caret);
                        ran++;
                    }
                },
                Limit);
        }

        return ran;
    }

    // --- per keystroke ---

    [Test]
    public async Task The_scope_model_reads_any_text()
    {
        var read = 0;
        Exercise((sql, caret) =>
        {
            var model = SqlScopeModel.Parse(sql);
            if (model.BlockAt(caret, out _) is { } block)
            {
                _ = SqlScopeModel.VisibleSources(block);
                _ = SqlScopeModel.VisibleCtes(block);
                _ = SqlScopeModel.IsAssignmentTarget(sql, block, caret);
                read++;
            }
        });

        await Assert.That(read).IsGreaterThan(0);
    }

    [Test]
    [Timeout(240_000)]
    public async Task The_scope_model_reads_a_hundred_thousand_nested_parens(CancellationToken ct)
    {
        // The audit's paste: SELECT (((…(1)…))). It is read on a 256 KB stack here
        // (RunBounded), a quarter of what any production thread has, so the depth
        // cap leaves headroom rather than just barely fitting.
        var opaque = false;
        var ran = ExerciseDeep((sql, caret) =>
        {
            var model = SqlScopeModel.Parse(sql);
            _ = model.BlockAt(caret, out var unknown);
            opaque |= unknown;
        });

        // A caret in the deepest group is inside something the reader stopped at.
        var deep = SqlScopeModel.Parse(HostileText.DeepParens(Depth));
        _ = deep.BlockAt("SELECT ".Length + Depth, out var unknownAtCentre);
        await Assert.That(ran).IsGreaterThan(0);
        await Assert.That(unknownAtCentre).IsTrue();
        await Assert.That(opaque).IsTrue();
    }

    [Test]
    public async Task The_completion_context_reads_any_text()
    {
        var read = 0;
        Exercise((sql, caret) =>
        {
            _ = SqlCompletionContext.GetCaretContext(sql, caret);
            _ = SqlCompletionContext.IsAtStatementStart(sql, caret);
            _ = SqlCompletionContext.CompletionStatementSpan(sql, caret);
            _ = SqlCompletionContext.GetQualifierChainBeforeCaret(sql, caret);
            _ = SqlCompletionContext.IsAfterOnKeyword(sql, caret);
            _ = SqlCompletionContext.IsAfterCompleteJoinTarget(sql, caret);
            _ = SqlCompletionContext.IsAfterCompleteFromItem(sql, caret);
            _ = SqlCompletionContext.ExtractUsingColumns(sql, out _);
            _ = SqlCompletionContext.ExtractTables(sql);
            _ = SqlCompletionContext.ExtractCteNames(sql);
            read++;
        });

        await Assert.That(read).IsGreaterThan(0);
    }

    [Test]
    [Timeout(240_000)]
    public async Task The_completion_context_reads_a_hundred_thousand_nested_parens(CancellationToken ct)
    {
        var ran = ExerciseDeep((sql, caret) =>
        {
            _ = SqlCompletionContext.GetCaretContext(sql, caret);
            _ = SqlCompletionContext.IsAtStatementStart(sql, caret);
            _ = SqlCompletionContext.CompletionStatementSpan(sql, caret);
            _ = SqlCompletionContext.GetQualifierChainBeforeCaret(sql, caret);
            _ = SqlCompletionContext.IsAfterCompleteJoinTarget(sql, caret);
            _ = SqlCompletionContext.IsAfterCompleteFromItem(sql, caret);
            _ = SqlCompletionContext.ExtractTables(sql);
        });

        await Assert.That(ran).IsGreaterThan(0);
    }

    [Test]
    [Timeout(120_000)]
    public async Task Thousands_of_nested_ctes_read_quickly(CancellationToken ct)
    {
        // Each CTE body was read on its own and a nested WITH's outer bodies hold
        // every inner one: 4,000 of them took ~46 s of completion work, per popup,
        // and 2,000 (42k characters, under the size where completion moves off the
        // UI thread) 3.4 s. Capped, the definitions past the limit are skipped.
        const int depth = 4_000;
        var sql = string.Concat(Enumerable.Repeat("WITH q AS (", depth)) + "SELECT 1"
            + string.Concat(Enumerable.Repeat(") SELECT 1", depth));

        IReadOnlyList<SqlCompletionContext.CteDefinition> definitions = [];
        HostileText.RunBounded(
            () =>
            {
                definitions = SqlCompletionContext.ExtractCteDefinitions(sql);
                _ = SqlCompletionContext.GetCaretContext(sql, sql.Length);
                _ = SqlCompletionContext.IsNewNamePosition(sql, sql.Length);
            },
            TimeSpan.FromSeconds(5));

        await Assert.That(definitions.Count).IsLessThanOrEqualTo(SqlCompletionContext.MaxCteDefinitions);
    }

    [Test]
    public async Task The_formatter_hands_back_text_nested_past_its_limit()
    {
        static string Nested(int depth) => "SELECT * FROM " + string.Concat(Enumerable.Repeat("(SELECT * FROM ", depth))
            + "t" + string.Concat(Enumerable.Repeat(") x", depth));

        var shallow = Nested(3);
        var deep = Nested(SqlFormatter.MaxNestingDepth + 1);

        await Assert.That(SqlFormatter.Format(shallow)).IsNotEqualTo(shallow);
        await Assert.That(SqlFormatter.Format(deep)).IsEqualTo(deep);
    }

    [Test]
    public async Task A_browse_where_nested_past_its_limit_is_an_ordinary_query()
    {
        static string Where(int depth) => "SELECT * FROM t WHERE " + new string('(', depth) + "a = 1" + new string(')', depth) + " LIMIT 100";

        await Assert.That(BrowseSqlParser.TryParse(Where(2), "public", "t", [])).IsNotNull();
        await Assert.That(BrowseSqlParser.TryParse(Where(BrowseSqlParser.MaxParenDepth + 1), "public", "t", [])).IsNull();
    }

    [Test]
    public async Task The_call_site_reads_any_text()
    {
        var read = 0;
        Exercise((sql, caret) =>
        {
            _ = SqlCallSite.At(sql, caret);
            _ = SqlCallSite.ValuesRowAt(sql, caret);
            read++;
        });
        _ = SqlParameters.Parse(new string('(', 1000) + ", =>" + new string(')', 1000));

        await Assert.That(read).IsGreaterThan(0);
    }

    [Test]
    [Timeout(240_000)]
    public async Task The_call_site_reads_a_hundred_thousand_nested_parens(CancellationToken ct)
    {
        var ran = ExerciseDeep((sql, caret) =>
        {
            _ = SqlCallSite.At(sql, caret);
            _ = SqlCallSite.ValuesRowAt(sql, caret);
        });

        await Assert.That(ran).IsGreaterThan(0);
    }

    [Test]
    public async Task The_statement_cache_reads_any_text()
    {
        var read = 0;
        var boundaries = new SqlStatementBoundaries();
        Exercise((sql, caret) =>
        {
            boundaries.Reset();
            _ = boundaries.Span(sql, caret);
            boundaries.Invalidate(caret / 2);
            _ = boundaries.Span(sql, caret);
            read++;
        });

        await Assert.That(read).IsGreaterThan(0);
    }

    [Test]
    [Timeout(240_000)]
    public async Task The_statement_cache_reads_a_hundred_thousand_nested_parens(CancellationToken ct)
    {
        var boundaries = new SqlStatementBoundaries();
        var ran = ExerciseDeep((sql, caret) =>
        {
            boundaries.Reset();
            _ = boundaries.Span(sql, caret);
        });

        await Assert.That(ran).IsGreaterThan(0);
    }

    [Test]
    public async Task The_keyword_grammar_reads_any_text()
    {
        var read = 0;
        Exercise((sql, caret) =>
        {
            _ = SqlKeywordGrammar.At(sql, caret);
            _ = SqlKeywordGrammar.IsInJoinCondition(sql, caret);
            read++;
        });

        await Assert.That(read).IsGreaterThan(0);
    }

    [Test]
    [Timeout(240_000)]
    public async Task The_keyword_grammar_reads_a_hundred_thousand_nested_parens(CancellationToken ct)
    {
        // Governing() used to recurse once per unclosed "(" — and copy the token list
        // each time — so "SELECT (((((" was both a stack overflow and O(n²) memory.
        var ran = ExerciseDeep((sql, caret) =>
        {
            _ = SqlKeywordGrammar.At(sql, caret);
            _ = SqlKeywordGrammar.IsInJoinCondition(sql, caret);
        });

        await Assert.That(ran).IsGreaterThan(0);
    }

    [Test]
    public async Task The_value_slot_reads_any_text()
    {
        var read = 0;
        Exercise((sql, caret) =>
        {
            _ = SqlValueSlot.At(sql, caret);
            read++;
        });

        await Assert.That(read).IsGreaterThan(0);
    }

    [Test]
    [Timeout(240_000)]
    public async Task The_value_slot_reads_a_hundred_thousand_nested_parens(CancellationToken ct)
    {
        var ran = ExerciseDeep((sql, caret) => _ = SqlValueSlot.At(sql, caret));

        await Assert.That(ran).IsGreaterThan(0);
    }

    // --- on Run and on format ---

    [Test]
    public async Task The_script_splitter_reads_any_text()
    {
        var read = 0;
        Exercise((sql, caret) =>
        {
            _ = SqlScriptSplitter.Split(sql);
            _ = SqlScriptSplitter.StatementAt(sql, caret);
            _ = SqlScriptSplitter.StatementSpanAt(sql, caret);
            read++;
        });

        await Assert.That(read).IsGreaterThan(0);
    }

    [Test]
    [Timeout(240_000)]
    public async Task The_script_splitter_reads_a_hundred_thousand_nested_parens(CancellationToken ct)
    {
        var ran = ExerciseDeep((sql, caret) =>
        {
            _ = SqlScriptSplitter.Split(sql);
            _ = SqlScriptSplitter.StatementAt(sql, caret);
            _ = SqlScriptSplitter.StatementSpanAt(sql, caret);
        });

        await Assert.That(ran).IsGreaterThan(0);
    }

    [Test]
    public async Task The_statement_inspector_reads_any_text()
    {
        var read = 0;
        Exercise((sql, caret) =>
        {
            _ = SqlStatementInspector.IsDataModifying(sql);
            _ = SqlStatementInspector.IsExplain(sql);
            _ = SqlStatementInspector.StripExplain(sql);
            _ = SqlStatementInspector.ChangesCatalog(sql);
            _ = SqlStatementInspector.SetsSearchPath(sql);
            read++;
        });

        await Assert.That(read).IsGreaterThan(0);
    }

    [Test]
    [Timeout(240_000)]
    public async Task The_statement_inspector_reads_a_hundred_thousand_nested_parens(CancellationToken ct)
    {
        // ChangesCatalog reaches the scope model after every Run (SELECT … INTO).
        var ran = ExerciseDeep((sql, caret) =>
        {
            _ = SqlStatementInspector.IsDataModifying(sql);
            _ = SqlStatementInspector.IsExplain(sql);
            _ = SqlStatementInspector.StripExplain(sql);
            _ = SqlStatementInspector.ChangesCatalog(sql);
            _ = SqlStatementInspector.SetsSearchPath(sql);
        });

        await Assert.That(ran).IsGreaterThan(0);
    }

    [Test]
    public async Task The_formatter_reads_any_text()
    {
        var read = 0;
        Exercise((sql, caret) =>
        {
            _ = SqlFormatter.Format(sql);
            read++;
        });

        await Assert.That(read).IsGreaterThan(0);
    }

    [Test]
    [Timeout(240_000)]
    public async Task The_formatter_reads_a_hundred_thousand_nested_parens(CancellationToken ct)
    {
        var ran = ExerciseDeep((sql, caret) => _ = SqlFormatter.Format(sql));

        await Assert.That(ran).IsGreaterThan(0);
    }

    // --- on a plan paste or a Run of a hand-written EXPLAIN ---

    [Test]
    public async Task The_plan_text_parser_reads_any_text()
    {
        // FormatException is the one documented answer for text that is not a plan;
        // anything else escaping here used to reach the crash window.
        var parsed = 0;
        var refused = 0;
        var shapes = new[]
        {
            "Seq Scan on t  (cost=0.00..41.88 rows=850 width=4) (actual time=0.009..0.021 rows=7.00 loops=1)\n  Filter: (a = 1)\n  ->  Index Scan using i on u  (cost=0.1..1.2 rows=1 width=8)\nPlanning Time: 0.1 ms\nExecution Time: 0.2 ms\n",
            "QUERY PLAN\n----------\n Result  (cost=0.00..0.01 rows=1 width=4)\n(1 row)\n",
        };
        var inputs = shapes.SelectMany(shape => Enumerable.Range(0, shape.Length + 1).SelectMany(at => HostileText.Alphabet.Select(c => shape.Insert(at, c.ToString()))))
            .Concat(HostileText.Random(20260928, 2000))
            .Concat(HostileText.EveryCharAtEveryPosition());
        foreach (var text in inputs)
        {
            try
            {
                _ = ExplainService.Import(text);
                parsed++;
            }
            catch (FormatException)
            {
                refused++;
            }
        }

        await Assert.That(parsed).IsGreaterThan(0);
        await Assert.That(refused).IsGreaterThan(0);
    }

    [Test]
    [Timeout(120_000)]
    public async Task The_plan_text_parser_is_bounded(CancellationToken ct)
    {
        // (cost= followed by a hundred thousand dots: the old [\d.]+\.\.[\d.]+ regex
        // tried every split of the dot run before failing, O(n²) on the UI thread.
        var dots = "Seq Scan on t  (cost=" + new string('.', Depth) + " rows=1 width=4)";
        // A node per line, each one deeper: the parser's own depth cap, so the
        // formatter, the analyzer and the view models never recurse past it.
        var deep = string.Join('\n', Enumerable.Range(0, 200).Select(i =>
            i == 0 ? "Result  (cost=0.00..0.01 rows=1 width=4)" : new string(' ', i * 2) + "->  Result  (cost=0.00..0.01 rows=1 width=4)"));
        // Every number the text form carries, past what a long or an int can hold.
        var huge = "Seq Scan on t  (cost=99999999999999999999999.9..1 rows=99999999999999999999 width=99999999999) (actual time=1..2 rows=1.5 loops=99999999999999999999)\nPlanning Time: 99999999999999999999999 ms";
        var oversize = new string('x', ExplainPlanTextParser.MaxInputLength + 1);

        string? dotsOutcome = null, deepOutcome = null, oversizeOutcome = null;
        ExplainResult? hugeResult = null;
        HostileText.RunBounded(
            () =>
            {
                dotsOutcome = Outcome(dots);
                deepOutcome = Outcome(deep);
                oversizeOutcome = Outcome(oversize);
                hugeResult = ExplainService.Import(huge).Result;
            },
            TimeSpan.FromSeconds(20),
            stackBytes: 1024 * 1024);

        await Assert.That(dotsOutcome).IsEqualTo("FormatException");
        await Assert.That(deepOutcome).IsEqualTo("FormatException");
        await Assert.That(oversizeOutcome).IsEqualTo("FormatException");
        await Assert.That(hugeResult!.Root.PlanRows).IsEqualTo(long.MaxValue);
        await Assert.That(hugeResult.Root.PlanWidth).IsEqualTo(int.MaxValue);
        await Assert.That(hugeResult.Root.ActualLoops).IsEqualTo(long.MaxValue);

        static string Outcome(string text)
        {
            try
            {
                _ = ExplainService.Import(text);
                return "parsed";
            }
            catch (FormatException)
            {
                return "FormatException";
            }
        }
    }
}
