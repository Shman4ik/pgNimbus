# pgNimbus roadmap

What might come next, and the evidence behind each item. For what ships today, see the [README](README.md) and the [documentation](https://shman4ik.github.io/pgNimbus/docs/).

The backlog itself is tracked as [GitHub issues labeled `roadmap`](https://github.com/Shman4ik/pgNimbus/issues?q=is%3Aissue%20is%3Aopen%20label%3Aroadmap), with a priority (`P0`–`P2`) and a size (`size: S`, `M`, `L`). Scope, "done when" and constraints for each item are in its issue. This file keeps the direction, the evidence, one line per item, and the P3 ideas that nobody has asked for yet.

## Direction and competitive evidence

Research snapshot: **2026-09-21**. This is a proposed backlog, not a list of available features or a delivery commitment. Priorities are product hypotheses based on the source and public competitor documentation; validate demand with user interviews before investing in the larger items.

**Backlog review: 2026-09-27.** No item here has outside demand yet. Every open issue came from this roadmap, and no user has filed a feature request. So the order is: first, gaps a user can hit today; then parity features that people expect from any PostgreSQL client; and the large (L) items only after a pilot user asks for them. Items dropped in that review are listed [at the end](#dropped-after-review), each with its reason, so they don't come back unexamined.

**Primary audience:** PostgreSQL developers who investigate slow queries, fix production data, and debug access problems. The proposed positioning is **a fast, local PostgreSQL workbench that makes changes reviewable and performance improvements explainable**. Keep startup speed, NativeAOT, streaming, cancellation, and zero telemetry as constraints on every release.

| Evidence from competitors | Implication for pgNimbus |
| --- | --- |
| TablePlus already offers Safe mode and generated-SQL review ([official overview](https://tableplus.com/blog/2017/07/10-hidden-gems-in-tableplus.html)). | Staging edits alone is not a new category. Extend it with conflict detection and consistent production safeguards. |
| DBeaver supports graphical execution plans and schema comparison; its documented Schema Compare is in Enterprise/Ultimate ([plans](https://dbeaver.com/docs/dbeaver/Query-Execution-Plan/), [schema comparison](https://dbeaver.com/docs/dbeaver/Schema-compare/)). | A plan viewer is baseline functionality. A compact before/after workflow and an MIT-licensed PostgreSQL schema diff are stronger reasons to switch. |
| pgAdmin offers AI reports and query/plan assistance ([official AI documentation](https://www.pgadmin.org/docs/pgadmin4/9.18/ai_tools.html)). | Adding a generic AI chat is unlikely to be a sufficient launch story. Prioritize useful workflows that work without a model or account. |
| Beekeeper Studio advertises AI assistance and shared cloud workspaces ([official product page](https://www.beekeeperstudio.io/)). | Test demand for file-based team workflows without an account; avoid taking on a collaboration backend before the core workflows are compelling. |

These are documented capability comparisons, not measured speed comparisons or claims that competitors lack every proposed workflow. Any future performance claim needs a reproducible, versioned benchmark.

## What the current implementation makes possible

The existing foundations are substantial: staged grid changes, FK navigation, offline plan import, plan warnings, database statistics, a blocking tree, and schema DDL reconstruction. The source also already includes a [security workspace](src/PgNimbus.App/ViewModels/Security/SecurityViewModel.cs), [effective-privilege explanations](src/PgNimbus.Core/Security/EffectivePrivilegeResolver.cs), and [RLS policy inspection](src/PgNimbus.App/ViewModels/Security/RlsTabViewModel.cs); role management and a policy list should not be proposed as new features.

Specific gaps drive the first priorities:

- [CredentialStore](src/PgNimbus.Core/Connections/CredentialStore.cs) now selects protected platform storage with verified legacy migration and a visible session-memory fallback; see [where your password goes](https://shman4ik.github.io/pgNimbus/docs/getting-started/connecting/#where-your-password-goes) for storage details and remaining legacy files.
- [PendingChangeSet](src/PgNimbus.Core/Query/PendingChangeSet.cs) now re-reads and locks every staged row at commit and compares it with the row as loaded (T2), so a concurrent change rolls the batch back instead of being overwritten.
- [ConnectionProfile](src/PgNimbus.Core/Connections/ConnectionProfile.cs) has connection colors and SSL modes, but no environment policy or configurable query deadline; its command timeout is currently unlimited.
- [ExplainService](src/PgNimbus.Core/Query/ExplainService.cs) and [PlanAnalyzer](src/PgNimbus.Core/Query/PlanAnalyzer.cs) already parse and explain individual plans. Comparing saved runs is the next increment, not rebuilding visualization.
- [TableBrowseViewModel](src/PgNimbus.App/ViewModels/TableBrowseViewModel.cs) pages with `LIMIT/OFFSET`, and the grid holds at most 100,000 rows. Export no longer depends on either (D1): it runs the query again and streams every row to the file.

## P0: Trust and everyday adoption

Ship these before a broad production-use or cross-platform launch. Scope labels are relative: **S** = contained change, **M** = workflow across a few components, **L** = substantial subsystem; they are not calendar estimates.

- [x] **T1 · Native credential storage and accurate privacy wording (M).** Shipped: macOS Keychain and Linux Secret Service, verified migration of legacy files, and a visible session-memory fallback. See [Privacy](README.md#-privacy).
- [x] **T2 · Conflict-aware Safe mode (L).** Shipped: every staged row is re-read and locked at commit, and a concurrent change rolls the batch back. See [Safe mode](docs/guide/results.md#safe-mode).
- [ ] **T3 · Production connection policies (L) · [#373](https://github.com/Shman4ik/pgNimbus/issues/373).** Read-only sessions shipped ([Read-only connections](https://shman4ik.github.io/pgNimbus/docs/getting-started/connecting/#read-only-connections)). Environment labels, deliberate write enablement, and one policy for every path are left.
- [x] **T4 · Row detail editor and visual filters (M).** Shipped: a row-details form whose edits always stage, and server-side filter chips in table browsing. See [Filtering rows](docs/guide/results.md#filtering-rows) and [Row details](docs/guide/results.md#row-details).
- [ ] **T5 · Signed and notarized macOS builds (M) · [#374](https://github.com/Shman4ik/pgNimbus/issues/374).** Developer ID signing and notarization for the .dmg, real-device checks, and an in-place upgrade check for the .deb. Windows needs no paid signing.

## P1: Features worth announcing

Deliver the following as small, reviewable increments. The first campaign should be **Query Lab**; T1–T3 remain trust work, not optional marketing polish.

- [ ] **Q1 · EXPLAIN baselines and plan diff (L) · [#375](https://github.com/Shman4ik/pgNimbus/issues/375).** Save named runs and compare two of them, imported or live.
- [x] **Q2 · Slow-query shortlist (M).** Shipped: a Slow queries window over `pg_stat_statements`, with interval deltas, reset detection, and a separate explanation for each reason the data is unavailable. Prompting for typed values is left to [#138](https://github.com/Shman4ik/pgNimbus/issues/138). See [Slow queries](https://shman4ik.github.io/pgNimbus/docs/guide/monitoring/#slow-queries).
- [ ] **S1 · Schema snapshots and drift report (L) · [#376](https://github.com/Shman4ik/pgNimbus/issues/376).** Compare two connections, or a connection against a local snapshot.
- [ ] **R1 · RLS access investigation (L) · [#377](https://github.com/Shman4ik/pgNimbus/issues/377).** A bounded, read-only preview of the rows a role can see, with the policies that apply.
- [x] **D1 · Export the whole result, not the grid (S).** Shipped: a browsed table's export runs its query again and streams every row to the file. Other queries are never run again and write the rows in the grid. See [Import and export](docs/guide/results.md#import-and-export). Keyset pagination was dropped in the 2026-09-27 review (see [below](#dropped-after-review)).

## Launch backlog and evidence

The headlines below are proposals to use **after** the corresponding capabilities ship. Each campaign needs a short screencast, a reproducible example, and an explicit statement of limitations.

| Order / release story | Minimum deliverable | Demonstration and validation |
| --- | --- | --- |
| 1. **“See exactly what changed in your PostgreSQL query plan.”** | Q1 + Q3; Q2 can follow. | Compare a seeded query before/after a reviewed index change, including a case with no improvement. Publish the workload and measurement conditions. Ask pilot users to identify the reason for the difference without assistance. |
| 2. **“Catch conflicting data edits before they overwrite someone else's work.”** | T2 + T3. | Two sessions edit the same row; show the conflict, rollback, and successful restaging. Cover editor/import policy paths as well as the grid in release checks. |
| 3. **“Find PostgreSQL schema drift without a cloud account.”** | S1. | Compare dev/staging snapshots and expose a missing constraint and changed default. Publish supported object coverage and an example diff that can be reviewed in Git. |
| 4. **“See what each tenant role can read.”** | R1. | A two-tenant fixture with different policies, including an owner-bypass case. Pilot users should explain the visible rows and identify when the preview cannot answer a write-access question. |

- [ ] **L1 · Repeatable first-run demo (M) · [#378](https://github.com/Shman4ik/pgNimbus/issues/378).** Reach the `scripts/demo/` fixture from the app, with guided tasks.
- [ ] **L2 · Fair comparison kit (S) · [#357](https://github.com/Shman4ik/pgNimbus/issues/357).** Benchmarks for large-result memory, deep paging, and cancellation latency. Publish raw results before making new superiority claims.
- [ ] **L3 · Validate adoption without telemetry (S) · [#379](https://github.com/Shman4ik/pgNimbus/issues/379).** A small pilot group with consented task-completion observations.

## P2: Reduce switching costs and deepen PostgreSQL workflows

- [ ] **Typed query parameters (M) · [#138](https://github.com/Shman4ik/pgNimbus/issues/138).** Prompt for `:name` / `$1` values with PostgreSQL types and NULL support; do not persist sensitive values by default. The minimal typed input that Q2's statements need comes first.
- [ ] **ER diagram (L) · [#380](https://github.com/Shman4ik/pgNimbus/issues/380).** A table and its FK neighbors first, then a schema-wide layout and SVG export.
- [ ] **Backup/restore UI (L) · [#381](https://github.com/Shman4ik/pgNimbus/issues/381).** Over `pg_dump`/`pg_restore`, with a command preview that holds no secrets and an explicit restore target.
- [ ] **Maintenance insights (M) · [#382](https://github.com/Shman4ik/pgNimbus/issues/382).** Stale statistics, dead tuples, long transactions, and vacuum progress in Database Overview.
- [ ] **Hotkey remapping (M) · [#383](https://github.com/Shman4ik/pgNimbus/issues/383).** A remap layer over the command catalog.
- [ ] **macOS platform polish (M) · [#384](https://github.com/Shman4ik/pgNimbus/issues/384).** Vibrancy, sheet-style dialogs, native context menus, and sidebar/title-bar integration.

## P3: Validate before committing

These have no issues yet, on purpose: each is a hypothesis that needs a request from a user first. If you'd use one, open an issue that links its line here.

- [ ] **Privacy-first AI / MCP (L).** Explore a narrow explanation or query-drafting workflow only after the deterministic workflows above. Local models first; remote providers require explicit opt-in and an exact data preview. Any MCP surface needs connection-scoped permissions, read-only defaults, bounded results, and explicit approval for execution. Revisit the current network/privacy wording before shipping; do not market “AI” alone as the differentiator.
- [ ] **Notebook mode (L).** Validate demand for mixed SQL/Markdown with local result snapshots after portable SQL projects; define secret handling and snapshot size limits first.
- [ ] **Plugin API (L).** Defer until repeated extension needs justify a stable, NativeAOT-compatible contract; investigate process isolation and permissions before allowing third-party code access to connections.
- [ ] **Flatpak distribution (M).** Validate demand beyond the existing Linux packages, including sandbox access to Secret Service and external PostgreSQL tools.
- [ ] **Reviewable performance report (M; depends on Q1).** Export a local HTML/Markdown before/after report with plan changes, observed timings, buffers, and reproducibility notes. Allow users to omit SQL, literals, identifiers, and connection metadata, then preview the exact export; do not promise automatic anonymization. New runs must be explicit: [EXPLAIN ANALYZE executes the statement](https://www.postgresql.org/docs/current/sql-explain.html), and rollback does not undo every possible side effect. Moved here from P1 (it was Q3) in the 2026-09-27 review: it only matters once people compare plans (Q1), and nobody has asked for a report format yet.
- [ ] **Local hang diagnostics (M).** A UI-thread watchdog with bounded local diagnostics and a recovery message; no dump or log is ever uploaded. Moved here from P0 (it was T6) in the 2026-09-27 review. Under NativeAOT a process can't read another thread's stack, so the watchdog could report *that* the UI stalled but not *where*. The two hangs found so far (in the browse-query parser and the SQL lexer) were fixed, and tests now feed those two every kind of input and fail if they don't finish.
- [ ] **Portable SQL projects and connection import (M).** Group saved queries, snippets, parameter definitions, and schema snapshots in a versioned folder suitable for Git. Start with one documented external connection-profile format; preview imported fields and omit passwords/SSH secrets. Keep profiles local and resolve project connection aliases explicitly.
- [ ] **Result comparison (M).** Compare two bounded result snapshots by an explicit key; surface duplicate keys, NULL/type differences, and truncation. Export a local diff; cross-database data synchronization is outside the first increment.

## Dropped after review

These were on the backlog and were dropped in the 2026-09-27 review. None of them had a request from anyone outside the project. If you'd use one, comment on its issue and it can come back.

- **Vim keybindings ([#140](https://github.com/Shman4ik/pgNimbus/issues/140)).** A modal-editing layer over AvaloniaEdit is a large thing to build and keep correct, for a small group of users.
- **Localization ([#135](https://github.com/Shman4ik/pgNimbus/issues/135)).** The UI still changes every week, so translations would fall behind faster than anyone could keep them current.
- **PostGIS geometry viewer ([#141](https://github.com/Shman4ik/pgNimbus/issues/141)).** A lot of work for a small group of users, and map tiles would add network traffic, which the privacy promise rules out by default.
- **Quick result charts ([#139](https://github.com/Shman4ik/pgNimbus/issues/139)).** Nice to have, and lower than the gaps in everyday workflows.
- **WinGet community-source submission ([#134](https://github.com/Shman4ik/pgNimbus/issues/134)).** `winget install pgNimbus` already works through the `msstore` source with the Microsoft-signed package. The release pipeline still generates and validates the manifests, in case that ever isn't enough.
- **Keyset pagination for deep browsing (from D1).** Nobody clicks through thousands of pages to reach a row; they filter, and T4's filter chips run on the server. The part of D1 that users do hit, an incomplete export, stays as D1.

Completed items from the previous backlog: Linux release packages, table/index size and usage inspection, the blocking tree, and the Windows Mica backdrop. Keep them as shipped capabilities, not future launch promises. Contributions welcome; pick one scoped increment from the [roadmap issues](https://github.com/Shman4ik/pgNimbus/issues?q=is%3Aissue%20is%3Aopen%20label%3Aroadmap) rather than an entire campaign.
