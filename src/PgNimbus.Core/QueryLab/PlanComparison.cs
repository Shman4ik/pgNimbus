using System.Globalization;
using PgNimbus.Core.Localization;
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
                return Strings.QueryLabCore_NoChange;
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
        < 10 => Strings.Core_UnitMilliseconds(ms.ToString("0.###", CultureInfo.InvariantCulture)),
        < 10_000 => Strings.Core_UnitMilliseconds(ms.ToString("#,0.#", CultureInfo.InvariantCulture)),
        _ => Strings.Core_UnitSeconds((ms / 1000).ToString("N1", CultureInfo.InvariantCulture)),
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

    /// <summary>
    /// Writes the figures' labels, the notes and the warnings again in the language now
    /// loaded, over the same alignment and tree: the expensive half of a comparison has
    /// no words in it, so a change of language costs only the cheap half.
    /// </summary>
    private Func<PlanComparison>? _describe;

    /// <summary>This comparison with its text in the language now loaded.</summary>
    public PlanComparison InCurrentLanguage() => _describe?.Invoke() ?? this;

    public static PlanComparison Compare(PlanSide before, PlanSide after)
    {
        var beforeMetrics = PlanMetrics.Compute(before.Plan);
        var afterMetrics = PlanMetrics.Compute(after.Plan);
        var alignment = PlanAlignment.Align(before.Plan, after.Plan);
        var diff = PlanDiff.Build(alignment, beforeMetrics, afterMetrics);
        return Describe(before, after, alignment, diff, beforeMetrics, afterMetrics);
    }

    private static PlanComparison Describe(
        PlanSide before,
        PlanSide after,
        PlanAlignment alignment,
        PlanDiffResult diff,
        IReadOnlyDictionary<ExplainNode, PlanNodeMetrics> beforeMetrics,
        IReadOnlyDictionary<ExplainNode, PlanNodeMetrics> afterMetrics)
    {
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
            notes)
        {
            _describe = () => Describe(before, after, alignment, diff, beforeMetrics, afterMetrics),
        };
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

        public string? MissingAnalyze => Analyzed ? null : Strings.QueryLabCore_NotAnalyzed;

        public string? MissingBuffers => !Analyzed ? Strings.QueryLabCore_NotAnalyzed
            : Buffers is null ? Plan.Capture.Buffers == false ? Strings.QueryLabCore_NoBuffers : Strings.QueryLabCore_BuffersUnknown
            : null;
    }

    private static List<PlanMetricRow> MetricRows(Facts a, Facts b)
    {
        var rows = new List<PlanMetricRow>
        {
            new(Strings.QueryLabCore_ExecutionTime, a.Plan.ExecutionTimeMs, b.Plan.ExecutionTimeMs, PlanMetricUnit.Milliseconds)
            {
                BeforeMissing = a.Plan.ExecutionTimeMs is null ? Strings.QueryLabCore_NotAnalyzed : null,
                AfterMissing = b.Plan.ExecutionTimeMs is null ? Strings.QueryLabCore_NotAnalyzed : null,
                Note = a.Plan.ExecutionTimeMs is not null && b.Plan.ExecutionTimeMs is not null ? Strings.QueryLabCore_OneRunEach : null,
            },
            new(Strings.QueryLabCore_PlanningTime, a.Plan.PlanningTimeMs, b.Plan.PlanningTimeMs, PlanMetricUnit.Milliseconds)
            {
                BeforeMissing = a.Plan.PlanningTimeMs is null ? Strings.QueryLabCore_NotReported : null,
                AfterMissing = b.Plan.PlanningTimeMs is null ? Strings.QueryLabCore_NotReported : null,
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

        PartOfExecution(Strings.QueryLabCore_JitTime, c => c.JitMs);
        PartOfExecution(Strings.QueryLabCore_TriggerTime, c => c.TriggerMs);

        rows.Add(new PlanMetricRow(Strings.QueryLabCore_RowsReturned, a.Root.ActualRowsTotal, b.Root.ActualRowsTotal, PlanMetricUnit.Rows)
        {
            BeforeMissing = a.Root.ActualRowsTotal is null ? a.MissingAnalyze ?? Strings.QueryLabCore_NotMeasured : null,
            AfterMissing = b.Root.ActualRowsTotal is null ? b.MissingAnalyze ?? Strings.QueryLabCore_NotMeasured : null,
        });
        rows.Add(new PlanMetricRow(Strings.QueryLabCore_RowsEstimated, a.Root.EstimatedRows, b.Root.EstimatedRows, PlanMetricUnit.Rows));
        rows.Add(new PlanMetricRow(Strings.QueryLabCore_EstimatedCost, a.Root.TotalCost, b.Root.TotalCost, PlanMetricUnit.PlannerUnits));

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

        Blocks(Strings.QueryLabCore_SharedHit, c => c.SharedHit, always: true);
        Blocks(Strings.QueryLabCore_SharedRead, c => c.SharedRead, always: true);
        Blocks(Strings.QueryLabCore_TempRead, c => c.TempRead, always: false);
        Blocks(Strings.QueryLabCore_TempWritten, c => c.TempWritten, always: false);

        var planningA = BufferCounts.From(a.Plan.Capture.PlanningBuffers);
        var planningB = BufferCounts.From(b.Plan.Capture.PlanningBuffers);
        if (planningA.Total > 0 || planningB.Total > 0)
        {
            rows.Add(new PlanMetricRow(
                Strings.QueryLabCore_PlanningBuffers, planningA.SharedHit + planningA.SharedRead, planningB.SharedHit + planningB.SharedRead,
                PlanMetricUnit.Blocks));
        }

        rows.Add(new PlanMetricRow(Strings.QueryLabCore_NodesToDisk, a.Analyzed ? a.Spills : null, b.Analyzed ? b.Spills : null, PlanMetricUnit.Count)
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
            Add(PlanWarningSeverity.Info, Strings.QueryLabCore_EstimatesOnlyTitle,
                !a.Analyzed && !b.Analyzed ? Strings.QueryLabCore_EstimatesOnlyNeither
                : !a.Analyzed ? Strings.QueryLabCore_EstimatesOnlyBefore
                : Strings.QueryLabCore_EstimatesOnlyAfter);
        }

        if (a.Plan.ExecutionTimeMs is not null && b.Plan.ExecutionTimeMs is not null)
        {
            Add(PlanWarningSeverity.Info, Strings.QueryLabCore_OneRunTitle, Strings.QueryLabCore_OneRunDetail);
        }

        if (a.Analyzed && b.Analyzed && (!a.Timed || !b.Timed))
        {
            Add(PlanWarningSeverity.Info, Strings.QueryLabCore_TimingOffTitle,
                !a.Timed && !b.Timed ? Strings.QueryLabCore_TimingOffBoth
                : !a.Timed ? Strings.QueryLabCore_TimingOffBefore
                : Strings.QueryLabCore_TimingOffAfter);
        }

        if (a.Buffers is { } ab && b.Buffers is { } bb)
        {
            var hi = Math.Max(ab.SharedRead, bb.SharedRead);
            var lo = Math.Min(ab.SharedRead, bb.SharedRead);
            if (hi >= CacheReadFloor && lo * 10 <= hi)
            {
                Add(PlanWarningSeverity.Warning, Strings.QueryLabCore_CacheTitle,
                    ab.SharedRead > bb.SharedRead
                        ? Strings.QueryLabCore_CacheBeforeMore(Number(hi), Number(lo))
                        : Strings.QueryLabCore_CacheAfterMore(Number(hi), Number(lo)));
            }
        }
        else if (a.Analyzed && b.Analyzed)
        {
            Add(PlanWarningSeverity.Info, Strings.QueryLabCore_CacheUnknownTitle,
                a.Buffers is null && b.Buffers is null ? Strings.QueryLabCore_CacheUnknownNeither
                : a.Buffers is null ? Strings.QueryLabCore_CacheUnknownBefore
                : Strings.QueryLabCore_CacheUnknownAfter);
        }

        if (before.ServerVersion is { } va && after.ServerVersion is { } vb && va != vb)
        {
            Add(PlanWarningSeverity.Warning, Strings.QueryLabCore_VersionsTitle, Strings.QueryLabCore_VersionsDetail(va, vb));
        }

        var costChanges = settings.Where(s => CostSettings.Contains(s.Name)).Select(s => s.Name).ToList();
        if (costChanges.Count > 0)
        {
            Add(PlanWarningSeverity.Warning, Strings.QueryLabCore_CostScalesTitle,
                Strings.QueryLabCore_CostScalesDetail(string.Join(", ", costChanges)));
        }

        if ((a.Plan.Capture.Settings is null) != (b.Plan.Capture.Settings is null))
        {
            Add(PlanWarningSeverity.Info, Strings.QueryLabCore_SettingsUnknownTitle,
                a.Plan.Capture.Settings is null ? Strings.QueryLabCore_SettingsUnknownBefore : Strings.QueryLabCore_SettingsUnknownAfter);
        }

        AddWorkerNotes(notes, alignment, beforeMetrics, afterMetrics);

        if ((a.Plan.Capture.JitMs is null) != (b.Plan.Capture.JitMs is null))
        {
            Add(PlanWarningSeverity.Info, Strings.QueryLabCore_JitTitle,
                a.Plan.Capture.JitMs is { } jitA
                    ? Strings.QueryLabCore_JitBefore(PlanMetricRow.Format(jitA, PlanMetricUnit.Milliseconds))
                    : Strings.QueryLabCore_JitAfter(PlanMetricRow.Format(b.Plan.Capture.JitMs!.Value, PlanMetricUnit.Milliseconds)));
        }

        if (before.Connection is { } ca && after.Connection is { } cb && ca != cb)
        {
            Add(PlanWarningSeverity.Info, Strings.QueryLabCore_ConnectionsTitle, Strings.QueryLabCore_ConnectionsDetail(ca, cb));
        }

        if (before.Statement is { } sa && after.Statement is { } sb && Normalize(sa) != Normalize(sb))
        {
            Add(PlanWarningSeverity.Info, Strings.QueryLabCore_StatementsTitle, Strings.QueryLabCore_StatementsDetail);
        }

        if (a.Root.ActualRowsTotal is { } rowsA && b.Root.ActualRowsTotal is { } rowsB && rowsA != rowsB)
        {
            Add(PlanWarningSeverity.Warning, Strings.QueryLabCore_RowsTitle, Strings.QueryLabCore_RowCountsDetail(
                rowsA.ToString("N0", CultureInfo.InvariantCulture), rowsB.ToString("N0", CultureInfo.InvariantCulture)));
        }

        if (diff.ShapeChanged)
        {
            var parts = new List<string>();
            if (diff.Added > 0)
            {
                parts.Add(Strings.QueryLabCore_ShapeAdded(diff.Added, Number(diff.Added)));
            }

            if (diff.Removed > 0)
            {
                parts.Add(Strings.QueryLabCore_ShapeRemoved(diff.Removed, Number(diff.Removed)));
            }

            if (diff.Moved > 0)
            {
                parts.Add(Strings.QueryLabCore_ShapeMoved(diff.Moved, Number(diff.Moved)));
            }

            // A list joined by the language's own words: "A and B", "A, B and C".
            var list = parts.Count switch
            {
                1 => parts[0],
                2 => Strings.QueryLabCore_ListTwo(parts[0], parts[1]),
                _ => Strings.QueryLabCore_ListThree(parts[0], parts[1], parts[2]),
            };
            var detail = Strings.QueryLabCore_ShapeDetail(list);
            Add(PlanWarningSeverity.Info, Strings.QueryLabCore_ShapeTitle, char.ToUpperInvariant(detail[0]) + detail[1..]);
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
        var shortBefore = beforeMetrics.Values.Any(m => m.WorkersLaunched < m.WorkersPlanned);
        var shortAfter = afterMetrics.Values.Any(m => m.WorkersLaunched < m.WorkersPlanned);
        if (shortBefore || shortAfter)
        {
            notes.Add(new PlanWarning(
                PlanWarningSeverity.Warning,
                Strings.QueryLabCore_WorkersShortTitle,
                shortBefore && shortAfter ? Strings.QueryLabCore_WorkersShortBoth
                : shortBefore ? Strings.QueryLabCore_WorkersShortBefore
                : Strings.QueryLabCore_WorkersShortAfter,
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
                    Strings.QueryLabCore_WorkersDiffTitle,
                    Strings.QueryLabCore_WorkersDiffDetail(
                        Strings.QueryLabCore_Workers(launchedA, Number(launchedA)),
                        Strings.QueryLabCore_Workers(launchedB, Number(launchedB))),
                    string.Empty,
                    null));
                return;
            }
        }
    }

    private static string Number(long count) => count.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>Statement text with runs of whitespace folded and the trailing semicolon dropped.</summary>
    private static string Normalize(string sql) =>
        string.Join(' ', sql.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).TrimEnd(';', ' ');
}
