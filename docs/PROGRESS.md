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

## Open / candidate items
- [ ] More Files-language adoption: layered content tones (sidebar vs
      content), compact left sidebar with section headers, breadcrumb-style
      context (connection › database), segmented status bar. Mica/acrylic
      backdrop remains blocked on safe verification (a headless sandbox
      can't see transparency failures).
- [ ] Keyboard navigation audit (tab order, editor ↔ grid focus, Ctrl+W
      close tab?)
- [ ] Roadmap features (command palette, extension manager) — out of polish
      scope
