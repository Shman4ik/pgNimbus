# Design: Smarter EXPLAIN / query-plan analysis

Status: in progress · Owner: pgNimbus · Created 2026-07-23

## Motivation

pgNimbus already renders `EXPLAIN` / `EXPLAIN ANALYZE` as a classic text
layout and a graphical cost-bar tree (`ExplainService`,
`ExplainTextFormatter`, `ExplainNodeViewModel`, the plan pane in
`ResultsGridPanel`). That puts us at roughly pgAdmin's "graphical tree"
tier.

The best-in-class plan tools — [pev2/Dalibo](https://github.com/dalibo/pev2),
[explain.depesz.com](https://explain.depesz.com),
[pgMustard](https://www.pgmustard.com), pganalyze — win not by drawing a
prettier tree but by **interpreting** it: they collect `BUFFERS`, heat-map on
*actual* time, and surface a named, prioritized list of problems (bad row
estimates, disk spills, seq-scans that filter everything away, lossy bitmap
heap blocks). Research notes and the competitive landscape are summarized in
the issue that spawned this work.

Concretely, the gaps in today's implementation:

1. **`BUFFERS` is explicitly disabled** (`ExplainService.cs`,
   `BUFFERS false`). This is the single most-requested EXPLAIN option across
   the ecosystem — buffers expose real I/O and are machine-independent, unlike
   timing.
2. **No problem detection.** We show raw numbers and leave interpretation to
   the user. This is the biggest differentiator versus pgAdmin/DBeaver/HeidiSQL.
3. **The visual bar is cost-based, not time-based.** `BarWidth =
   TotalCost / rootTotalCost` uses a planner *estimate*, and `TotalCost`
   includes children, so the bar double-counts down the tree. When ANALYZE
   data exists, the interesting quantity is each node's *self* (exclusive)
   time.
4. **`EXPLAIN ANALYZE` of a write actually runs the write** — no transaction
   guard. (Tracked here, addressed separately.)

## Scope of this change (v1)

Deliver the three highest-value, lowest-risk pieces as one `verify`-checked
change:

- **Enable `BUFFERS` (and `SETTINGS`)** on the ANALYZE path; suppress
  zero-valued buffer detail lines so the text view stays clean.
- **A pure, unit-tested plan analyzer** in `PgNimbus.Core` that walks the
  parsed tree and emits named `PlanWarning`s. Same shape as the existing
  Core-pure, sibling-of-`JsonTree`/`BlockingTree` analyzers.
- **Time-based heat** in the tree view: the cost bar becomes a *self-time*
  bar when ANALYZE timing is present (falling back to cost otherwise), and the
  single slowest node is tinted with the danger color as the bottleneck.
- **A warnings strip** above the plan views listing the analyzer's findings.

Out of scope for v1 (follow-ups, noted so they aren't forgotten):

- Write-statement safety guard (wrap `EXPLAIN ANALYZE <write>` in
  `BEGIN … ROLLBACK`).
- Paste-a-plan / import-external-plan entry point (`ExplainService.Parse` is
  already static and side-effect-free, so this is cheap later).
- Copy/export plan (raw JSON/text) for sharing into external tools.
- Re-color-by-metric toggle (time / rows / cost / buffers), pev2-style.
- Aggregating buffer counters onto a single `Buffers:` line in the text view
  to match `EXPLAIN (FORMAT TEXT)` exactly.

## Design

### Core: `PlanAnalyzer` + `PlanWarning`

`PgNimbus.Core/Query/PlanAnalyzer.cs` — a static class with
`Analyze(ExplainResult) : IReadOnlyList<PlanWarning>` that walks the node tree.
Pure and deterministic (no DB, no clock), so it is unit-tested against
captured JSON exactly like `ExplainService.Parse`.

```csharp
public enum PlanWarningSeverity { Info, Warning, Critical }

public sealed record PlanWarning(
    PlanWarningSeverity Severity,
    string Title,
    string Detail,
    string NodeType,
    string? Relation);
```

Heuristics in v1 (all computable from what we already parse — node type,
`PlanRows`, `ActualRows`, `ActualLoops`, and the `Details` key/value lines):

| Kind | Trigger | Severity |
|---|---|---|
| Row misestimate | `max(est,act) ≥ 100` and estimated-vs-actual (per-loop) off by ≥ 10× | Warning (≥ 100× → Critical) |
| Sort spilled to disk | `Sort Method` contains `external` | Warning |
| Hash spilled to disk | `Hash Batches` > 1 | Warning |
| Seq scan filters most rows | `Seq Scan`, `scanned ≥ 1000`, ≥ 90% removed by filter | Warning |
| Lossy bitmap heap blocks | `Lossy Heap Blocks` > 0 | Warning |

Each rule names the relation and gives an actionable one-liner (e.g. "raising
`work_mem` may keep this sort in memory", "consider an index on …"). Thresholds
are deliberately conservative to avoid noise; they live as named constants so
they're easy to tune.

### App: time heat

`ExplainNodeViewModel` gains:

- `SelfTimeMs` — exclusive time: `ActualTotalTimeMs × ActualLoops` minus the
  same for its children (clamped at 0).
- A post-construction `ApplyTimeHeat(maxSelfMs)` pass that sets `BarWidth` from
  `SelfTimeMs / maxSelfMs` when timing is present (otherwise the ctor's
  cost-ratio bar stands).
- `IsBottleneck` — true for the node with the largest self time, used to tint
  its bar with `AppDangerBrush`.

`QueryViewModel.RunExplainAsync` builds the VM tree, runs `ApplyTimeHeat`, and
sets `PlanWarnings` from `PlanAnalyzer.Analyze(result)`.

### App: warnings strip

An `ItemsControl` above the Text/Tree switch in `ResultsGridPanel`, visible
only when there are warnings, one row per finding: a severity glyph, the
title, and the detail. A thin `PlanWarningViewModel` wrapper maps severity to a
glyph/wash brush (keeping `PlanWarning` itself UI-free per hard rule 1).

## Testing

- `PlanAnalyzerTests` (TUnit, Core.Tests): one captured-JSON case per heuristic
  plus a clean plan that yields no warnings, and the row-estimate
  threshold/direction boundaries.
- Existing `ExplainParsingTests` continue to pass (option-string change is
  runtime-only; `Parse` is unaffected).
- End-to-end `verify` run against a seeded local Postgres: `EXPLAIN ANALYZE` a
  query that seq-scans + sorts to disk, confirm the warnings strip and the
  bottleneck tint render.

## Keeping CLAUDE.md current

`CLAUDE.md` has no dedicated EXPLAIN section today; the query-engine rules
don't mention plan analysis. Once v1 lands, add a short note under the
architecture rules describing `PlanAnalyzer` as a Core-pure, unit-tested
sibling of `BlockingTree`/`JsonTree`, and that `BUFFERS`/`SETTINGS` ride the
ANALYZE path.
