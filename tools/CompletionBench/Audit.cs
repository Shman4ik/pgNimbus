using System.Text;
using Npgsql;
using PgNimbus.App.Completion;
using PgNimbus.Core.Schema;
using PgNimbus.Core.Text;

namespace PgNimbus.CompletionBench;

/// <summary>
/// The provider-level measurements of the completion audit
/// (docs/dev/design/sql-completion-audit-2.md, sections 2.3 and 8). They load a
/// catalog snapshot (<see cref="AuditCatalog"/>) and ask the real
/// <see cref="SqlCompletionProvider"/> and <see cref="CompletionRanker"/>
/// what the popup would hold — no editor, no server. The typing replay,
/// which needs the real editor, is <c>CompletionTypingReplayTests</c> in
/// PgNimbus.App.Tests.
/// </summary>
public static class Audit
{
    public static async Task<int> RunAsync(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8; // the reports carry ‸, →, ⏎
        switch (args[0])
        {
            case "dump" when args.Length >= 2:
                await DumpAsync(args[1], args.Length >= 3 ? args[2] : AuditCatalog.DefaultPath);
                return 0;
            case "quality":
                Console.Write(Quality(Provider(Arg(args, 1, AuditCatalog.DefaultPath)), AuditCatalog.LoadCorpus(Arg(args, 2, AuditCatalog.DefaultCorpusPath))));
                return 0;
            case "cases":
                Console.Write(Cases(Provider(Arg(args, 2, AuditCatalog.DefaultPath)), File.ReadAllLines(Arg(args, 1, AuditFile("cases.txt")))));
                return 0;
            case "hints":
                Console.Write(Hints(Provider(Arg(args, 2, AuditCatalog.DefaultPath)), File.ReadAllLines(Arg(args, 1, AuditFile("hints.txt")))));
                return 0;
            default:
                Console.Error.WriteLine("""
                    usage: CompletionBench                              latency bench (no arguments)
                           CompletionBench dump <connection-string> [catalog.json]
                           CompletionBench quality [catalog.json] [corpus.sql]
                           CompletionBench cases [cases.txt] [catalog.json]
                           CompletionBench hints [hints.txt] [catalog.json]
                    """);
                return 2;
        }
    }

    private static string Arg(string[] args, int index, string fallback) => args.Length > index ? args[index] : fallback;

    private static string AuditFile(string name) => Path.Combine(AppContext.BaseDirectory, "Audit", name);

    private static SqlCompletionProvider Provider(string catalogPath)
    {
        var provider = new SqlCompletionProvider(null);
        provider.Load(AuditCatalog.Load(catalogPath));
        return provider;
    }

    private static async Task DumpAsync(string connectionString, string path)
    {
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var catalog = await SqlCompletionProvider.ReadCatalogAsync(new SchemaService(dataSource), new HashSet<string>(), CancellationToken.None);
        AuditCatalog.Save(catalog, path);
        Console.WriteLine($"{path}: {catalog.Tables.Count} relations, {catalog.Tables.Sum(t => t.Columns.Count)} columns, "
            + $"{catalog.Functions.Count} functions, {catalog.BuiltinFunctions.Count} pg_catalog functions, {catalog.Types.Count} types, "
            + $"{catalog.ForeignKeys.Count} foreign keys, search_path [{string.Join(", ", catalog.SearchPath ?? [])}]");
    }

    // The rank of the intended row for every word of the corpus (two
    // characters or longer), after 1, 2 and 3 typed characters, typing left
    // to right: the text up to the word is all the provider sees. The
    // intended row is one whose name or label equals the word, ignoring case.
    // Words are grouped by what that row is; "member" is a word right after a
    // dot, "absent" one that no row names at all.
    private static string Quality(SqlCompletionProvider provider, IReadOnlyList<string> corpus)
    {
        var groups = new Dictionary<string, Tally>(StringComparer.Ordinal);
        var offeredWords = new Tally();
        var misses = new List<string>();
        // Why a word was never offered: no list can hold a name being made up,
        // one that is declared later in the query (package R, typing "columns
        // first"), or a DDL word (package N); anything else is a gap.
        var neverOffered = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var query in corpus)
        {
            var tokens = SqlLexer.Tokenize(query);
            for (var t = 0; t < tokens.Count; t++)
            {
                var token = tokens[t];
                if (token.Kind != SqlTokenKind.Word || token.Length < 2)
                {
                    continue;
                }

                var word = query[token.Start..token.End];
                var afterDot = t > 0 && tokens[t - 1].Kind == SqlTokenKind.Dot;
                var group = afterDot
                    ? "member"
                    : provider.GetCompletionData(query[..(token.Start + 1)], token.Start + 1)
                        .FirstOrDefault(i => Names(i, word, query, token.Start))?.Kind.ToString() ?? "absent";
                if (!groups.TryGetValue(group, out var tally))
                {
                    groups[group] = tally = new Tally();
                }

                tally.Words++;
                var offered = false;
                var ranks = new int[3];
                for (var typed = 1; typed <= 3; typed++)
                {
                    if (typed > word.Length)
                    {
                        ranks[typed - 1] = 0;
                        tally.Count(typed, 0); // typed in full: nothing left to complete
                        continue;
                    }

                    var rank = RankOf(provider, query, token.Start, token.Start + typed, word);
                    ranks[typed - 1] = rank;
                    offered |= rank >= 0;
                    tally.Count(typed, rank);
                    if (typed == 2 && rank != 0)
                    {
                        var text = query[..(token.Start + typed)];
                        misses.Add($"- {group} `{word}` after `…{text[Math.Max(0, text.Length - 30)..].Replace('\n', '⏎')}`: "
                            + (rank < 0 ? "not offered" : $"rank {rank + 1}"));
                    }
                }

                if (!offered)
                {
                    tally.NeverOffered++;
                    var reason = WhyNeverOffered(query, tokens, t);
                    neverOffered[reason] = neverOffered.GetValueOrDefault(reason) + 1;
                }
                else
                {
                    offeredWords.Words++;
                    for (var typed = 1; typed <= 3; typed++)
                    {
                        offeredWords.Count(typed, ranks[typed - 1]);
                    }
                }
            }
        }

        var output = new StringBuilder();
        output.AppendLine($"{corpus.Count} queries, {groups.Values.Sum(g => g.Words)} words.");
        output.AppendLine();
        output.AppendLine("| Words | Count | First @1 | @2 | @3 | Top 5 @1 | @2 | @3 | Never offered |");
        output.AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- | --- |");
        var all = new Tally();
        foreach (var (group, tally) in groups.OrderByDescending(g => g.Value.Words))
        {
            output.AppendLine(tally.Row(group));
            all.Add(tally);
        }

        output.AppendLine(all.Row("**all**"));
        output.AppendLine(offeredWords.Row("**offered**"));
        output.AppendLine();
        var words = groups.Values.Sum(g => g.Words);
        output.AppendLine($"Never offered: {all.NeverOffered} of {words} ({(double)all.NeverOffered / Math.Max(words, 1):P1}) — "
            + string.Join(", ", NeverOfferedReasons.Select(r => $"{r} {neverOffered.GetValueOrDefault(r)} ({(double)neverOffered.GetValueOrDefault(r) / Math.Max(words, 1):P1})")));
        output.AppendLine();
        output.AppendLine("Not first after two characters:");
        foreach (var miss in misses)
        {
            output.AppendLine(miss);
        }

        return output.ToString();
    }

    private static readonly string[] NeverOfferedReasons = ["new name", "declared later (R)", "DDL (N)", "other"];

    // Why the word at tokens[t] can't be offered while typing left to right:
    // it is a name being made up (an alias, an output name, a CTE's name);
    // the alias or name it uses is declared later in the query, so nothing
    // to the left knows it yet; it is in a DDL statement; or none of these.
    private static string WhyNeverOffered(string query, List<SqlToken> tokens, int t)
    {
        var token = tokens[t];
        if (SqlCompletionContext.IsNewNamePosition(query[..token.End], token.End))
        {
            return "new name";
        }

        // "c.first_name" before "FROM customers c": c is declared later.
        var name = SqlLexer.FoldCase(query.AsSpan(token.Start, token.Length));
        var qualifier = t >= 2 && tokens[t - 1].Kind == SqlTokenKind.Dot ? tokens[t - 2] : (SqlToken?)null;
        var used = qualifier is { } q ? SqlLexer.FoldCase(query.AsSpan(q.Start, q.Length)) : name;
        for (var later = t + 1; later < tokens.Count; later++)
        {
            if (tokens[later].Kind == SqlTokenKind.Word && SqlLexer.FoldCase(query.AsSpan(tokens[later].Start, tokens[later].Length)) == used
                && SqlCompletionContext.IsNewNamePosition(query[..tokens[later].End], tokens[later].End))
            {
                return "declared later (R)";
            }
        }

        var first = tokens.FirstOrDefault(x => x.Kind == SqlTokenKind.Word);
        return SqlLexer.FoldCase(query.AsSpan(first.Start, first.Length)) is "create" or "alter" or "drop" or "comment" or "grant"
            ? "DDL (N)"
            : "other";
    }

    // True when accepting `item` writes `word` (the word at `start` in
    // `query`): its name is the word, or it is a phrase row (ORDER BY, IS NOT
    // NULL) whose words are the ones the query goes on with.
    private static bool Names(SqlCompletionData item, string word, string query, int start)
    {
        if (string.Equals(item.Text, word, StringComparison.OrdinalIgnoreCase)
            || string.Equals(item.Label, word, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var end = start + item.Text.Length;
        return item.Kind == SqlCompletionKind.Keyword && item.Text.Contains(' ', StringComparison.Ordinal)
            && end <= query.Length && string.Compare(query, start, item.Text, 0, item.Text.Length, StringComparison.OrdinalIgnoreCase) == 0
            && (end == query.Length || !SqlLexer.IsIdentPart(query[end]));
    }

    // Where the word at `start` lands in the list the popup would show with
    // the caret at `caret` (its first characters typed); -1 when it isn't there.
    private static int RankOf(SqlCompletionProvider provider, string query, int start, int caret, string word)
    {
        var text = query[..caret];
        var filter = text[CompletionEdits.TokenAt(text, caret).FilterStart..];
        var ranked = CompletionRanker.Rank(provider.GetCompletionData(text, caret), filter, d => d.Text, d => d.Priority, _ => int.MaxValue);
        for (var i = 0; i < ranked.Items.Count; i++)
        {
            if (Names(ranked.Items[i], word, query, start))
            {
                return i;
            }
        }

        return -1;
    }

    private sealed class Tally
    {
        public int Words;
        public int NeverOffered;
        private readonly int[] _first = new int[3];
        private readonly int[] _topFive = new int[3];

        public void Count(int typed, int rank)
        {
            if (rank == 0)
            {
                _first[typed - 1]++;
            }

            if (rank is >= 0 and < 5)
            {
                _topFive[typed - 1]++;
            }
        }

        public void Add(Tally other)
        {
            Words += other.Words;
            NeverOffered += other.NeverOffered;
            for (var i = 0; i < 3; i++)
            {
                _first[i] += other._first[i];
                _topFive[i] += other._topFive[i];
            }
        }

        public string Row(string name) =>
            $"| {name} | {Words} | {Share(_first[0])} | {Share(_first[1])} | {Share(_first[2])} | "
            + $"{Share(_topFive[0])} | {Share(_topFive[1])} | {Share(_topFive[2])} | {NeverOffered} |";

        private string Share(int n) => Words == 0 ? "-" : $"{100.0 * n / Words:F0}%";
    }

    // One case per line: SQL with `|` for the caret (`\n` for a newline),
    // optionally prefixed with `[N]` for how many rows to print. `##` lines are
    // headings, `#` lines comments. Prints the ranked popup, the row it would
    // preselect, and whether Enter would take that row (CompletionAcceptance,
    // for a list that opened by itself).
    private static string Cases(SqlCompletionProvider provider, IEnumerable<string> lines)
    {
        var output = new StringBuilder();
        foreach (var raw in lines)
        {
            if (raw.StartsWith("##", StringComparison.Ordinal))
            {
                output.AppendLine().AppendLine(raw);
                continue;
            }

            if (string.IsNullOrWhiteSpace(raw) || raw.StartsWith('#'))
            {
                continue;
            }

            var line = raw;
            var top = 8;
            if (line.StartsWith('[') && line.IndexOf(']') is var close and > 0)
            {
                top = int.Parse(line[1..close], System.Globalization.CultureInfo.InvariantCulture);
                line = line[(close + 1)..].TrimStart();
            }

            var marked = line.Replace("\\n", "\n", StringComparison.Ordinal);
            var caret = marked.IndexOf('|', StringComparison.Ordinal);
            var text = marked.Remove(caret, 1);
            var items = provider.GetCompletionData(text, caret);
            var filter = text[CompletionEdits.TokenAt(text, caret).FilterStart..caret];
            var ranked = CompletionRanker.Rank(items, filter, d => d.Text, d => d.Priority, _ => int.MaxValue);
            output.AppendLine($"### `{line.Replace("|", "‸", StringComparison.Ordinal)}`");
            output.AppendLine($"clause={SqlCompletionContext.GetCaretContext(text, caret).Clause} candidates={items.Count} matched={ranked.Items.Count} filter=\"{filter}\"");
            for (var i = 0; i < Math.Min(top, ranked.Items.Count); i++)
            {
                var item = ranked.Items[i];
                var selected = i == ranked.SelectedIndex;
                var row = new CompletionRow(item.Text, item.InsertText, item.Kind == SqlCompletionKind.Keyword);
                var enter = !selected ? ""
                    : CompletionAcceptance.EnterAccepts(text, caret, caret - filter.Length, row, chosen: false) ? " [Enter takes it]"
                    : " [tentative]";
                var insert = item.InsertText == item.Label ? "" : $" → `{item.InsertText}`";
                output.AppendLine($"  {(selected ? ">" : " ")} {item.Kind,-13} {item.Label}{insert}  ({item.Detail}; p={item.Priority}){enter}");
            }

            if (ranked.Items.Count > top)
            {
                output.AppendLine($"    … {ranked.Items.Count - top} more");
            }
        }

        return output.ToString();
    }

    // One call per line, `|` for the caret: the argument hint shown there.
    private static string Hints(SqlCompletionProvider provider, IEnumerable<string> lines)
    {
        var output = new StringBuilder();
        foreach (var raw in lines)
        {
            if (string.IsNullOrWhiteSpace(raw) || raw.StartsWith('#'))
            {
                continue;
            }

            var caret = raw.IndexOf('|', StringComparison.Ordinal);
            var text = raw.Remove(caret, 1);
            output.AppendLine($"### `{raw.Replace("|", "‸", StringComparison.Ordinal)}`");
            if (provider.GetSignatureHints(text, caret) is not { } result)
            {
                output.AppendLine("  (no hint)");
                continue;
            }

            foreach (var hint in result.Hints.Take(6))
            {
                var parameters = string.Join(", ", hint.Parameters.Select((p, i) => i == hint.ActiveParameter ? $"**{p.Text}**" : p.Text));
                output.AppendLine($"  {hint.Schema}.{hint.Name}({parameters}) → {hint.ReturnType}");
            }

            if (result.Hints.Count > 6)
            {
                output.AppendLine($"  … +{result.Hints.Count - 6} more");
            }
        }

        return output.ToString();
    }
}
