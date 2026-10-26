using System.Globalization;
using PgNimbus.Core.Query;

namespace PgNimbus.Core.QueryLab;

/// <summary>One side of a comparison: a parsed plan and what is known about how it was measured.</summary>
public sealed record PlanSide(
    ExplainResult Plan,
    string? ServerVersion = null,
    string? Connection = null,
    string? Statement = null);

public enum PlanMetricUnit
{
    Milliseconds,
    Rows,
    Blocks,
    PlannerUnits,
    Count,
}

/// <summary>
/// A plan-level figure on both sides. A side without the figure carries the reason
/// instead ("not analyzed", "no BUFFERS"): a comparison that shows a blank as a zero
/// says something it doesn't know.
/// </summary>
public sealed record PlanMetricRow(string Label, double? Before, double? After, PlanMetricUnit Unit)
{
    public string? BeforeMissing { get; init; }

    public string? AfterMissing { get; init; }

    /// <summary>A caveat that belongs beside this figure ("one run each" beside execution time).</summary>
    public string? Note { get; init; }

    public string BeforeText => Before is { } value ? Format(value, Unit) : "—";

    public string AfterText => After is { } value ? Format(value, Unit) : "—";

    /// <summary>
    /// The difference, signed and without a verdict ("−811.2 ms (−99.8%)"). Whether less
    /// is good depends on the figure and on how it was measured, which is the notes' job.
    /// </summary>
    public string ChangeText
    {
        get
        {
            if (Before is not { } before || After is not { } after)
            {
                return string.Empty;
            }

            var delta = after - before;
            if (delta == 0)
            {
                return "no change";
            }

            var sign = delta < 0 ? "−" : "+";
            var text = sign + Format(Math.Abs(delta), Unit);
            if (before != 0)
            {
                text += string.Create(CultureInfo.InvariantCulture, $" ({sign}{Math.Abs(delta / before) * 100:0.#}%)");
            }

            return text;
        }
    }

    public static string Format(double value, PlanMetricUnit unit) => unit switch
    {
        PlanMetricUnit.Milliseconds => Milliseconds(value),
        PlanMetricUnit.PlannerUnits => value.ToString(value < 100 ? "0.##" : "N0", CultureInfo.InvariantCulture),
        _ => value.ToString(value % 1 == 0 ? "N0" : "N2", CultureInfo.InvariantCulture),
    };

    private static string Milliseconds(double ms) => ms switch
    {
        < 10 => ms.ToString("0.###", CultureInfo.InvariantCulture) + " ms",
        < 10_000 => ms.ToString("#,0.#", CultureInfo.InvariantCulture) + " ms",
        _ => (ms / 1000).ToString("N1", CultureInfo.InvariantCulture) + " s",
    };
}

/// <summary>A planner setting that differs: null on a side means it was at its default there.</summary>
public sealed record SettingChange(string Name, string? Before, string? After);

/// <summary>
/// Two plans side by side: the plan-level figures, the settings and warnings that
/// changed, the merged node tree, and the notes that say what the comparison can and
/// cannot show. Core-pure and deterministic, a sibling of <see cref="PlanAnalyzer"/>.
/// </summary>
/// <remarks>
/// Every string it produces stays neutral. Whether a run that took less time ran a
/// better plan depends on the cache, the data, the server and chance, and a comparison
/// of two single runs proves none of them; the notes say which of those it can see.
/// </remarks>
public sealed record PlanComparison(
    IReadOnlyList<PlanMetricRow> Metrics,
    IReadOnlyList<SettingChange> Settings,
    IReadOnlyList<PlanWarning> WarningsResolved,
    IReadOnlyList<PlanWarning> WarningsIntroduced,
    PlanDiffResult Diff,
    IReadOnlyList<PlanWarning> Notes)
{
    /// <summary>Settings that scale the planner's cost units: a difference in one makes the two costs incomparable.</summary>
    private static readonly HashSet<string> CostSettings =
    [
        "seq_page_cost", "random_page_cost", "cpu_tuple_cost", "cpu_index_tuple_cost", "cpu_operator_cost",
        "parallel_setup_cost", "parallel_tuple_cost", "effective_cache_size", "min_parallel_table_scan_size",
        "min_parallel_index_scan_size",
    ];

    /// <summary>A side read at least this many blocks outside shared buffers for cache warmth to be worth a note.</summary>
    private const long CacheReadFloor = 100;

    public static PlanComparison Compare(PlanSide before, PlanSide after)
    {
        var beforeMetrics = PlanMetrics.Compute(before.Plan);
        var afterMetrics = PlanMetrics.Compute(after.Plan);
        var alignment = PlanAlignment.Align(before.Plan, after.Plan);
        var diff = PlanDiff.Build(alignment, beforeMetrics, afterMetrics);

        var a = Facts.Of(before.Plan, beforeMetrics);
        var b = Facts.Of(after.Plan, afterMetrics);

        var (resolved, introduced) = CompareWarnings(before.Plan, after.Plan, alignment);
        var settings = CompareSettings(before.Plan.Capture.Settings, after.Plan.Capture.Settings);
        var metrics = MetricRows(a, b);
        var notes = new List<PlanWarning>();
        AddNotes(notes, before, after, a, b, settings, diff, alignment, beforeMetrics, afterMetrics);

        return new PlanComparison(
            metrics,
            settings,
            PlanAnalyzer.Condense(resolved),
            PlanAnalyzer.Condense(introduced),
            diff,
            notes);
    }

    /// <summary>The figures of one plan that the rows and notes read.</summary>
    private sealed record Facts(
        ExplainResult Plan,
        PlanNodeMetrics Root,
        bool Analyzed,
        bool Timed,
        BufferCounts? Buffers,
        int Spills)
    {
        public static Facts Of(ExplainResult plan, IReadOnlyDictionary<ExplainNode, PlanNodeMetrics> metrics)
        {
            var root = metrics[plan.Root];
            return new Facts(
                plan,
                root,
                Analyzed: root.Analyzed || plan.ExecutionTimeMs is not null,
                Timed: root.InclusiveMs is not null,
                Buffers: root.Buffers,
                Spills: metrics.Values.Count(m => m.Spill is not null));
        }

        public string? MissingAnalyze => Analyzed ? null : "not analyzed";

        public string? MissingBuffers => !Analyzed ? "not analyzed"
            : Buffers is null ? Plan.Capture.Buffers == false ? "no BUFFERS" : "BUFFERS unknown"
            : null;
    }

    private static List<PlanMetricRow> MetricRows(Facts a, Facts b)
    {
        var rows = new List<PlanMetricRow>
        {
            new("Execution time", a.Plan.ExecutionTimeMs, b.Plan.ExecutionTimeMs, PlanMetricUnit.Milliseconds)
            {
                BeforeMissing = a.Plan.ExecutionTimeMs is null ? "not analyzed" : null,
                AfterMissing = b.Plan.ExecutionTimeMs is null ? "not analyzed" : null,
                Note = a.Plan.ExecutionTimeMs is not null && b.Plan.ExecutionTimeMs is not null ? "one run each" : null,
            },
            new("Planning time", a.Plan.PlanningTimeMs, b.Plan.PlanningTimeMs, PlanMetricUnit.Milliseconds)
            {
                BeforeMissing = a.Plan.PlanningTimeMs is null ? "not reported" : null,
                AfterMissing = b.Plan.PlanningTimeMs is null ? "not reported" : null,
            },
        };

        // Parts of the execution time, shown when either side has them. An analyzed run
        // without one spent no time on it; a plan that wasn't analyzed doesn't say.
        void PartOfExecution(string label, Func<ExplainCapture, double?> pick)
        {
            if (pick(a.Plan.Capture) is null && pick(b.Plan.Capture) is null)
            {
                return;
            }

            rows.Add(new PlanMetricRow(
                label,
                a.Analyzed ? pick(a.Plan.Capture) ?? 0 : null,
                b.Analyzed ? pick(b.Plan.Capture) ?? 0 : null,
                PlanMetricUnit.Milliseconds)
            {
                BeforeMissing = a.MissingAnalyze,
                AfterMissing = b.MissingAnalyze,
            });
        }

        PartOfExecution("JIT time", c => c.JitMs);
        PartOfExecution("Trigger time", c => c.TriggerMs);

        rows.Add(new PlanMetricRow("Rows returned", a.Root.ActualRowsTotal, b.Root.ActualRowsTotal, PlanMetricUnit.Rows)
        {
            BeforeMissing = a.Root.ActualRowsTotal is null ? a.MissingAnalyze ?? "not measured" : null,
            AfterMissing = b.Root.ActualRowsTotal is null ? b.MissingAnalyze ?? "not measured" : null,
        });
        rows.Add(new PlanMetricRow("Rows estimated", a.Root.EstimatedRows, b.Root.EstimatedRows, PlanMetricUnit.Rows));
        rows.Add(new PlanMetricRow("Estimated cost (planner units)", a.Root.TotalCost, b.Root.TotalCost, PlanMetricUnit.PlannerUnits));

        void Blocks(string label, Func<BufferCounts, long> pick, bool always)
        {
            var before = a.Buffers is { } x ? pick(x) : (long?)null;
            var after = b.Buffers is { } y ? pick(y) : (long?)null;
            if (always || before > 0 || after > 0)
            {
                rows.Add(new PlanMetricRow(label, before, after, PlanMetricUnit.Blocks)
                {
                    BeforeMissing = a.MissingBuffers,
                    AfterMissing = b.MissingBuffers,
                });
            }
        }

        Blocks("Shared buffers hit", c => c.SharedHit, always: true);
        Blocks("Shared buffers read", c => c.SharedRead, always: true);
        Blocks("Temp blocks read", c => c.TempRead, always: false);
        Blocks("Temp blocks written", c => c.TempWritten, always: false);

        var planningA = BufferCounts.From(a.Plan.Capture.PlanningBuffers);
        var planningB = BufferCounts.From(b.Plan.Capture.PlanningBuffers);
        if (planningA.Total > 0 || planningB.Total > 0)
        {
            rows.Add(new PlanMetricRow(
                "Planning buffers", planningA.SharedHit + planningA.SharedRead, planningB.SharedHit + planningB.SharedRead,
                PlanMetricUnit.Blocks));
        }

        rows.Add(new PlanMetricRow("Nodes that went to disk", a.Analyzed ? a.Spills : null, b.Analyzed ? b.Spills : null, PlanMetricUnit.Count)
        {
            BeforeMissing = a.MissingAnalyze,
            AfterMissing = b.MissingAnalyze,
        });
        return rows;
    }

    /// <summary>
    /// The settings that differ, when both plans say what theirs were. EXPLAIN (SETTINGS)
    /// lists only the non-default ones, so a name on one side alone was at its default
    /// on the other.
    /// </summary>
    private static List<SettingChange> CompareSettings(
        IReadOnlyList<KeyValuePair<string, string>>? before, IReadOnlyList<KeyValuePair<string, string>>? after)
    {
        if (before is null || after is null)
        {
            return [];
        }

        var a = before.ToDictionary(s => s.Key, s => s.Value, StringComparer.Ordinal);
        var b = after.ToDictionary(s => s.Key, s => s.Value, StringComparer.Ordinal);
        return [.. a.Keys.Union(b.Keys, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Select(name => new SettingChange(name, a.GetValueOrDefault(name), b.GetValueOrDefault(name)))
            .Where(change => change.Before != change.After)];
    }

    /// <summary>
    /// The analyzer's warnings that went away and those that are new, matched through the
    /// node mapping by kind: a title carries a figure ("off by 120×") that differs between
    /// runs of one plan.
    /// </summary>
    private static (List<PlanWarning> Resolved, List<PlanWarning> Introduced) CompareWarnings(
        ExplainResult before, ExplainResult after, PlanAlignment alignment)
    {
        var a = PlanAnalyzer.Analyze(before);
        var b = PlanAnalyzer.Analyze(after);

        // Indexed by node: a plan over thousands of partitions can carry a warning per scan.
        var byNode = new Dictionary<ExplainNode, List<PlanWarning>>(ReferenceEqualityComparer.Instance);
        var nodeless = new List<PlanWarning>();
        foreach (var warning in b)
        {
            if (warning.Node is { } node)
            {
                if (!byNode.TryGetValue(node, out var list))
                {
                    byNode[node] = list = [];
                }

                list.Add(warning);
            }
            else
            {
                nodeless.Add(warning);
            }
        }

        var matched = new HashSet<PlanWarning>(ReferenceEqualityComparer.Instance);
        var resolved = new List<PlanWarning>();
        foreach (var warning in a)
        {
            var candidates = warning.Node is { } node
                ? alignment.AfterOf(node) is { } counterpart ? byNode.GetValueOrDefault(counterpart) : null
                : nodeless;
            var match = candidates?.FirstOrDefault(w =>
                !matched.Contains(w) && (w.Kind ?? w.Title) == (warning.Kind ?? warning.Title));
            if (match is not null)
            {
                matched.Add(match);
            }
            else
            {
                resolved.Add(warning);
            }
        }

        return (resolved, [.. b.Where(w => !matched.Contains(w))]);
    }

    private static void AddNotes(
        List<PlanWarning> notes,
        PlanSide before,
        PlanSide after,
        Facts a,
        Facts b,
        IReadOnlyList<SettingChange> settings,
        PlanDiffResult diff,
        PlanAlignment alignment,
        IReadOnlyDictionary<ExplainNode, PlanNodeMetrics> beforeMetrics,
        IReadOnlyDictionary<ExplainNode, PlanNodeMetrics> afterMetrics)
    {
        if (!a.Analyzed || !b.Analyzed)
        {
            var which = !a.Analyzed && !b.Analyzed ? "Neither plan was" : !a.Analyzed ? "The before plan wasn't" : "The after plan wasn't";
            Add(PlanWarningSeverity.Info, "Estimates only",
                $"{which} analyzed, so there is no time, actual row count or buffer use to compare there, only the "
                + "planner's estimates. Estimated cost is in planner units, not time.");
        }

        if (a.Plan.ExecutionTimeMs is not null && b.Plan.ExecutionTimeMs is not null)
        {
            Add(PlanWarningSeverity.Info, "One run each",
                "Each side is a single run. Timings move between runs, with other load on the server and with what "
                + "is already in memory, so run each a few times before reading much into the difference.");
        }

        if (a.Analyzed && b.Analyzed && (!a.Timed || !b.Timed))
        {
            var which = !a.Timed && !b.Timed ? "Both plans" : !a.Timed ? "The before plan" : "The after plan";
            Add(PlanWarningSeverity.Info, "Timing was off",
                $"{which} ran with TIMING OFF, so only the total execution time compares, not each node's.");
        }

        if (a.Buffers is { } ab && b.Buffers is { } bb)
        {
            var hi = Math.Max(ab.SharedRead, bb.SharedRead);
            var lo = Math.Min(ab.SharedRead, bb.SharedRead);
            if (hi >= CacheReadFloor && lo * 10 <= hi)
            {
                var (more, less) = ab.SharedRead > bb.SharedRead ? ("before", "after") : ("after", "before");
                Add(PlanWarningSeverity.Warning, "The cache may explain part of the difference",
                    string.Create(CultureInfo.InvariantCulture,
                        $"The {more} run read {hi:N0} blocks from outside shared buffers (the disk or the OS cache) and the {less} run read {lo:N0}. ")
                    + "A run that finds its data already in memory reads less from disk whatever its plan. Run both "
                    + "again in turn, or compare the buffers each node touched.");
            }
        }
        else if (a.Analyzed && b.Analyzed)
        {
            var which = a.Buffers is null && b.Buffers is null ? "Neither plan has" : a.Buffers is null ? "The before plan has no" : "The after plan has no";
            Add(PlanWarningSeverity.Info, "Cache use unknown",
                $"{which} buffer counts, so a warm cache can't be told apart from a change in the plan. "
                + "pgNimbus's Explain Analyze always asks for BUFFERS.");
        }

        if (before.ServerVersion is { } va && after.ServerVersion is { } vb && va != vb)
        {
            Add(PlanWarningSeverity.Warning, "Different PostgreSQL versions",
                $"The before plan comes from PostgreSQL {va} and the after plan from {vb}. The planner, its cost model "
                + "and the executor differ between versions.");
        }

        var costChanges = settings.Where(s => CostSettings.Contains(s.Name)).Select(s => s.Name).ToList();
        if (costChanges.Count > 0)
        {
            Add(PlanWarningSeverity.Warning, "Costs are on different scales",
                $"The settings that scale the planner's costs differ ({string.Join(", ", costChanges)}), so its "
                + "estimated costs are in different units on each side.");
        }

        if ((a.Plan.Capture.Settings is null) != (b.Plan.Capture.Settings is null))
        {
            Add(PlanWarningSeverity.Info, "Settings unknown on one side",
                $"The {(a.Plan.Capture.Settings is null ? "before" : "after")} plan doesn't say which planner settings "
                + "were changed from their defaults, so settings can't be compared.");
        }

        AddWorkerNotes(notes, alignment, beforeMetrics, afterMetrics);

        if ((a.Plan.Capture.JitMs is null) != (b.Plan.Capture.JitMs is null))
        {
            var (side, ms) = a.Plan.Capture.JitMs is { } jitA ? ("before", jitA) : ("after", b.Plan.Capture.JitMs!.Value);
            Add(PlanWarningSeverity.Info, "JIT compiled one side only",
                $"JIT took {PlanMetricRow.Format(ms, PlanMetricUnit.Milliseconds)} of the {side} run, and is part of its "
                + "execution time. It starts above jit_above_cost, so a change in estimates can switch it on or off.");
        }

        if (before.Connection is { } ca && after.Connection is { } cb && ca != cb)
        {
            Add(PlanWarningSeverity.Info, "Different connections",
                $"The before plan ran on {ca} and the after plan on {cb}: the data, the statistics and the server can differ.");
        }

        if (before.Statement is { } sa && after.Statement is { } sb && Normalize(sa) != Normalize(sb))
        {
            Add(PlanWarningSeverity.Info, "Different statements",
                "The two plans are for different statement text, so a difference can come from the rewrite as well "
                + "as from the data, the statistics or the indexes.");
        }

        if (a.Root.ActualRowsTotal is { } rowsA && b.Root.ActualRowsTotal is { } rowsB && rowsA != rowsB)
        {
            Add(PlanWarningSeverity.Warning, "Different rows returned",
                string.Create(CultureInfo.InvariantCulture,
                    $"The before run returned {rowsA:N0} rows and the after run {rowsB:N0}: the data or the statement ")
                + "changed between them, so they did not do the same work.");
        }

        if (diff.ShapeChanged)
        {
            Add(PlanWarningSeverity.Info, "The plan's shape changed",
                $"{Count(diff.Added, "node")} added, {Count(diff.Removed, "node")} removed and {Count(diff.Moved, "node")} "
                + "moved to another parent. The Plan changes tab shows where.");
        }

        void Add(PlanWarningSeverity severity, string title, string detail) =>
            notes.Add(new PlanWarning(severity, title, detail, string.Empty, null));
    }

    /// <summary>
    /// A Gather that launched fewer workers than it planned, or a different number than its
    /// counterpart: the server was short of workers, which moves time without the plan changing.
    /// </summary>
    private static void AddWorkerNotes(
        List<PlanWarning> notes,
        PlanAlignment alignment,
        IReadOnlyDictionary<ExplainNode, PlanNodeMetrics> beforeMetrics,
        IReadOnlyDictionary<ExplainNode, PlanNodeMetrics> afterMetrics)
    {
        var shortOn = new List<string>();
        foreach (var (side, metrics) in (ReadOnlySpan<(string, IReadOnlyDictionary<ExplainNode, PlanNodeMetrics>)>)
                 [("before", beforeMetrics), ("after", afterMetrics)])
        {
            if (metrics.Values.Any(m => m.WorkersLaunched < m.WorkersPlanned))
            {
                shortOn.Add(side);
            }
        }

        if (shortOn.Count > 0)
        {
            notes.Add(new PlanWarning(
                PlanWarningSeverity.Warning,
                "Fewer parallel workers than planned",
                $"A Gather launched fewer workers than it planned in the {string.Join(" and the ", shortOn)} run: "
                    + "the server had none to spare (max_parallel_workers), which changes the time without changing the plan.",
                string.Empty,
                null));
            return;
        }

        foreach (var (before, after) in alignment.Pairs)
        {
            if (beforeMetrics[before].WorkersLaunched is { } launchedA && afterMetrics[after].WorkersLaunched is { } launchedB
                && launchedA != launchedB)
            {
                notes.Add(new PlanWarning(
                    PlanWarningSeverity.Info,
                    "Different numbers of parallel workers",
                    $"A Gather ran with {Count(launchedA, "worker")} before and {Count(launchedB, "worker")} after, "
                        + "which changes its time without the plan changing.",
                    string.Empty,
                    null));
                return;
            }
        }
    }

    private static string Count(long count, string noun) =>
        string.Create(CultureInfo.InvariantCulture, $"{count:N0} {noun}{(count == 1 ? "" : "s")}");

    /// <summary>Statement text with runs of whitespace folded and the trailing semicolon dropped.</summary>
    private static string Normalize(string sql) =>
        string.Join(' ', sql.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).TrimEnd(';', ' ');
}
