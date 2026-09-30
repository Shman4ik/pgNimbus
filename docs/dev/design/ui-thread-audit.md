# UI-thread audit (2026-09)

kubeNimbus froze twice on large data in September 2026: a log list on a plain
`ItemsControl` (4,000 rows became about 37,000 live controls and took 7 s to lay
out) and an `ObservableCollection` trimmed with `RemoveAt(0)` and filled with one
`Add` per item (396,000 notifications for one flush). This audit looked for the
same class of bug in pgNimbus: work on the UI thread that grows with the size of
the data. It found twelve, and all are fixed here.

Timings below are from Avalonia's headless platform (software rendering), so
they are machine-relative. "Debug" timings come from the first probes, "Release"
timings from `tools/UiBench`. The counts (rows realized, change notifications)
do not depend on the machine, and `PgNimbus.App.Tests/UiThreadBudgetTests` holds
them as budgets.

## What was found and what changed

| # | Where | Data that grows | Before | After |
|---|---|---|---|---|
| 1 | Script result strip (`ResultsGridPanel.axaml`) | one chip per statement | a `StackPanel`, every chip realized, one dispatcher round trip per statement | `VirtualizingStackPanel` (8 chips realized for 5,000 statements); sections arrive in batches through `RangeObservableCollection.AddRange` |
| 2 | Schema tree (`SchemaTreePanel`) | tables, partitions, functions in a schema | expanding 5,000 tables realized 5,011 rows: 7.9 to 9.2 s (Debug); a filter pass on each loaded table was quadratic | every level virtualizes (`TreeView.virtualizing`): 90 rows, 0.36 s (Release); filtering goes through `ShownChildren`, 0.09 s to filter 5,000 to one |
| 3 | Grid copy (`CopyRowsAsync`) | the selected rows (up to 100,000) | formatted on the UI thread | formatted on the thread pool, 64 Mi characters at most, with a status line past that |
| 4 | Deleting rows | the selection | one `Remove` per row, each a DataGrid notification (~200 µs) and a queued walk over every visual in the grid; safe mode's staged set was a `List` checked with `Contains`, quadratic | one swap of `Rows` past 50 rows; one coalesced repaint that stops at the rows; `PendingChangeSet` indexed by key (100,000 staged deletes in 42 ms) |
| 5 | Query history | 200 entries of whole statements | each Run re-read the file, redacted every entry twice and wrote it, on the UI thread; Clear rebuilt the list once per removed entry | written on a background queue, one `ReplaceAll` per change, previews cut to 500 characters and tooltips to 40 lines |
| 6 | Command palette | every relation, partitions included | `Clear` and one `Add` per match on every keystroke | one Reset; each keystroke scores only the previous one's matches; 40 ms per key over 50,000 relations (Release) |
| 7 | SQL editor | the document | `(`, `'`, `,` and every caret move under the argument hint lexed the whole document | `SqlStatementBoundaries` remembers statement starts across edits; the readers get the statement around the caret (17 ms per key at the end of a 5 MB script, Release) |
| 8 | EXPLAIN views | plan nodes and text | text in a `SelectableTextBlock`, a fully expanded tree with a `Styles` per node, one warning per partition scan | `ReadOnlyTextView`, a virtualizing tree, `PlanAnalyzer.Condense` (three of each kind, then "N more") in a height-capped strip |
| 9 | Cell inspector, row details | a jsonb value; the columns of a row | the whole value laid out at once; the form rebuilt for every grid row, even closed | `ReadOnlyTextView` (a read-only editor past 32 Ki characters); the form follows the grid only while open, capped at `MaxGridColumns`, one Reset |
| 10 | Monitor windows | backends, lock waiters, tables, notifications | an `Add` per row into a DataGrid every 2 s; 500 `RemoveAt` and 500 `Insert(0)` per NOTIFY drain | one Reset per refresh, reads on the thread pool; one range Remove and one range Add per drain, selection kept |
| 11 | Core called from the UI thread | the catalog, an imported file | the completion catalog read (a million columns) and grouped there; import inferred types and ran its COPY loop there | `Task.Run` around each |
| 12 | Wide results | result columns | `MaxGridColumns` 1,000: 5.3 s to show, 2.9 s back on a tab switch, 3.0 s when the edit context arrived (Debug) | 300 columns (1.6 s, Release); the edit context updates the columns in place (0.1 s) |

Low: the permissions window's filters and matrix, the roles and RLS lists and
default privileges rebuild with one Reset; the completion popup holds the top
5,000 of its ranking.

## Checked and ruled out

- **Result streaming.** Rows are collected on the thread pool, delivered to the
  grid twice, capped at 100,000 rows and 256 MiB, and `Rows` is swapped whole.
  Cell previews are capped at 256 characters. Export streams on the thread pool.
- **Blocking waits on async work.** None: `.Result` is read only on finished tasks.
- **NOTICE output.** Never collected, so nothing grows.
- **Sorting by a column header.** 100,000 rows in about 0.2 s (Debug), left as is.
- **A `ListBox` inside a `ScrollViewer` and a `StackPanel`** (the permissions
  matrix). The audit listed it as unvirtualized, and it isn't: Avalonia's
  `VirtualizingStackPanel` sizes itself from the effective viewport, so it
  realized 13 of 3,000 rows. The same property is what lets the section strip and
  the nested levels of a `TreeView` virtualize.
- **Small, bounded lists:** connections, tabs, saved queries (240 px), slow
  queries (100 rows), largest relations (LIMIT 50).

## Two things to know before changing these views

- **A virtualizing panel realizes a hidden row to learn it takes no space.** A
  filter that sets `IsVisible` on rows it hides brings back every row: with the
  tree virtualized, a filter matching one of 5,000 tables realized all 5,000
  (0.9 to 3.5 s). Filter the list the control is bound to instead, as the schema
  tree does with `ShownSchemas` and `ShownChildren`.
- **`TreeView` moves focus only to a row that exists.** With nothing realized
  past the viewport, the arrow keys stopped at its bottom edge.
  `VirtualizingStackPanel.CacheLength="1"` keeps a viewport of rows realized on
  each side, which is what the `TreeView.virtualizing` style sets.

## Still open

- **The DataGrid has no column virtualization.** It builds a cell for every
  column of every row on screen, and laying out a column header costs about
  0.8 ms (Release, headless) before any cell. A 300-column result still takes
  about 1.6 s to show. Going past that needs a grid that virtualizes columns.
- A single cell is still read whole by Npgsql before the result budget can refuse
  its row (hard rule notes in `CLAUDE.md`).

## Measuring it

- `tools/UiBench` (Release) prints the `ui_*` metrics: the views over big data on
  the headless platform. `scripts/benchmarks/run-benchmarks.sh` runs it with the
  query benchmarks, and each release charts it under `/dev/bench/`.
- `PgNimbus.Benchmarks` prints `stage_deletes_ms`, `copy_tsv_ms`,
  `history_append_ms` and `editor_statement_ms` before its server metrics;
  `PGNIMBUS_BENCH_SKIP_DB=1` runs only those.
- `UiThreadBudgetTests` holds the counts on every build: rows realized for a
  5,000-table schema and a 5,000-statement script, one change notification per
  palette keystroke, history change and NOTIFY drain, no row-details rebuild
  while closed, no column rebuild when an edit context arrives.
