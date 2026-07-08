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

## Iteration 19

| Item | Outcome | Commit |
| --- | --- | --- |
| Row insert & delete from the grid | Completes the CRUD triangle beyond inline cell edits. **Delete**: a grid context-menu item + Delete key (guarded to editable results, skipped mid-cell-edit) → a `ConfirmDialog` (destructive, "can't be undone") → `QueryViewModel.DeleteRowsAsync` runs one PK-keyed parameterized `DELETE` per selected row; in browse mode the page reloads afterward (counts/paging stay right), otherwise rows drop from the grid in place. **Add**: a context-menu "Add row…" opens `AddRowDialog`/`AddRowViewModel` — one input per column (name + declared type, NULL checkbox) built from `GetColumnsAsync`. Values are passed as **text parameters cast to each column's declared type server-side** (`CAST(@p AS numeric(12,2))`), so Postgres does the parsing and it stays injection-safe; a blank field is omitted so its default applies (serials, `now()`), and NULL is explicit. On success the grid refreshes (`RefreshCurrentAsync`: browse reload or query re-run). No `Core` change — both reuse the existing `ExecuteNonQueryAsync`. Verified under Xvfb against `customers`: add row (name/email typed, id serial + created_at defaulted → id 264 in DB), then filter to it and delete (confirm dialog → 0 rows). Also stress-tested against a 16-column all-types `kitchen_sink` (integer/bigint/numeric/real/boolean/text/varchar/uuid/date/timestamp/timestamptz/**json**/**jsonb**/**integer[]**/inet): inserting `42`, `99.95`, `true`, a uuid, `{"a":1}`, `{"foo":[1,2],"bar":"baz"}`, `{7,8,9}` round-tripped correctly with the serial id and `tstz` default applied. README (feature bullet, backlog check, recently-shipped) updated. | (this commit) |

## Iteration 20

| Item | Outcome | Commit |
| --- | --- | --- |
| Refresh database & schema | On-demand reload of everything derived from the live catalog, so objects created/altered in another session (or via DDL here) appear without reconnecting. A `MainViewModel.RefreshSchemaCommand` nulls the command-palette relation cache and awaits `SchemaTree.RefreshCommand` + `CompletionProvider.RefreshAsync` together. Surfaced three ways: a refresh (⟳) chip button beside the sidebar filter box, a `Ctrl+Shift+R` key binding, and a "Refresh database & schema" command-palette action. Verified under Xvfb: with `public` expanded showing only `customers`, `CREATE TABLE public.widgets` in another connection → click refresh → re-expand `public` now lists `widgets` too, and the command palette (cache invalidated) finds `public.widgets`. README (feature bullet, backlog check, recently-shipped, shortcut row) + F1 cheat sheet updated. | (this commit) |

## Iteration 21

| Item | Outcome | Commit |
| --- | --- | --- |
| Multiple result sets per script | Running a `;`-separated script now surfaces every statement, not just the first. A new `Core` lexer (`SqlScriptSplitter`) splits on top-level semicolons while respecting single-quoted literals (`''` escapes), quoted identifiers, dollar-quoted strings (`$tag$…$tag$`), and line/(nestable-)block comments — dropping empty/comment-only statements. `QueryEngine.ExecuteScriptAsync` runs the statements **on one shared connection**, so session state carries across them (a `CREATE TEMP TABLE`/`INSERT`/`SELECT` sequence works), yields one `StatementResult` per statement (new materialized `MaterializedResultSet` since a shared connection can't hand off a lazily-streaming reader), each independently timed, and stops at the first `QueryError` (psql `ON_ERROR_STOP`). In the app, `RunAsync` splits: a single statement keeps the untouched streaming/editing/browse path; two+ go the script path (background enumeration, UI-marshaled section adds). Results show as a selectable strip of `ScriptResultViewModel` chips above the grid (label = `n · KEYWORD`, secondary line = row count / rows affected / red **Error**); selecting one re-points the shared grid + status-bar segments at that statement's rows/timing, so copy/export work per section for free. Verified under Xvfb: `CREATE TEMP TABLE t …; INSERT …; SELECT * FROM t; SELECT … customers …` → 4 sections with per-statement timings (temp table survived across statements — proof of the shared connection), clicking §3 showed its 3 rows; an error mid-script (`SELECT 1; SELECT * FROM does_not_exist; SELECT 2`) produced 2 sections (3rd never ran), auto-jumped to the red errored section, and the status bar showed `Error: relation "does_not_exist" does not exist`; a lone `SELECT … LIMIT 6` still streamed with no strip. README (feature bullet, backlog check, recently-shipped) updated. | (this commit) |

## Iteration 22

| Item | Outcome | Commit |
| --- | --- | --- |
| DDL view ("Source" per object) | A **Source (DDL)** context-menu action on any schema-tree table/view reconstructs its `CREATE …` definition from pg_catalog and opens it in a new query tab, where it can be read, copied, or tweaked and run. New `Core` `DdlService`: tables/partitioned tables are rebuilt column-by-column (`format_type` types, `pg_get_expr` defaults, `attidentity` → `GENERATED … AS IDENTITY`, `NOT NULL`), then constraints via `pg_get_constraintdef` (ordered PK → unique → FK → check), a `pg_get_partkeydef` `PARTITION BY` clause for partitioned tables, and secondary indexes via `pg_get_indexdef` (skipping the PK index and any index backing a constraint, so nothing repeats); views/matviews use `pg_get_viewdef`. oids are read as `uint` and inlined (Npgsql has no oid parameter mapping — an `AddWithValue(uint)` throws) since they're catalog-sourced numbers, not user input. Wired through `App.axaml.cs` → `MainViewModel.ShowSourceAsync` (new `NewTab` helper). A new `QueryViewModel.TitleOverride` gives the tab a fixed label (`name · source`) that wins over the SQL-derived title — needed because a view's `CREATE VIEW … AS SELECT … FROM other` would otherwise name the tab after the *other* table. Verified under Xvfb: `public.customers` → `CREATE TABLE` with serial `nextval` default, PK constraint, and both a plain and a `lower(email)` unique index; `sales.order_summary` (view) → `CREATE VIEW … AS SELECT …` with the tab correctly titled `order_summary · source` (not `customers`); `sales.orders` → FK constraint (`REFERENCES customers`) after the PK plus its secondary index. README (feature bullet, backlog check, recently-shipped) updated. | (this commit) |

## Iteration 23

| Item | Outcome | Commit |
| --- | --- | --- |
| Set a cell to NULL from the grid | Inline editing can't express NULL (an emptied cell editor commits an empty string), so the results-grid context menu gains **Set cell to NULL** — enabled only for editable (PK-mapped) result sets, acting on the same last-pressed cell the inspector uses. `QueryViewModel.SetCellNullAsync` issues a PK-keyed `UPDATE … SET col = NULL` (literal NULL, PKs parameterized; PK columns refused) and replaces the row in place so the dimmed NULL placeholder renders immediately. Verified under Xvfb in browse mode on `customers.notes`: menu disabled for a hand-typed SELECT (no edit context), enabled in browse; click → placeholder + "Set public.customers.notes to NULL" status + `notes IS NULL` in psql. | (this commit) |
| Editor niceties: current line, bracket match, font zoom | `Options.HighlightCurrentLine` with theme-resolved brushes (5%-alpha wash, stock border suppressed — it draws a hard outline box). Matching-bracket highlight is a new `BracketHighlightRenderer` (`IBackgroundRenderer`, Selection layer): nesting-aware raw-text scan for `()`/`[]` around the caret (char before wins), driven from `Caret.PositionChanged`, accent-tinted washes behind both ends. Font zoom: Ctrl+wheel (tunneled — the TextView claims wheel for scrolling), Ctrl+±/numpad, Ctrl+0 reset, clamped 8–32. **Landmine:** "Ctrl and +" is physically Ctrl+Shift+= on most layouts, so an exact `KeyModifiers == Control` check silently fails and the `+` leaks into the document as text — match with `HasFlag(Control)` (Alt excluded). Verified under Xvfb both themes: pair `(…)` washed while `count(*)`'s parens stay quiet, zoom in/reset/out via keys and Ctrl+wheel, F1 cheat sheet updated. | (this commit) |

## Iteration 24

| Item | Outcome | Commit |
| --- | --- | --- |
| Smarter SQL IntelliSense | Completion now reads the caret's grammatical position instead of always dumping the catalog. A new single-pass scanner (`SqlCompletionContext.GetCaretContext`) tracks string/comment state (`'…'` with `''` escapes, quoted identifiers, `--`, nestable `/* */`, `$tag$…$tag$`) and the governing clause per statement, so: **(1)** nothing pops up inside literals/comments; **(2)** after `FROM`/`JOIN`/`INTO`/`UPDATE` only tables/schemas/CTE names (+keywords) are offered — the list opens by itself on the space after those keywords, tables first — while `INSERT INTO t (` flips back to columns; **(3)** everywhere else the statement's own columns float to the top as before, now also from `UPDATE`/`INSERT INTO` targets (new `UpdateIntoTargetRegex`) and joined by the statement's aliases and `WITH` CTE names (new `CteNameRegex`). Column items now carry their `format_type` data type (`GetAllColumnsAsync` returns it; description reads `column (users) : integer`), and ~70 curated everyday functions (`coalesce`, `date_trunc`, `string_agg`, window functions…) complete as `name()` with the caret placed between the parens. Logic verified by a 58-case scratch harness (clause detection, suppression, table/CTE extraction — all pass) and end-to-end under Xvfb: `FROM ` auto-opened tables-first list; `cu`→Enter inserted `customers`; `c.` listed exactly its 4 columns; the selected column's tooltip showed `column (customers) : integer`; typing inside `'nam` showed no popup; `coale`→Enter produced `coalesce(│)` with the next popup led by alias `c` + floated columns; a `WITH recent AS (…)` query listed `recent` first after `FROM `. | (this commit) |

## Iteration 25

| Item | Outcome | Commit |
| --- | --- | --- |
| Tab-bar navigation extras | ‹/› scroll arrows on the tab strip, visible only while the strip actually overflows (one `ScrollChanged` subscription tracks both scrolling and extent changes from tabs opening/closing) and disabled at the respective end; each click nudges the strip 160 px. Plus a ▾ dropdown (chip button, `Flyout`) listing every open tab with type-to-search: the search box gets focus on open (posted via the dispatcher — the flyout otherwise reclaims it), typing filters case-insensitively on the tab title, ↑/↓ move the highlight, Enter (or a tap on an item — taps on the scrollbar deliberately don't count) activates the tab and closes the flyout; the active tab keeps the initial highlight while it matches. Dirty-state dots carry into the list. Verified under Xvfb with 13 tabs: arrows appeared on overflow, ‹ scrolled Query 7–13 back to 4–10, dropdown listed all 13 with the active one highlighted, typing `5` narrowed to Query 5 and Enter jumped to it. | (this commit) |
| Connection-dialog empty state | The Saved Connections list was a bare grey panel on first launch; it now shows the same centered dimmed hint the saved-queries/history lists use ("No saved connections yet — fill in the form and press Save to keep one here"), driven by `HasNoProfiles` on `ConnectionDialogViewModel` (recomputed on every `Profiles` collection change). Verified under Xvfb with a fresh `$HOME`: hint shows on first launch and disappears the moment a profile is saved. | (this commit) |
| README hygiene | The running-query-feedback backlog entry was still unchecked although #54 shipped it — flipped, with the description updated to what was actually built. | (this commit) |

## Iteration 26

| Item | Outcome | Commit |
| --- | --- | --- |
| Drag-and-drop from the schema tree | Schemas, tables, and columns drag out of the sidebar tree and drop into the SQL editor as identifiers quoted only where a bare name wouldn't round-trip (`SqlIdentifier.QuoteIfNeeded`; tables drop schema-qualified). The drag arms on press over a draggable node and starts only past a 4 px movement threshold, so clicks, expander toggles, and double-click previews behave exactly as before. **Avalonia 12 landmine:** the old `DataObject`/`DataFormats`/`DoDragDrop` API is compile-error obsolete — it's `DataTransfer` + `DataTransferItem.CreateText` + `DragDrop.DoDragDropAsync` now, and `DoDragDropAsync` only starts from the original `PointerPressedEventArgs`, so the press args ride along in the armed-candidate tuple. On the editor side `DragOver` moves the caret with the pointer (live landing preview) and `Drop` inserts at that position and focuses the editor. Verified under Xvfb: `customers` dropped as `public.customers` at the pointer; the `name` column dropped mid-token exactly at the pointer position. | (this commit) |
| Compact schema-tree indent | The deferred item from the open list, via path (a): the header's own indent is `Level*16` px set at Template priority (unbeatable from a style), but each nested `PART_ItemsPresenter`'s `Margin` is *not* template-set, so a `-8,0,0,0` style counter-offset (scoped inside the schema `TreeView.Styles`) nets out to ~8 px per level. Verified on a live three-level tree (schema → table → columns) — clearly tighter, chevrons and key icons intact. | (this commit) |

## Open / candidate items
- [x] Mica/acrylic backdrop on Windows — `MainWindow` sets
      `TransparencyLevelHint="Mica,AcrylicBlur"` + transparent window
      background; `ApplyBackdrop` (code-behind) swaps the two-tone shell's base
      (`ShellBase`) to the theme-split `ShellBackdropBrush` when
      `ActualTransparencyLevel` is Mica/Acrylic, and keeps the opaque chrome
      tone otherwise (Win10-, Linux, macOS, transparency off) so the base is
      never a see-through hole. Content pane stays opaque. Needs a live desktop
      to eyeball the frost.
- [x] Dropped the OS `SystemAccentColor`/`SystemControlHighlightListAccentLowBrush`
      for a fixed brand blue (`AppAccentBrush`/`Hover`/`Pressed` + low-alpha
      `AppSelectionBrush` in `Theme.axaml`). The OS accent could be any hue, so
      keeping every selection/hover/primary surface legible against it wasn't
      worth polishing. Per-connection `AccentColor` (the connection dot) is
      unrelated and untouched.
- [x] Compact schema tree — done in Iteration 26 via path (a): a `-8,0,0,0`
      style margin on each nested `PART_ItemsPresenter` (not template-set, so
      a style wins) counter-offsets the template's `Level*16` header indent
      to ~8 px per level. Verified on a live three-level tree.
- [ ] `Window.MinWidth`/`MinHeight` clamp the layout size (and the `Width`
      property) but do **not** feed the Win32 min-track-size, so an OS frame
      drag can still shrink the window below `940` and squeeze the right pane
      to a sliver (content clips / DataGrid auto-scrolls a clicked cell into
      view). Fix candidates: push the min into the platform impl, or make the
      command bar wrap and the pane degrade gracefully. Deferred.
- [ ] Roadmap features (extension manager, plugin API) — out of polish
      scope
