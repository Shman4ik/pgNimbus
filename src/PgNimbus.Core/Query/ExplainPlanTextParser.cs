using System.Globalization;
using System.Text.RegularExpressions;

namespace PgNimbus.Core.Query;

/// <summary>
/// Parses a pasted <c>EXPLAIN (FORMAT TEXT)</c> plan into the same
/// <see cref="ExplainResult"/> tree the JSON parser produces, so an imported
/// text plan flows through the exact same views, heat, and <see cref="PlanAnalyzer"/>
/// as a live one — no DB round-trip. Core-pure, deterministic, and unit-tested,
/// a read-only sibling of <see cref="ExplainService.Parse"/>.
///
/// Text is the lossy interchange format (JSON is preferred and robust), so this is
/// deliberately best-effort: it reconstructs the tree, cost/row/timing figures, and
/// the indented detail lines the analyzer reads (Sort Method, Rows Removed by Filter,
/// …), enough for the warnings and heat to work on the common node shapes. Anything
/// it genuinely can't make sense of raises <see cref="FormatException"/> so the import
/// UI can steer the user to the JSON form.
/// </summary>
public static class ExplainPlanTextParser
{
    /// <summary>
    /// The most text <see cref="Parse"/> takes, in characters. A pasted plan is a few
    /// hundred lines; a Run of <c>EXPLAIN</c> against a hostile server could return
    /// anything, and every line below is matched by a regex on the UI thread.
    /// </summary>
    public const int MaxInputLength = 4 * 1024 * 1024;

    /// <summary>
    /// How deep "->" children may nest. A join of many tables nests a level per
    /// join, so a real plan can pass 30; 128 leaves room for that while bounding
    /// every consumer of the tree (formatter, analyzer, the view models), which
    /// walks it recursively. The JSON form gets the same room through
    /// <see cref="ExplainService.MaxJsonDepth"/>.
    /// </summary>
    public const int MaxDepth = 128;

    // Every number is `\d+(?:\.\d+)?`, never `[\d.]+`: the latter overlaps with the
    // ".." that follows it, so "(cost=" + "."×n made the engine try O(n²) splits
    // before giving up (the audit's hang). NonBacktracking bounds the match to
    // linear time whatever the pattern, and the timeout is the belt to those braces.
    private const RegexOptions Options = RegexOptions.NonBacktracking | RegexOptions.CultureInvariant;
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(2);
    private const string Number = @"\d+(?:\.\d+)?";

    // "  (cost=0.00..41.88 rows=850 width=4)"
    private static readonly Regex CostRegex = new(
        $@"\(cost=(?<startup>{Number})\.\.(?<total>{Number})\s+rows=(?<rows>\d+)\s+width=(?<width>\d+)\)",
        Options, MatchTimeout);

    // "(actual time=0.009..0.021 rows=7.00 loops=1)" — the time= clause is absent
    // when ANALYZE ran with TIMING OFF, so it's optional here.
    private static readonly Regex ActualRegex = new(
        $@"\(actual\s+(?:time=(?<startup>{Number})\.\.(?<total>{Number})\s+)?rows=(?<rows>{Number})\s+loops=(?<loops>\d+)\)",
        Options, MatchTimeout);

    // Lines that end the plan tree and carry summary figures rather than nodes.
    private static readonly Regex PlanningTimeRegex = new($@"^Planning Time:\s*({Number})\s*ms", Options, MatchTimeout);
    private static readonly Regex ExecutionTimeRegex = new($@"^Execution Time:\s*({Number})\s*ms", Options, MatchTimeout);
    private static readonly Regex RowCountRegex = new(@"^\(\d+\s+rows?\)$", Options, MatchTimeout);

    /// <summary>
    /// Strips psql's framing from pasted output so the bare plan lines remain:
    /// a leading <c>QUERY PLAN</c> header and its <c>-----</c> underline, the trailing
    /// <c>(N rows)</c> count, and the single leading space psql pads every value line
    /// with (removed uniformly so relative indentation — which encodes the tree — is
    /// preserved). A plan copied straight from a tool without that framing is unaffected.
    /// </summary>
    public static string Clean(string raw)
    {
        var lines = raw.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').ToList();

        // Drop a leading blank run.
        while (lines.Count > 0 && lines[0].Trim().Length == 0)
        {
            lines.RemoveAt(0);
        }

        // psql column header + underline.
        if (lines.Count > 0 && lines[0].Trim() == "QUERY PLAN")
        {
            lines.RemoveAt(0);
            if (lines.Count > 0 && lines[0].TrimStart().StartsWith('-'))
            {
                lines.RemoveAt(0);
            }
        }

        // Trailing "(N rows)" / "(N row)" psql row count, and any trailing blanks.
        while (lines.Count > 0 && lines[^1].Trim().Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        if (lines.Count > 0 && RowCountRegex.IsMatch(lines[^1].Trim()))
        {
            lines.RemoveAt(lines.Count - 1);
        }

        // psql pads every content line with one leading space; strip it uniformly so
        // the root sits at column 0 and child indentation stays relative.
        if (lines.Where(l => l.Length > 0).All(l => l.StartsWith(' ')))
        {
            lines = [.. lines.Select(l => l.Length > 0 ? l[1..] : l)];
        }

        return string.Join("\n", lines);
    }

    public static ExplainResult Parse(string text)
    {
        if (text.Length > MaxInputLength)
        {
            throw new FormatException($"The plan is too large to import (over {MaxInputLength / (1024 * 1024)} MB of text).");
        }

        try
        {
            return ParseCore(text);
        }
        catch (RegexMatchTimeoutException ex)
        {
            throw new FormatException("A line of the plan took too long to read.", ex);
        }
    }

    private static ExplainResult ParseCore(string text)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        ParsedNode? root = null;
        var rootIndent = 0;
        double? planningTime = null;
        double? executionTime = null;
        List<KeyValuePair<string, string>>? settings = null;
        var planningBuffers = new List<KeyValuePair<string, string>>();
        double? jitMs = null;
        double? triggerMs = null;
        var sawBuffers = false;

        // Stack of (indent, node): a node's children are the deeper-indented "-> …"
        // lines that follow it; detail lines attach to the top of the stack.
        var stack = new List<(int Indent, ParsedNode Node)>();

        // Set once a top-level trailer section ("Planning:", "JIT:", "Triggers:") starts:
        // its indented lines describe the statement, not the last node parsed, so they
        // must not land in that node's details (which feed the buffers heat + analyzer).
        var inTrailer = false;
        string? trailer = null;

        // "InitPlan 1 (returns $0)", "SubPlan 2", "CTE recent": the header EXPLAIN prints
        // above the child it names, which FORMAT JSON carries as that child's
        // "Subplan Name" and "Parent Relationship" instead.
        string? pendingSubplan = null;

        foreach (var rawLine in lines)
        {
            if (rawLine.Trim().Length == 0)
            {
                continue;
            }

            var trimmedStart = rawLine.TrimStart();

            // Summary trailers close the tree.
            if (PlanningTimeRegex.Match(trimmedStart) is { Success: true } pt)
            {
                planningTime = Double(pt.Groups[1]);
                continue;
            }

            if (ExecutionTimeRegex.Match(trimmedStart) is { Success: true } et)
            {
                executionTime = Double(et.Groups[1]);
                continue;
            }

            var indent = rawLine.Length - trimmedStart.Length;
            var isChild = trimmedStart.StartsWith("->", StringComparison.Ordinal);

            if (root is null)
            {
                // First non-summary line is the root node header.
                root = ParseHeader(trimmedStart);
                if (root is null)
                {
                    throw new FormatException("This doesn't look like an EXPLAIN text plan — the first line has no plan node.");
                }

                rootIndent = indent;
                stack.Add((indent, root));
                continue;
            }

            // A bare top-level section header opens the post-tree trailer (see inTrailer).
            if (indent <= rootIndent && !isChild && trimmedStart.EndsWith(':') && !trimmedStart.Contains(": ", StringComparison.Ordinal))
            {
                inTrailer = true;
                trailer = trimmedStart[..^1];
                continue;
            }

            // Any other line at the root's indentation describes the statement, not a
            // node: "Settings: …", "Trigger t on orders: time=… calls=…", "Query Identifier: …".
            if (indent <= rootIndent && !isChild)
            {
                inTrailer = false;
                trailer = null;
                if (trimmedStart.StartsWith("Settings: ", StringComparison.Ordinal))
                {
                    settings = ParseSettings(trimmedStart["Settings: ".Length..]);
                }
                else if (trimmedStart.StartsWith("Trigger ", StringComparison.Ordinal) && TriggerTime(trimmedStart) is { } time)
                {
                    triggerMs = (triggerMs ?? 0) + time;
                }

                continue;
            }

            if (inTrailer && !isChild)
            {
                if (trailer == "Planning" && trimmedStart.StartsWith("Buffers: ", StringComparison.Ordinal))
                {
                    planningBuffers.AddRange(SplitBuffers(trimmedStart["Buffers: ".Length..]));
                }
                else if (trailer == "JIT" && trimmedStart.StartsWith("Timing: ", StringComparison.Ordinal))
                {
                    jitMs = JitTotal(trimmedStart) ?? jitMs;
                }

                continue;
            }

            if (isChild)
            {
                inTrailer = false;
                var node = ParseHeader(trimmedStart[2..].TrimStart())
                    ?? throw new FormatException($"Couldn't parse a plan node from: {trimmedStart}");
                if (pendingSubplan is not null)
                {
                    node.SubplanName = pendingSubplan;
                    node.ParentRelationship = pendingSubplan.StartsWith("SubPlan", StringComparison.Ordinal) ? "SubPlan" : "InitPlan";
                    pendingSubplan = null;
                }

                // Pop shallower/sibling frames so the new node hangs off the nearest
                // strictly-shallower ancestor.
                while (stack.Count > 0 && stack[^1].Indent >= indent)
                {
                    stack.RemoveAt(stack.Count - 1);
                }

                if (stack.Count == 0)
                {
                    throw new FormatException("The plan's indentation is inconsistent — couldn't place a child node.");
                }

                if (stack.Count >= MaxDepth)
                {
                    throw new FormatException($"The plan nests deeper than {MaxDepth} levels, more than pgNimbus reads.");
                }

                stack[^1].Node.Children.Add(node);
                stack.Add((indent, node));
                continue;
            }

            // A non-arrow, non-summary line is a detail ("Key: value") of the current
            // node. A subplan header names the child that follows; other bare
            // annotations without a colon aren't first-class detail and are skipped.
            var colon = trimmedStart.IndexOf(": ", StringComparison.Ordinal);
            if (colon < 0)
            {
                if (IsSubplanHeader(trimmedStart))
                {
                    pendingSubplan = trimmedStart.TrimEnd();
                }

                continue;
            }

            if (colon > 0 && stack.Count > 0)
            {
                var key = trimmedStart[..colon];
                var value = trimmedStart[(colon + 2)..].Trim();
                var node = stack[^1].Node;
                sawBuffers |= key == "Buffers";
                node.Details.AddRange(Expand(node.NodeType, key, value));
            }
        }

        if (root is null)
        {
            throw new FormatException("No plan node found in the pasted text.");
        }

        return new ExplainResult(root.ToExplainNode(), planningTime, executionTime)
        {
            Capture = new ExplainCapture
            {
                Format = PlanFormat.Text,
                // The text format prints a node's Buffers line only where a counter is
                // non-zero, so one proves the nodes were measured, and none can't prove
                // they weren't.
                Buffers = sawBuffers ? true : null,
                Settings = settings,
                PlanningBuffers = planningBuffers,
                JitMs = jitMs,
                TriggerMs = triggerMs,
            },
        };
    }

    private static bool IsSubplanHeader(string line) =>
        line.StartsWith("InitPlan", StringComparison.Ordinal)
        || line.StartsWith("SubPlan", StringComparison.Ordinal)
        || line.StartsWith("CTE ", StringComparison.Ordinal);

    /// <summary>
    /// A text detail line in the keys <c>FORMAT JSON</c> uses, where the text format folds
    /// several figures onto one line: <c>Buffers: shared hit=3 read=5, temp read=342</c>,
    /// <c>Heap Blocks: exact=10 lossy=5</c>, <c>Buckets: 1024  Batches: 2 (originally 1)
    /// Memory Usage: 9kB</c>, <c>Sort Method: external merge  Disk: 2400kB</c>. Splitting
    /// them is what lets the Buffers heat, the spill and lossy-bitmap warnings and a plan
    /// comparison read a pasted text plan the way they read a JSON one.
    /// </summary>
    private static IEnumerable<KeyValuePair<string, string>> Expand(string nodeType, string key, string value)
    {
        switch (key)
        {
            case "Buffers":
                return SplitBuffers(value);
            case "Heap Blocks":
                return SplitCounters(value, ("exact", "Exact Heap Blocks"), ("lossy", "Lossy Heap Blocks"));
            case "Buckets" or "Batches" or "Planned Partitions":
                return SplitSegments($"{key}: {value}", nodeType);
            case "Sort Method":
                return SplitSortMethod(value);
            default:
                return [new KeyValuePair<string, string>(key, value)];
        }
    }

    /// <summary><c>shared hit=3 read=5 dirtied=1 written=2, local …, temp read=342 written=342</c>.</summary>
    private static List<KeyValuePair<string, string>> SplitBuffers(string value)
    {
        var counters = new List<KeyValuePair<string, string>>();
        foreach (var pool in value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var words = pool.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length < 2 || words[0] is not ("shared" or "local" or "temp"))
            {
                continue;
            }

            var prefix = char.ToUpperInvariant(words[0][0]) + words[0][1..];
            foreach (var word in words[1..])
            {
                var equals = word.IndexOf('=');
                if (equals > 0 && Counter(word[(equals + 1)..]) is { } count)
                {
                    var kind = word[..equals];
                    counters.Add(new KeyValuePair<string, string>(
                        $"{prefix} {char.ToUpperInvariant(kind[0])}{kind[1..]} Blocks", count));
                }
            }
        }

        return counters;
    }

    /// <summary><c>exact=10 lossy=5</c> to the named keys.</summary>
    private static List<KeyValuePair<string, string>> SplitCounters(string value, params (string Word, string Key)[] names)
    {
        var counters = new List<KeyValuePair<string, string>>();
        foreach (var word in value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = word.IndexOf('=');
            if (equals <= 0 || Counter(word[(equals + 1)..]) is not { } count)
            {
                continue;
            }

            foreach (var (name, key) in names)
            {
                if (word[..equals] == name)
                {
                    counters.Add(new KeyValuePair<string, string>(key, count));
                }
            }
        }

        return counters;
    }

    /// <summary>
    /// A Hash or HashAggregate line, two spaces between figures:
    /// <c>Buckets: 4096 (originally 1024)  Batches: 2 (originally 1)  Memory Usage: 9kB</c>
    /// or <c>Batches: 5  Memory Usage: 4145kB  Disk Usage: 7680kB</c>.
    /// </summary>
    private static List<KeyValuePair<string, string>> SplitSegments(string line, string nodeType)
    {
        var details = new List<KeyValuePair<string, string>>();
        var batches = nodeType == "Hash" ? "Hash Batches" : "HashAgg Batches";
        foreach (var segment in line.Split("  ", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var colon = segment.IndexOf(": ", StringComparison.Ordinal);
            if (colon <= 0)
            {
                continue;
            }

            var name = segment[..colon];
            var figure = segment[(colon + 2)..];
            var original = figure.IndexOf(" (originally ", StringComparison.Ordinal);
            var current = Counter((original >= 0 ? figure[..original] : figure).Replace("kB", "", StringComparison.Ordinal));
            var was = original >= 0 ? Counter(figure[(original + " (originally ".Length)..].TrimEnd(')')) : null;
            switch (name)
            {
                case "Buckets":
                    Add("Hash Buckets", current);
                    Add("Original Hash Buckets", was ?? current);
                    break;
                case "Batches":
                    Add(batches, current);
                    if (nodeType == "Hash")
                    {
                        Add("Original Hash Batches", was ?? current);
                    }

                    break;
                case "Memory Usage":
                    Add("Peak Memory Usage", current);
                    break;
                case "Disk Usage":
                    Add("Disk Usage", current);
                    break;
                default:
                    details.Add(new KeyValuePair<string, string>(name, figure));
                    break;
            }
        }

        return details;

        void Add(string key, string? figure)
        {
            if (figure is not null)
            {
                details.Add(new KeyValuePair<string, string>(key, figure));
            }
        }
    }

    /// <summary><c>external merge  Disk: 2400kB</c> → Sort Method, Sort Space Type, Sort Space Used.</summary>
    private static List<KeyValuePair<string, string>> SplitSortMethod(string value)
    {
        var parts = value.Split("  ", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var details = new List<KeyValuePair<string, string>> { new("Sort Method", parts.Length > 0 ? parts[0] : value) };
        foreach (var part in parts.Skip(1))
        {
            var colon = part.IndexOf(": ", StringComparison.Ordinal);
            if (colon > 0 && part[..colon] is "Disk" or "Memory"
                && Counter(part[(colon + 2)..].Replace("kB", "", StringComparison.Ordinal)) is { } used)
            {
                details.Add(new KeyValuePair<string, string>("Sort Space Used", used));
                details.Add(new KeyValuePair<string, string>("Sort Space Type", part[..colon]));
            }
        }

        return details;
    }

    /// <summary>
    /// <c>work_mem = '64MB', random_page_cost = '1.1'</c>. EXPLAIN writes each value in
    /// single quotes without escaping, so a value ends at a quote followed by
    /// <c>, name = '</c> or the end of the line.
    /// </summary>
    private static List<KeyValuePair<string, string>> ParseSettings(string text)
    {
        var settings = new List<KeyValuePair<string, string>>();
        var position = 0;
        while (position < text.Length)
        {
            var equals = text.IndexOf(" = '", position, StringComparison.Ordinal);
            if (equals < 0)
            {
                break;
            }

            var name = text[position..equals].Trim();
            var valueStart = equals + " = '".Length;
            var end = valueStart;
            while (true)
            {
                var quote = text.IndexOf('\'', end);
                if (quote < 0)
                {
                    end = text.Length;
                    break;
                }

                if (quote == text.Length - 1 || IsNextSetting(text, quote + 1))
                {
                    end = quote;
                    break;
                }

                end = quote + 1;
            }

            settings.Add(new KeyValuePair<string, string>(name, text[valueStart..end]));
            position = Math.Min(text.Length, end + 1);
            if (position < text.Length && text[position] == ',')
            {
                position++;
            }
        }

        return settings;
    }

    /// <summary>True when <c>, name = '</c> follows <paramref name="at"/>.</summary>
    private static bool IsNextSetting(string text, int at)
    {
        if (at + 1 >= text.Length || text[at] != ',' || text[at + 1] != ' ')
        {
            return false;
        }

        var equals = text.IndexOf(" = '", at, StringComparison.Ordinal);
        return equals > at + 2 && text[(at + 2)..equals].All(c => char.IsLetterOrDigit(c) || c is '_' or '.');
    }

    /// <summary>The <c>time=1.234</c> of <c>Trigger t on orders: time=1.234 calls=5</c>.</summary>
    private static double? TriggerTime(string line)
    {
        var at = line.LastIndexOf(": time=", StringComparison.Ordinal);
        if (at < 0)
        {
            return null;
        }

        var figure = line[(at + ": time=".Length)..];
        var space = figure.IndexOf(' ');
        return Figure(space < 0 ? figure : figure[..space]);
    }

    /// <summary>The <c>Total 5.446 ms</c> of a JIT <c>Timing:</c> line.</summary>
    private static double? JitTotal(string line)
    {
        var at = line.IndexOf("Total ", StringComparison.Ordinal);
        if (at < 0)
        {
            return null;
        }

        var figure = line[(at + "Total ".Length)..];
        var space = figure.IndexOf(' ');
        return Figure(space < 0 ? figure : figure[..space]);
    }

    /// <summary>A whole-number counter as the invariant text JSON would carry, or null.</summary>
    private static string? Counter(string text) =>
        long.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value.ToString(CultureInfo.InvariantCulture)
            : null;

    private static double? Figure(string text) =>
        double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;

    /// <summary>
    /// Parses one node header line — the operator name plus the <c>(cost=…)</c> and
    /// <c>(actual …)</c> parentheticals — into a partial node. Returns null when the line
    /// carries no cost group (so it can't be a node), letting the caller reject it.
    /// </summary>
    private static ParsedNode? ParseHeader(string line)
    {
        var cost = CostRegex.Match(line);
        if (!cost.Success)
        {
            return null;
        }

        var name = line[..cost.Index].Trim();
        var (nodeType, relation, alias, indexName, scanDirection, parallelAware) = DecomposeName(name);

        var node = new ParsedNode
        {
            NodeType = nodeType,
            RelationName = relation,
            Alias = alias,
            IndexName = indexName,
            ScanDirection = scanDirection,
            ParallelAware = parallelAware,
            StartupCost = Double(cost.Groups["startup"]),
            TotalCost = Double(cost.Groups["total"]),
            // Counts saturate rather than overflow: a text plan is whatever was pasted,
            // and "rows=99999999999999999999" is a FormatException nobody wants, not a crash.
            PlanRows = Long(cost.Groups["rows"]),
            PlanWidth = (int)Math.Clamp(Long(cost.Groups["width"]), int.MinValue, int.MaxValue),
        };

        if (line.Contains("(never executed)", StringComparison.Ordinal))
        {
            node.ActualLoops = 0;
        }
        else if (ActualRegex.Match(line) is { Success: true } actual)
        {
            if (actual.Groups["total"].Success)
            {
                node.ActualStartupTimeMs = Double(actual.Groups["startup"]);
                node.ActualTotalTimeMs = Double(actual.Groups["total"]);
            }

            node.ActualRows = Double(actual.Groups["rows"]);
            node.ActualLoops = Long(actual.Groups["loops"]);
        }

        return node;
    }

    // The patterns only ever capture digits with at most one dot, so these cannot fail
    // to parse; a run of digits longer than a double reads as infinity, which Long saturates.
    private static double Double(Group group) =>
        double.TryParse(group.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : 0;

    private static long Long(Group group) => ExplainService.ToLong(Double(group)) ?? 0;

    /// <summary>
    /// Reverses the display-name assembly <see cref="ExplainTextFormatter.HeaderFor"/>
    /// does, enough to recover the base node type and scan target the analyzer keys on
    /// (e.g. "Parallel Seq Scan on t" → type "Seq Scan", relation "t"). Join/aggregate
    /// composite names ("Hash Left Join", "GroupAggregate") are kept whole as the node
    /// type — the analyzer doesn't special-case them, and <c>HeaderFor</c> round-trips
    /// them unchanged since their structured fields stay null.
    /// </summary>
    private static (string NodeType, string? Relation, string? Alias, string? IndexName, string? ScanDirection, bool ParallelAware)
        DecomposeName(string name)
    {
        var parallelAware = false;
        if (name.StartsWith("Parallel ", StringComparison.Ordinal))
        {
            parallelAware = true;
            name = name["Parallel ".Length..];
        }

        string? relation = null;
        string? alias = null;
        string? indexName = null;
        string? scanDirection = null;

        // "Index Scan [Backward] using <index> on <relation> [alias]"
        var usingIdx = name.IndexOf(" using ", StringComparison.Ordinal);
        if (usingIdx >= 0)
        {
            var head = name[..usingIdx];
            var tail = name[(usingIdx + " using ".Length)..];

            if (head.EndsWith(" Backward", StringComparison.Ordinal))
            {
                scanDirection = "Backward";
                head = head[..^" Backward".Length];
            }

            var onIdx = tail.IndexOf(" on ", StringComparison.Ordinal);
            if (onIdx >= 0)
            {
                indexName = tail[..onIdx];
                (relation, alias) = SplitTarget(tail[(onIdx + " on ".Length)..]);
            }
            else
            {
                indexName = tail;
            }

            return (head, relation, alias, indexName, scanDirection, parallelAware);
        }

        // "<Node Type> on <target>" — for a bitmap index scan the target is the index,
        // for everything else it's the scanned relation.
        var onlyOn = name.IndexOf(" on ", StringComparison.Ordinal);
        if (onlyOn >= 0)
        {
            var head = name[..onlyOn];
            var target = name[(onlyOn + " on ".Length)..];
            if (head == "Bitmap Index Scan")
            {
                indexName = target;
            }
            else
            {
                (relation, alias) = SplitTarget(target);
            }

            return (head, relation, alias, indexName, scanDirection, parallelAware);
        }

        return (name, null, null, null, null, parallelAware);
    }

    /// <summary>"orders o" → ("orders", "o"); "public.orders" → ("public.orders", null).</summary>
    private static (string Relation, string? Alias) SplitTarget(string target)
    {
        target = target.Trim();
        var space = target.IndexOf(' ');
        return space < 0 ? (target, null) : (target[..space], target[(space + 1)..].Trim());
    }

    /// <summary>Mutable builder mirroring the immutable <see cref="ExplainNode"/> record.</summary>
    private sealed class ParsedNode
    {
        public string NodeType { get; init; } = "";
        public string? RelationName { get; init; }
        public string? Alias { get; init; }
        public string? IndexName { get; init; }
        public string? ScanDirection { get; init; }
        public bool ParallelAware { get; init; }
        public double StartupCost { get; init; }
        public double TotalCost { get; init; }
        public long PlanRows { get; init; }
        public int PlanWidth { get; init; }
        public double? ActualStartupTimeMs { get; set; }
        public double? ActualTotalTimeMs { get; set; }
        public double? ActualRows { get; set; }
        public long? ActualLoops { get; set; }
        public string? SubplanName { get; set; }
        public string? ParentRelationship { get; set; }
        public List<KeyValuePair<string, string>> Details { get; } = [];
        public List<ParsedNode> Children { get; } = [];

        public ExplainNode ToExplainNode() => new(
            NodeType,
            RelationName,
            Alias,
            IndexName,
            JoinType: null,
            ScanDirection,
            SubplanName,
            Strategy: null,
            PartialMode: null,
            Operation: null,
            ParallelAware,
            StartupCost,
            TotalCost,
            PlanRows,
            PlanWidth,
            ActualStartupTimeMs,
            ActualTotalTimeMs,
            ActualRows,
            ActualLoops,
            Details,
            Children.Select(c => c.ToExplainNode()).ToList())
        {
            ParentRelationship = ParentRelationship,
        };
    }
}
