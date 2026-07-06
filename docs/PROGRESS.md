# Polish & performance loop — progress log

Working branch: `claude/loop-command-gjyq99`. Each entry was verified by
building and driving the real app under Xvfb (screenshots / `pg_stat_activity`
/ file traces), not just by code review. Newest iteration last; keep this file
current when landing work on this branch.

## Iteration 1

| Item | Outcome | Commit |
| --- | --- | --- |
| Light-theme nav pill loses highlight on hover | Root-caused: FluentTheme's TabItem ControlTheme repaints `PART_LayoutRoot` from `TabItemHeaderBackground*` resources on `:pointerover`/`:pressed`, bypassing `TemplateBinding`; app-level `/template/` styles lose the precedence fight. Fixed by overriding the DynamicResource values FluentTheme paints with; stock underline pipe retired. Verified light + dark, all pointer states. | `3c68697` |
| Unbounded memory growth on huge results | 100k-row display cap. Lives in `QueryEngine` (owns the `NpgsqlCommand`) because abandoning the reader drains the whole result set and a between-reads token cancel never reaches the backend — cap issues an explicit `command.Cancel()`. ViewModel requests cap+1 rows so an exactly-at-cap result isn't mislabeled truncated. Verified: backend goes `idle` in `pg_stat_activity`, bounded RSS, Cancel + small-result paths intact. | `c57fcea` |
| Emoji icons render as tofu on Linux | Nav glyphs (🗄 📝 🔔) and the 🔑 primary-key marker replaced with Material Design Icons `PathIcon` geometries; they inherit `Foreground`, so accent-inside-pill and dark theme work for free. | `becf827` |
| README "cold start under 500 ms" claim | Nobody had measured. JIT Release: ~700–720 ms launch-to-window (5 runs, 10 ms polling). NativeAOT (the intended route) crashed at startup at the time. README updated to measured figures. | `2798291` |

## Iteration 2

| Item | Outcome | Commit |
| --- | --- | --- |
| 100k-row result took ~20 s to display | Engine streams 100k rows in ~0.2 s; the DataGrid burns ~200 µs/row of UI time on **any** bulk collection change (per-item Add, range Add, Reset all alike), while assigning a pre-populated `ItemsSource` costs ~10 ms (virtualization realizes only a viewport). `Rows` is now swapped wholesale: first batch immediately, accumulation off-UI, full list swapped at completion (success/cancel/cap). Also dropped per-cell `IsDBNullAsync` in the engine (~20% engine win). **21.8 s → 0.4–0.7 s.** | `f7c29bb` |
| Wide-schema stress test | 30 schemas × 40 tables × 20 columns (1,200 tables) seeded (Sonnet subagent); lazy-loading schema tree renders instantly at every level. | — (test only) |
| DataGrid scroll with 100k loaded rows | Ctrl+End jumps to the last row instantly; grid stays responsive. | — (test only) |
| Notify tab polish | Accent "Start listening", green/grey status dot + "Listening on N channels" replacing `Listening: False`. Verified live: psql `NOTIFY` arrived and rendered. | `efaa123` |
| SSH-tunnel section polish | Scrollbar no longer overlaps field edges; dialog height 680 so the SSH-expanded form fits with no scroll/clipping. | `efaa123` |

## Iteration 3

| Item | Outcome | Commit |
| --- | --- | --- |
| NativeAOT publish crashed at startup | Bogus `avares://…/icon-256.png not found`. Real cause three layers down: NativeAOT bundles the DataGrid's `zh-Hans` satellite assembly into `AppDomain.GetAssemblies()`; with `InvariantGlobalization=true`, Avalonia's asset resolver calls `GetName()` on it → `CultureNotFoundException`, swallowed and re-surfaced as file-not-found. JIT never loads the satellite, hence JIT-only working. Fix: `SatelliteResourceLanguages=en`. Also replaced reflection `"[i]"` grid bindings with `RowIndexConverter` (clears IL2026/IL3050). | `af5473e` |
| AOT verification & measurements | Published linux-x64 AOT binary: full UI, capped 100k query in 167 ms, EXPLAIN plan tree, launch-to-window **91–112 ms** (JIT ~710 ms). README + CLAUDE.md updated. | `af5473e`, `b5fb46d` |
| Plain EXPLAIN showed "Planning:  ms" (no number) | Plain `EXPLAIN` omits Planning Time without `SUMMARY`; now requests `FORMAT JSON, SUMMARY`, and the summary line omits absent fragments. (Sonnet subagent, screenshot-verified.) | `48a01f7` |

## Measurements (Linux container, 4 cores)

| Metric | Before | After |
| --- | --- | --- |
| Capped 100k-row `SELECT *` display | 21.8 s | 0.4–0.7 s JIT / 167 ms AOT |
| Launch-to-window (Release) | ~710 ms JIT | 91–112 ms NativeAOT |
| Memory on unbounded 5M-row scan | unbounded growth | capped (~30 MB data + runtime) |
| Backend after cap/cancel | kept running | `idle` (explicit backend cancel) |

## Iteration 4

| Item | Outcome | Commit |
| --- | --- | --- |
| Progress log | This file. | `607e0fc` |
| Header-click sorting on result columns | Columns bind through a converter (no path), so the stock sort has no key: each column gets a `RowCellComparer` (`CustomSortComparer`) comparing its cell — NULLs last, same-type `IComparable` natively, ordinal-string fallback — plus explicit `CanUserSort`/`CanUserSortColumns`. Verified: text asc/desc with arrows, numeric `id` sorts 1,2,3 (not 1,10,100), and a 100k-row result sorts in ~1 s. | (this commit) |

## Iteration 5

| Item | Outcome | Commit |
| --- | --- | --- |
| NULL cells indistinguishable from empty strings | `RowIndexConverter` renders SQL NULL as a "NULL" placeholder; `ResultTextColumn` dims those cells to 0.4 opacity via a per-element binding (a style can't see the cell value), so the marker reads as a marker while a literal `'NULL'` string stays full-contrast. `PreparingCellForEdit` clears the placeholder out of the cell editor so an untouched commit can't write the string "NULL". Verified with NULL / `''` / `'NULL'` side by side. | (this commit) |

## Iteration 6 — UI north star: [Files](https://github.com/files-community/Files)

Per owner direction, Files is the reference for what "great Windows UI"
means for pgNimbus. Adopting its language where Avalonia allows.

| Item | Outcome | Commit |
| --- | --- | --- |
| Files-style command bar | Query toolbar is now a rounded card containing an accent Run (play icon) and quiet icon+label secondaries (stop/flash/gauge/table/export MDI glyphs) that are transparent at rest with Fluent's own hover wash. Disabled buttons stay quiet (dim text, no grey fill) via a container-scoped `ButtonBackgroundDisabled` resource override — the reliable per-area escape from ControlTheme repainting. Verified rest/hover/disabled in light and dark. | (this commit) |

## Iteration 7

| Item | Outcome | Commit |
| --- | --- | --- |
| Files two-tone shell | Window base now carries the chrome tint (`SystemControlBackgroundChromeMediumLowBrush`); the sidebar (nav pills + schema tree, card chrome removed) sits directly on it, and the whole right pane became a raised `Border.layer` — page-background fill, hairline border, 8 px radius — matching Files' content-on-Mica layering. Verified light + dark with results on screen. | (this commit) |

## Iteration 8

| Item | Outcome | Commit |
| --- | --- | --- |
| Title-bar connection breadcrumb | Files-style context in the chrome: "pgNimbus  host › database", parsed from the connection string (`NpgsqlConnectionStringBuilder`) and exposed as `ConnectionHost`/`ConnectionDatabase` on `MainViewModel`. Quiet 65 %-opacity text next to the accent environment dot. | (this commit) |

## Iteration 9

| Item | Outcome | Commit |
| --- | --- | --- |
| Files-style segmented status bar | The single concatenated `Status` string became structured segments — message · row count · timing · amber cap warning — divided by hairline separators in a strip flush along the layer's bottom edge (layer padding moved inward so the bar and its top hairline run edge-to-edge). New `RowCountText`/`TimingText`/`CapText`/`HasError` observables on `QueryViewModel`; streaming ticks update the row/timing segments while the message stays "Running..."; errors paint the message IndianRed; history entries get the segments flattened back into one line (`StatusSummary`). Verified live: success, 100k cap (backend `idle` after, 415 ms JIT / capped display intact), error, both themes. | (this commit) |
| Compact sidebar section headers | SAVED QUERIES / HISTORY / CHANNELS / NOTIFICATIONS as Files-style micro-headers: 11 px semibold, 0.55 opacity, letter-spaced uppercase (`TextBlock.sectionHeader`). Verified both themes. | (this commit) |
| AOT re-check | New compiled bindings only; publish clean (known DataGrid warnings), AOT binary runs the new status bar (500 rows in 31 ms, first byte 4 ms). | (this commit) |

## Iteration 10

| Item | Outcome | Commit |
| --- | --- | --- |
| Keyboard navigation | New shortcuts alongside Ctrl+Enter/Escape: **F5** run (DB-tool convention), **Ctrl+T** new tab, **Ctrl+W** close tab (parameterless → active tab), **Ctrl+PageDown/PageUp** cycle tabs (`NextTab`/`PreviousTab` on `MainViewModel`), **F6** hops focus editor ↔ results grid (code-behind — the target depends on where focus is). All exercised via xdotool from editor focus; AOT publish + shortcut smoke re-checked. | (this commit) |
| Close-tab selection bug (pre-existing, ✕ button too) | Closing the active tab left no tab selected and the editor/grid showing the dead tab's content: `Tabs.RemoveAt` makes the two-way-bound tab ListBox push `SelectedItem = null` into `ActiveTab` synchronously, so `CloseTab`'s "was it active" check compared against null (and the swallowed NRE in `AttachQuery` hid it). Fixed by deciding before the removal + guarding the transient null in the view. Verified: close via Ctrl+W lands on the neighbor tab, pill highlighted, its own SQL/results restored. | (this commit) |
| Status-bar pluralization | "1 rows" → "1 row"; "N row(s) affected" → real singular/plural. | (this commit) |

## Iteration 11

| Item | Outcome | Commit |
| --- | --- | --- |
| No SQL syntax highlighting (every query rendered plain) | Custom PostgreSQL XSHD (`Assets/PostgreSql.xshd`: keywords, types, strings, `--`/`/* */` comments, numbers; `ignoreCase`) loaded through AvaloniaEdit's `HighlightingLoader`. The highlighter has no theme awareness, so `MainWindow` rewrites the named colors on `Opened`/`ActualThemeVariantChanged` — VS-dark palette (#569CD6 keywords etc.) vs. saturated light palette — and reassigns `SyntaxHighlighting` to drop cached line visuals. Verified in both themes and under NativeAOT (the `AssetLoader.Open` path is the historical AOT landmine — publish + launch re-checked, highlighting renders). xdotool note: typed text gets garbled by the completion popup; paste via `xclip`/Ctrl+V for clean editor screenshots. | (this commit) |

## Iteration 12

| Item | Outcome | Commit |
| --- | --- | --- |
| Schema-tree filter box | Type-to-filter `TextBox` above the sidebar tree (magnifier `InnerLeftContent`, clear-✕ `InnerRightContent` shown only when non-empty). `SchemaTreeViewModel.FilterText` drives `ApplyFilter()`, which toggles a new `IsFilteredIn` on each node — bound to `TreeViewItem.IsVisible`. A schema survives when its own name matches (all its loaded tables stay visible) or when any loaded table matches (only the matches show, and the schema auto-expands to reveal them); an empty box reveals everything, and `RefreshAsync` re-applies so a lingering query holds across a catalog reload. Only schema + table names participate (case-insensitive substring); unloaded lazily-expandable tables can't match until expanded. Verified under Xvfb against a 4-schema seed: `customer` narrowed analytics→customer_metrics and billing→customer_accounts (non-matches + unmatched schemas hidden), and the ✕ button restored the full tree. | (this commit) |

## Iteration 13

| Item | Outcome | Commit |
| --- | --- | --- |
| In-app theme toggle | Title-bar sun/moon `chip` button (left of the `?`) flips `Application.Current.RequestedThemeVariant` between Light and Dark, so the app no longer only follows the OS. The window's existing `ActualThemeVariantChanged` hook — already there for the SQL highlighter — now also swaps the button glyph (`UpdateThemeIcon`: sun while dark = "click for light", moon while light), so the SQL palette and the toggle icon stay in lockstep however the variant changes. Verified under Xvfb: light→dark→light round-trip, whole shell + editor + syntax colors repaint each way and the glyph tracks. | (this commit) |

## Iteration 14

| Item | Outcome | Commit |
| --- | --- | --- |
| Empty states | Blank areas now guide instead of sitting bare. A centered dimmed table-icon hint ("No results yet — run a query with Ctrl+Enter or F5") overlays the results grid, driven by a new `QueryViewModel.HasNoResults` (`Rows.Count == 0 && !IsShowingPlan && !IsRunning`, recomputed from the Rows/IsShowingPlan/IsRunning change hooks). The saved-queries and history cards get their own centered hints via `SavedQueriesViewModel.HasNoSavedQueries`/`HasNoHistory` (notified on each collection change). All overlays are `IsHitTestVisible=False` so they never eat clicks. Verified under Xvfb: the results hint shows at startup and vanishes when `SELECT 1` returns a row; both list hints render on a fresh profile. | (this commit) |

## Iteration 15

| Item | Outcome | Commit |
| --- | --- | --- |
| Copy from the results grid | `Ctrl+C` copies the selected rows (or the whole result set when nothing is selected) as TSV; a grid context menu adds **Copy** and **Copy as ▸ CSV / JSON / Markdown table / INSERT statements**. Formatting lives in `Core`'s `ResultExporter` (new `WriteTsv`/`WriteMarkdown`/`WriteInsert` beside the existing CSV/JSON, plus public `QuoteIdentifier` and SQL-literal quoting — NULL, unquoted numbers/booleans, `''`-escaped text, `\x…` bytea), so it stays UI-free and reuses one value formatter. `QueryViewModel.CopyRows` renders to a string; the grid is `SelectionMode=Extended` with `ClipboardCopyMode=None` (our columns bind through a path-less converter, so the stock copy has no cell text — the view builds it and writes via `IClipboard.SetTextAsync`). INSERT targets the edited table when the result maps to one, else a `table_name` placeholder. Verified under Xvfb against `SELECT id,name,email FROM customers LIMIT 3`: Ctrl+C produced tab-separated header+rows, and the context menu produced valid INSERTs (numeric id unquoted, text quoted) and a GitHub Markdown table. | (this commit) |

## Iteration 16

| Item | Outcome | Commit |
| --- | --- | --- |
| Smarter tab titles | Tabs are named from the first table the SQL references (a source-generated `[GeneratedRegex]` grabs the identifier after FROM/JOIN/UPDATE/INTO, keeps the table part of a schema-qualified name, strips quotes), falling back to a `DefaultTitle` ("Query N") when none. A dirty-state accent dot (`IsDirty`) shows when the SQL differs from `_lastRunSql` — set as the baseline at the start of each run — so the dot appears on edit and clears on run. Regex is source-generated to stay AOT-clean. Verified under Xvfb: `SELECT 1` stayed "Query 1"; pasting `SELECT * FROM public.customers …` retitled the tab to "customers" with the dot; F5 cleared the dot. | (this commit) |

## Iteration 17

| Item | Outcome | Commit |
| --- | --- | --- |
| Command palette (`Ctrl+K` / `Ctrl+P`) | A centered overlay above the whole shell that fuzzy-jumps to any table, saved query, or action — the keyboard-first differentiator in one control. `CommandPaletteViewModel` owns the query/selection/invocation; `MainViewModel.OpenCommandPaletteAsync` builds the candidate set (actions + saved queries instantly, then merges in tables once `SchemaService.GetAllRelationsAsync` — a new single `pg_catalog` query returning schema-qualified relations — returns, so the palette shows without blocking on the DB). A `PaletteItem` carries a `Func<Task>` effect: table entries call `PreviewTableAsync(schema, name)` (refactored to a schema/name overload), actions resolve `ICommand`s lazily and fire only when `CanExecute`, and window-level actions (theme toggle, shortcuts) route through `ThemeToggleRequested`/`ShortcutsRequested` events the view subscribes to. Ranking is a subsequence `FuzzyMatcher` (prefix / word-boundary / adjacency bonuses) over "Title Category". Fully keyboard-driven from the search box: ↑/↓ move the highlight, Enter accepts, Esc / outside-click dismiss. Verified under Xvfb: Ctrl+K listed 10 actions + tables; typing "order" narrowed to `sales.order_summary` + `sales.orders`; Enter jumped the editor to `SELECT * FROM "sales"."order_summary" LIMIT 100;` and closed; Ctrl+P + "theme" ran the toggle. README (feature bullet, shortcut row) + F1 cheat sheet updated. | (this commit) |

## Iteration 18

| Item | Outcome | Commit |
| --- | --- | --- |
| Data browsing without SQL | Previewing a table (double-click or command palette) now opens no-SQL browse mode: a bar above the grid with a server-side `WHERE` filter, `ORDER BY` from clicking a column header, and prev/next paging — all pushed down to Postgres (`WHERE`/`ORDER BY`/`LIMIT`/`OFFSET`), never client-side. `TableBrowseViewModel` holds filter/sort/offset and composes the page SQL (identifiers quoted via `SqlIdentifier`, filter inlined as trusted client SQL, `LIMIT 100 OFFSET n`); `QueryViewModel.StartBrowseAsync`/`RunBrowseSqlAsync` run each page through the *existing* streaming path (sets `Sql`, awaits `RunCommand`) so the grid/columns/status all come for free, then re-applies the inline-edit context the programmatic `Sql` write cleared. A guard flag makes a programmatic browse compose keep browse mode while a manual editor edit tears it down (`OnSqlChanged`). Header clicks are intercepted via `DataGrid.Sorting` (`e.Handled = true` cancels the client comparer sort; `e.Column.Header` → `SortByAsync`, toggling asc/desc and resetting to page 1). Paging is a cheap heuristic — a full 100-row page means "maybe more" (Next enabled), no separate `COUNT(*)`. Verified under Xvfb against a 263-row `customers`: jump → page 1 (`Rows 1–100`, ◀ disabled), Next → `OFFSET 100` (`Rows 101–200`, ◀ enabled, ids 101+), click `id` header → `ORDER BY "id" ASC` + `ORDER BY id ▲` chip + reset to page 1, filter `id > 260` + Enter → `WHERE id > 260` → 3 rows (`Rows 1–3`, both arrows disabled, clear-✕ shown). README (feature bullet, backlog check, recently-shipped) updated. | (this commit) |

## Open / candidate items
- [ ] Mica/acrylic backdrop remains blocked on safe verification (a headless
      sandbox can't see transparency failures).
- [ ] Roadmap features (extension manager, plugin API) — out of polish
      scope
