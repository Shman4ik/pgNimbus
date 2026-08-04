window.BENCHMARK_DATA = {
  "lastUpdate": 1785821853682,
  "repoUrl": "https://github.com/Shman4ik/pgNimbus",
  "entries": {
    "pgNimbus benchmarks": [
      {
        "commit": {
          "author": {
            "email": "shman4ik@gmail.com",
            "name": "Dmitrii Shmanev",
            "username": "Shman4ik"
          },
          "committer": {
            "email": "noreply@github.com",
            "name": "GitHub",
            "username": "web-flow"
          },
          "distinct": true,
          "id": "68d8bdc4e242a7fec1e9fab3014d52caf27c44a7",
          "message": "Boost ON/USING completions after a finished JOIN table+alias (#94)\n\n* Boost ON/USING after a completed JOIN table+alias; move SqlCompletionContext to Core\n\nAfter \"JOIN table alias \" the only grammatical next tokens are ON/USING, but\ncompletion still dumped the flat table/keyword catalog, burying ON dozens of\nitems deep. IsAfterCompleteJoinTarget detects a fully-typed JOIN target\n(trailing whitespace proves it's not mid-alias) and floats ON/USING to the top\nwith the same priority as the FK join-condition suggestion.\n\nSqlCompletionContext is pure text logic with no Avalonia dependency, so it\nmoved to PgNimbus.Core/Text to be unit-testable from PgNimbus.Core.Tests\n(the App project isn't covered by the TUnit suite). Also fixed a\npre-existing bug surfaced by the new tests: FromClauseRegex could truncate\nthe FROM body mid-clause when a clause keyword appeared inside a quoted\nidentifier or string literal (e.g. \"Order Items\").\n\nCo-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>\n\n* Mask comments and string literals before the regex heuristics run\n\nReview feedback on #94: a clause keyword inside a comment could cut the\nFROM body short, and a \"join\" inside a comment/string before the caret\ncould pose as the JOIN whose target IsAfterCompleteJoinTarget checks.\nInstead of teaching each regex about quoting one by one, a single\nMaskCommentsAndStrings pass (same scan as GetCaretContext: line + nested\nblock comments, '...' and $...$ literals) blanks the noise to spaces,\noffsets preserved; ExtractTables, ExtractCteNames and\nIsAfterCompleteJoinTarget now run over the masked text. Double-quoted\nidentifiers survive the mask — FromClauseRegex still consumes them\natomically for the \"Order Items\" case.\n\nAlso fixes a pre-existing phantom table: SELECT 'copied from fake_table'\nused to extract fake_table as a FROM table.\n\nCo-Authored-By: Claude Fable 5 <noreply@anthropic.com>\n\n---------\n\nCo-authored-by: Claude Sonnet 5 <noreply@anthropic.com>",
          "timestamp": "2026-07-11T18:04:05+02:00",
          "tree_id": "50bb2e2d6e474491bcceabe00f12534626f4abaa",
          "url": "https://github.com/Shman4ik/pgNimbus/commit/68d8bdc4e242a7fec1e9fab3014d52caf27c44a7"
        },
        "date": 1783786741232,
        "tool": "customSmallerIsBetter",
        "benches": [
          {
            "name": "Startup, launch to first frame (NativeAOT)",
            "value": 157,
            "unit": "ms"
          },
          {
            "name": "Memory at first frame (NativeAOT)",
            "value": 149.6,
            "unit": "MB"
          },
          {
            "name": "Binary size (NativeAOT)",
            "value": 40.3,
            "unit": "MB"
          },
          {
            "name": "Startup, launch to first frame (JIT)",
            "value": 1665,
            "unit": "ms"
          },
          {
            "name": "Connect, cold pool",
            "value": 146.3,
            "unit": "ms"
          },
          {
            "name": "Round-trip, SELECT 1 warm",
            "value": 0.32,
            "unit": "ms"
          },
          {
            "name": "First row batch of a 100000-row SELECT",
            "value": 10.6,
            "unit": "ms"
          },
          {
            "name": "Stream 100000 rows",
            "value": 154,
            "unit": "ms"
          }
        ]
      },
      {
        "commit": {
          "author": {
            "email": "shman4ik@gmail.com",
            "name": "Dmitrii Shmanev",
            "username": "Shman4ik"
          },
          "committer": {
            "email": "noreply@github.com",
            "name": "GitHub",
            "username": "web-flow"
          },
          "distinct": true,
          "id": "08e8e3f729ab9156cd76fab7a4ae1a5b816bd8b1",
          "message": "Connect dialog polish, taskbar icon fix, and installer/benchmark cleanup (#96)\n\n* Connect dialog polish, taskbar icon fix, and installer/benchmark cleanup\n\n- Add an app version + copyright/license footer to the Connect dialog,\n  spanning the full width below all action buttons (New, Delete, Save,\n  Connect all on one row).\n- Fix the Windows 11 taskbar showing a generic blank icon for every window\n  (a known Avalonia/Win32 gap: Window.Icon doesn't reliably update the\n  taskbar's HICON). ThemedWindowChrome now also sends WM_SETICON directly\n  via P/Invoke, built from new per-theme multi-size .ico files\n  (window-icon-{light,dark}.ico) instead of the old flat 256px PNGs.\n- Exclude .pdb debug symbols from the MSI payload (Product.wxs) — they\n  added ~101MB of a 216MB publish output with no end-user benefit.\n- Track total publish directory size (not just the AOT exe) in the\n  benchmarks pipeline, since bundled native libs dwarf the exe itself.\n- Update copyright/author metadata to \"Dmitrii Shmanev\" in both csproj\n  files and LICENSE (GitHub URLs/handles are left untouched).\n\nCo-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>\n\n* Address PR review: fail-safe native icon path, clarify icon-script comment\n\n- ThemedWindowChrome: skip WM_SETICON when the cached HICON is zero\n  (sending NULL would remove the icon instead of leaving Window.Icon's),\n  and return null from ExtractIcoEntry instead of throwing so a size\n  mismatch degrades to plain Window.Icon behavior rather than crashing\n  window construction.\n- make-app-icons.ps1: scope the BMP-entry comment to app.ico (shell-read)\n  so it no longer contradicts the all-PNG window-icon .ico block.\n\nCo-Authored-By: Claude Fable 5 <noreply@anthropic.com>\n\n* Update Windows icon guidelines and compliance docs\n\nClarified window icon usage for Store/taskbar, added Microsoft's official Windows icon design rules to DESIGNER-BRIEF.md, and updated LOGO-ASSETS.md with a compliance checklist and notes on current asset coverage and recommendations.\n\n---------\n\nCo-authored-by: Claude Sonnet 5 <noreply@anthropic.com>",
          "timestamp": "2026-07-12T10:50:20+02:00",
          "tree_id": "5a98b4f5f5f62b1a5aa2ae9c5fad61f5e831affa",
          "url": "https://github.com/Shman4ik/pgNimbus/commit/08e8e3f729ab9156cd76fab7a4ae1a5b816bd8b1"
        },
        "date": 1783846519671,
        "tool": "customSmallerIsBetter",
        "benches": [
          {
            "name": "Startup, launch to first frame (NativeAOT)",
            "value": 160,
            "unit": "ms"
          },
          {
            "name": "Memory at first frame (NativeAOT)",
            "value": 149.5,
            "unit": "MB"
          },
          {
            "name": "Binary size (NativeAOT)",
            "value": 40.3,
            "unit": "MB"
          },
          {
            "name": "Publish size (NativeAOT, all files)",
            "value": 139.4,
            "unit": "MB"
          },
          {
            "name": "Startup, launch to first frame (JIT)",
            "value": 1681,
            "unit": "ms"
          },
          {
            "name": "Connect, cold pool",
            "value": 146.5,
            "unit": "ms"
          },
          {
            "name": "Round-trip, SELECT 1 warm",
            "value": 0.35,
            "unit": "ms"
          },
          {
            "name": "First row batch of a 100000-row SELECT",
            "value": 11.5,
            "unit": "ms"
          },
          {
            "name": "Stream 100000 rows",
            "value": 148.4,
            "unit": "ms"
          }
        ]
      },
      {
        "commit": {
          "author": {
            "email": "shman4ik@gmail.com",
            "name": "Dmitrii Shmanev",
            "username": "Shman4ik"
          },
          "committer": {
            "email": "noreply@github.com",
            "name": "GitHub",
            "username": "web-flow"
          },
          "distinct": true,
          "id": "9f2f98f905dbd45a2964c4f77b9e4824d5d2cc2d",
          "message": "Redraw pgNimbus logo/icon set + function-signature completion (#101)\n\n* Redraw pgNimbus logo/icon set; add function-signature completion\n\n- Vectorize the elephant-on-broom mark from the concept PNG and regenerate\n  the full design/masters/{icon,window,logo} pipeline (icon tiles 16-1024,\n  window title-bar icons, transparent logo, wordmark, social-preview.png).\n- Add PgNimbus.Core function-signature completion (FunctionSignatureFormatter\n  + tests) and related README/completion-provider updates.\n\n* Fix small icon legibility and taskbar icon contrast\n\n- Simplify icon-16/24/32 masters (drop hairline detail that turned to mush\n  at small sizes) while keeping them full-bleed, matching icon-48/256/1024.\n- ThemedWindowChrome: always use the light-ink icon for the taskbar's native\n  WM_SETICON, since the Windows taskbar is almost always dark regardless of\n  the app's own theme; the title bar keeps following the app theme.\n\n* Revert small icons to full-bleed (transparent badge was unreadable)\n\nThe transparent circular badge tried for icon-16/24/32 assumed an OS-drawn\nplate behind it, but app.ico feeds contexts (Explorer, taskbar, pinned\nshortcut) that render it directly with no plate — the navy ring just\ndisappeared into a dark taskbar. Back to full-bleed navy tiles, matching\nicon-48/256/1024, keeping the simplified (hairline-free) linework.\n\n* logo fix\n\n* refactoring",
          "timestamp": "2026-07-13T20:54:01+02:00",
          "tree_id": "d2d8ed749777f0266140d56876d9a710e783c154",
          "url": "https://github.com/Shman4ik/pgNimbus/commit/9f2f98f905dbd45a2964c4f77b9e4824d5d2cc2d"
        },
        "date": 1783969694332,
        "tool": "customSmallerIsBetter",
        "benches": [
          {
            "name": "Startup, launch to first frame (NativeAOT)",
            "value": 167,
            "unit": "ms"
          },
          {
            "name": "Memory at first frame (NativeAOT)",
            "value": 149.4,
            "unit": "MB"
          },
          {
            "name": "Binary size (NativeAOT)",
            "value": 40.3,
            "unit": "MB"
          },
          {
            "name": "Publish size (NativeAOT, all files)",
            "value": 139.5,
            "unit": "MB"
          },
          {
            "name": "Startup, launch to first frame (JIT)",
            "value": 1651,
            "unit": "ms"
          },
          {
            "name": "Connect, cold pool",
            "value": 148.1,
            "unit": "ms"
          },
          {
            "name": "Round-trip, SELECT 1 warm",
            "value": 0.29,
            "unit": "ms"
          },
          {
            "name": "First row batch of a 100000-row SELECT",
            "value": 10.2,
            "unit": "ms"
          },
          {
            "name": "Stream 100000 rows",
            "value": 123.9,
            "unit": "ms"
          }
        ]
      },
      {
        "commit": {
          "author": {
            "email": "shman4ik@gmail.com",
            "name": "Dmitrii Shmanev",
            "username": "Shman4ik"
          },
          "committer": {
            "email": "noreply@github.com",
            "name": "GitHub",
            "username": "web-flow"
          },
          "distinct": true,
          "id": "d1756324bee3bdd480c6a7b8c5ee325e5f82b0fc",
          "message": "Logo fix (#103)",
          "timestamp": "2026-07-14T08:07:08+02:00",
          "tree_id": "d6db39746240966a9e24bae8209cf470342e28ef",
          "url": "https://github.com/Shman4ik/pgNimbus/commit/d1756324bee3bdd480c6a7b8c5ee325e5f82b0fc"
        },
        "date": 1784009677820,
        "tool": "customSmallerIsBetter",
        "benches": [
          {
            "name": "Startup, launch to first frame (NativeAOT)",
            "value": 159,
            "unit": "ms"
          },
          {
            "name": "Memory at first frame (NativeAOT)",
            "value": 149.4,
            "unit": "MB"
          },
          {
            "name": "Binary size (NativeAOT)",
            "value": 40.3,
            "unit": "MB"
          },
          {
            "name": "Publish size (NativeAOT, all files)",
            "value": 139.5,
            "unit": "MB"
          },
          {
            "name": "Startup, launch to first frame (JIT)",
            "value": 1707,
            "unit": "ms"
          },
          {
            "name": "Connect, cold pool",
            "value": 146.1,
            "unit": "ms"
          },
          {
            "name": "Round-trip, SELECT 1 warm",
            "value": 0.37,
            "unit": "ms"
          },
          {
            "name": "First row batch of a 100000-row SELECT",
            "value": 11.5,
            "unit": "ms"
          },
          {
            "name": "Stream 100000 rows",
            "value": 149.9,
            "unit": "ms"
          }
        ]
      },
      {
        "commit": {
          "author": {
            "email": "shman4ik@gmail.com",
            "name": "Shman4ik",
            "username": "Shman4ik"
          },
          "committer": {
            "email": "shman4ik@gmail.com",
            "name": "Shman4ik",
            "username": "Shman4ik"
          },
          "distinct": true,
          "id": "7396d3d0c783fdaa93c8069190c60ff8811e369f",
          "message": "Explain: survive PG 18 fractional row counts; text plan view with Tree toggle\n\nPostgreSQL 18 reports EXPLAIN ANALYZE actual row counts averaged over\nloops with two decimals (\"Actual Rows\": 7.00 in FORMAT JSON). ExplainService\nparsed them with JsonElement.GetInt64(), which throws FormatException on any\nfractional number — so every EXPLAIN ANALYZE against a PG 18 server (e.g.\ncurrent Neon) failed with the opaque status \"Explain failed: One of the\nidentified items was in an invalid format.\" Row counts now parse as double\n(kept fractional for actual rows, truncated for plan rows), verified against\nreal postgres:17 and postgres:18 output.\n\nWhile in there, the plan pane gains the pgAdmin-style presentation split:\nthe default view is now the classic EXPLAIN (FORMAT TEXT) layout — node\nheaders with cost/actual figures, indented detail lines (Filter, Sort Key,\nHash Cond, ...), \"->\" arrows — rendered client-side from the same FORMAT\nJSON payload (no second round-trip: an EXPLAIN ANALYZE re-run would execute\nthe query again), with a chip toggle to the existing graphical tree. To feed\nit, ExplainNode now keeps the node detail properties it used to drop, plus\nthe header qualifiers (join type, alias, scan direction, aggregate\nstrategy), and the tree view titles nodes with the same header logic\n(\"Index Scan using idx on t\", \"Hash Left Join\").\n\nCo-Authored-By: Claude Fable 5 <noreply@anthropic.com>",
          "timestamp": "2026-07-14T20:59:42+02:00",
          "tree_id": "afee2bb6ac1c1b839ed7b940fbbc40586df4ff2d",
          "url": "https://github.com/Shman4ik/pgNimbus/commit/7396d3d0c783fdaa93c8069190c60ff8811e369f"
        },
        "date": 1784056504319,
        "tool": "customSmallerIsBetter",
        "benches": [
          {
            "name": "Startup, launch to first frame (NativeAOT)",
            "value": 157,
            "unit": "ms"
          },
          {
            "name": "Memory at first frame (NativeAOT)",
            "value": 150,
            "unit": "MB"
          },
          {
            "name": "Binary size (NativeAOT)",
            "value": 40.5,
            "unit": "MB"
          },
          {
            "name": "Publish size (NativeAOT, all files)",
            "value": 140.1,
            "unit": "MB"
          },
          {
            "name": "Startup, launch to first frame (JIT)",
            "value": 1727,
            "unit": "ms"
          },
          {
            "name": "Connect, cold pool",
            "value": 150.6,
            "unit": "ms"
          },
          {
            "name": "Round-trip, SELECT 1 warm",
            "value": 0.29,
            "unit": "ms"
          },
          {
            "name": "First row batch of a 100000-row SELECT",
            "value": 12,
            "unit": "ms"
          },
          {
            "name": "Stream 100000 rows",
            "value": 143.5,
            "unit": "ms"
          }
        ]
      },
      {
        "commit": {
          "author": {
            "email": "shman4ik@gmail.com",
            "name": "Dmitrii Shmanev",
            "username": "Shman4ik"
          },
          "committer": {
            "email": "noreply@github.com",
            "name": "GitHub",
            "username": "web-flow"
          },
          "distinct": true,
          "id": "f5dcef34c0422e99cdd44d0e16ca0ed7eb583d2e",
          "message": "Add project landing page and publish script (#112)\n\n* Add project landing page for GitHub Pages; mark Microsoft Store listing live\n\n- website/index.html: hand-written, self-contained landing page (light/dark\n  via prefers-color-scheme) with Store/Releases download links, feature\n  gallery from docs/screenshots, and a link to the benchmark history\n- scripts/website/publish-site.sh: assembles the page + assets into the\n  root of gh-pages (never touching dev/bench) and pushes\n- README/CLAUDE.md: the Microsoft Store listing passed certification and\n  is live — drop the 'in certification' notes, link the project page\n\nCo-Authored-By: Claude Fable 5 <noreply@anthropic.com>\nClaude-Session: https://claude.ai/code/session_01PYtsDovoMzB5uBCwJBk2fa\n\n* winget: install by name instead of Store ID\n\n'winget install pgNimbus --source msstore' reads better than the opaque\nStore ID; --source stays pinned so the command remains unambiguous once\nthe winget-pkgs community manifest registers the same name.\n\nCo-Authored-By: Claude Fable 5 <noreply@anthropic.com>\nClaude-Session: https://claude.ai/code/session_01PYtsDovoMzB5uBCwJBk2fa\n\n---------\n\nCo-authored-by: Claude <noreply@anthropic.com>",
          "timestamp": "2026-07-15T07:54:57+02:00",
          "tree_id": "fca5e533c67d3d02ebb6d24dccd43f464ac39496",
          "url": "https://github.com/Shman4ik/pgNimbus/commit/f5dcef34c0422e99cdd44d0e16ca0ed7eb583d2e"
        },
        "date": 1784095520058,
        "tool": "customSmallerIsBetter",
        "benches": [
          {
            "name": "Startup, launch to first frame (NativeAOT)",
            "value": 129,
            "unit": "ms"
          },
          {
            "name": "Memory at first frame (NativeAOT)",
            "value": 154.7,
            "unit": "MB"
          },
          {
            "name": "Binary size (NativeAOT)",
            "value": 40.6,
            "unit": "MB"
          },
          {
            "name": "Publish size (NativeAOT, all files)",
            "value": 140.2,
            "unit": "MB"
          },
          {
            "name": "Startup, launch to first frame (JIT)",
            "value": 1301,
            "unit": "ms"
          },
          {
            "name": "Connect, cold pool",
            "value": 116.1,
            "unit": "ms"
          },
          {
            "name": "Round-trip, SELECT 1 warm",
            "value": 0.2,
            "unit": "ms"
          },
          {
            "name": "First row batch of a 100000-row SELECT",
            "value": 7.9,
            "unit": "ms"
          },
          {
            "name": "Stream 100000 rows",
            "value": 99.8,
            "unit": "ms"
          }
        ]
      },
      {
        "commit": {
          "author": {
            "email": "shman4ik@gmail.com",
            "name": "Dmitrii Shmanev",
            "username": "Shman4ik"
          },
          "committer": {
            "email": "noreply@github.com",
            "name": "GitHub",
            "username": "web-flow"
          },
          "distinct": true,
          "id": "85a0856d441abc09a5dae3199894664fdccb119f",
          "message": "Exclude .pdb debug symbols from the MSIX package (#113)\n\nThe Microsoft Store MSIX build copied the whole publish directory\nverbatim, including native debug symbols like libSkiaSharp.pdb (~80MB)\nand libHarfBuzzSharp.pdb (~20MB) — over 100MB of dev-only files with\nno end-user benefit. The MSI installer already excludes *.pdb for this\nexact reason (see Product.wxs); apply the same exclusion here so the\nStore package isn't ~15x larger than the direct-download MSI.",
          "timestamp": "2026-07-15T08:30:47+02:00",
          "tree_id": "8a9f768f9101d32c028f3727a9d8e09d543084a9",
          "url": "https://github.com/Shman4ik/pgNimbus/commit/85a0856d441abc09a5dae3199894664fdccb119f"
        },
        "date": 1784097289463,
        "tool": "customSmallerIsBetter",
        "benches": [
          {
            "name": "Startup, launch to first frame (NativeAOT)",
            "value": 119,
            "unit": "ms"
          },
          {
            "name": "Memory at first frame (NativeAOT)",
            "value": 154.7,
            "unit": "MB"
          },
          {
            "name": "Binary size (NativeAOT)",
            "value": 40.6,
            "unit": "MB"
          },
          {
            "name": "Publish size (NativeAOT, all files)",
            "value": 140.2,
            "unit": "MB"
          },
          {
            "name": "Startup, launch to first frame (JIT)",
            "value": 1049,
            "unit": "ms"
          },
          {
            "name": "Connect, cold pool",
            "value": 95.2,
            "unit": "ms"
          },
          {
            "name": "Round-trip, SELECT 1 warm",
            "value": 0.23,
            "unit": "ms"
          },
          {
            "name": "First row batch of a 100000-row SELECT",
            "value": 6.1,
            "unit": "ms"
          },
          {
            "name": "Stream 100000 rows",
            "value": 84.6,
            "unit": "ms"
          }
        ]
      },
      {
        "commit": {
          "author": {
            "email": "shman4ik@gmail.com",
            "name": "Dmitrii Shmanev",
            "username": "Shman4ik"
          },
          "committer": {
            "email": "noreply@github.com",
            "name": "GitHub",
            "username": "web-flow"
          },
          "distinct": true,
          "id": "a8418be99d527622f831e1636b4f75089b46956a",
          "message": "Split schema loading cue so top bar and centered cue never double up (#117)\n\nThe initial (empty) schema load showed two indeterminate progress\nindicators at once — the thin top bar (bound to IsLoading) and the\ncentered \"Loading schema…\" cue (ShowInitialLoadingCue) — since both\nconditions were true. That reads as redundant.\n\nMake them mutually exclusive: add ShowRefreshLoadingBar\n(IsLoading && Schemas.Count > 0) and bind the top bar to it. Now the\nfirst, empty load shows only the centered cue, while a refresh of an\nalready-populated tree shows only the thin top bar (still never\noverlaying existing tree items).\n\nCo-authored-by: Claude Opus 4.8 <noreply@anthropic.com>",
          "timestamp": "2026-07-15T20:02:15+02:00",
          "tree_id": "0e484fb63943da47219c27452b4d984951ccb68e",
          "url": "https://github.com/Shman4ik/pgNimbus/commit/a8418be99d527622f831e1636b4f75089b46956a"
        },
        "date": 1784138895958,
        "tool": "customSmallerIsBetter",
        "benches": [
          {
            "name": "Startup, launch to first frame (NativeAOT)",
            "value": 158,
            "unit": "ms"
          },
          {
            "name": "Memory at first frame (NativeAOT)",
            "value": 150.5,
            "unit": "MB"
          },
          {
            "name": "Binary size (NativeAOT)",
            "value": 40.6,
            "unit": "MB"
          },
          {
            "name": "Publish size (NativeAOT, all files)",
            "value": 140.3,
            "unit": "MB"
          },
          {
            "name": "Startup, launch to first frame (JIT)",
            "value": 1747,
            "unit": "ms"
          },
          {
            "name": "Connect, cold pool",
            "value": 146.1,
            "unit": "ms"
          },
          {
            "name": "Round-trip, SELECT 1 warm",
            "value": 0.31,
            "unit": "ms"
          },
          {
            "name": "First row batch of a 100000-row SELECT",
            "value": 13.7,
            "unit": "ms"
          },
          {
            "name": "Stream 100000 rows",
            "value": 149.2,
            "unit": "ms"
          }
        ]
      },
      {
        "commit": {
          "author": {
            "email": "shman4ik@gmail.com",
            "name": "Dmitrii Shmanev",
            "username": "Shman4ik"
          },
          "committer": {
            "email": "noreply@github.com",
            "name": "GitHub",
            "username": "web-flow"
          },
          "distinct": true,
          "id": "7b8f163e6bed25eccc4be03de1583bdb90fe7cac",
          "message": "Match TextBox selection color to the SQL editor's brand-blue wash (#119)\n\nConnection-dialog fields (and every other plain TextBox) previously\nused FluentTheme's default OS/theme-accent selection highlight, which\ndidn't match the SQL editor's fixed brand-blue selection wash. Renamed\nAppEditorSelectionBrush to AppTextSelectionBrush and applied it via a\nglobal TextBox style so selection reads the same everywhere.\n\nCo-authored-by: Claude <noreply@anthropic.com>",
          "timestamp": "2026-07-15T20:31:17+02:00",
          "tree_id": "cfada2f2c1e9fab827ae377ff94eed08878fcc4b",
          "url": "https://github.com/Shman4ik/pgNimbus/commit/7b8f163e6bed25eccc4be03de1583bdb90fe7cac"
        },
        "date": 1784140573348,
        "tool": "customSmallerIsBetter",
        "benches": [
          {
            "name": "Startup, launch to first frame (NativeAOT)",
            "value": 166,
            "unit": "ms"
          },
          {
            "name": "Memory at first frame (NativeAOT)",
            "value": 150.3,
            "unit": "MB"
          },
          {
            "name": "Binary size (NativeAOT)",
            "value": 40.7,
            "unit": "MB"
          },
          {
            "name": "Publish size (NativeAOT, all files)",
            "value": 140.4,
            "unit": "MB"
          },
          {
            "name": "Startup, launch to first frame (JIT)",
            "value": 1679,
            "unit": "ms"
          },
          {
            "name": "Connect, cold pool",
            "value": 145,
            "unit": "ms"
          },
          {
            "name": "Round-trip, SELECT 1 warm",
            "value": 0.29,
            "unit": "ms"
          },
          {
            "name": "First row batch of a 100000-row SELECT",
            "value": 10.2,
            "unit": "ms"
          },
          {
            "name": "Stream 100000 rows",
            "value": 115.1,
            "unit": "ms"
          }
        ]
      },
      {
        "commit": {
          "author": {
            "email": "shman4ik@gmail.com",
            "name": "Shman4ik",
            "username": "Shman4ik"
          },
          "committer": {
            "email": "shman4ik@gmail.com",
            "name": "Shman4ik",
            "username": "Shman4ik"
          },
          "distinct": true,
          "id": "d49c773a8d4db32c3c6cfda62f61a004386c7b66",
          "message": "Results-grid row selection.",
          "timestamp": "2026-07-16T22:45:17+02:00",
          "tree_id": "697fcf666bbfa35fbc2d607e84b3f369ee724910",
          "url": "https://github.com/Shman4ik/pgNimbus/commit/d49c773a8d4db32c3c6cfda62f61a004386c7b66"
        },
        "date": 1784235016668,
        "tool": "customSmallerIsBetter",
        "benches": [
          {
            "name": "Startup, launch to first frame (NativeAOT)",
            "value": 157,
            "unit": "ms"
          },
          {
            "name": "Memory at first frame (NativeAOT)",
            "value": 150.9,
            "unit": "MB"
          },
          {
            "name": "Binary size (NativeAOT)",
            "value": 40.8,
            "unit": "MB"
          },
          {
            "name": "Publish size (NativeAOT, all files)",
            "value": 140.8,
            "unit": "MB"
          },
          {
            "name": "Startup, launch to first frame (JIT)",
            "value": 1750,
            "unit": "ms"
          },
          {
            "name": "Connect, cold pool",
            "value": 147.9,
            "unit": "ms"
          },
          {
            "name": "Round-trip, SELECT 1 warm",
            "value": 0.3,
            "unit": "ms"
          },
          {
            "name": "First row batch of a 100000-row SELECT",
            "value": 11.1,
            "unit": "ms"
          },
          {
            "name": "Stream 100000 rows",
            "value": 129.8,
            "unit": "ms"
          }
        ]
      },
      {
        "commit": {
          "author": {
            "email": "shman4ik@gmail.com",
            "name": "Dmitrii Shmanev",
            "username": "Shman4ik"
          },
          "committer": {
            "email": "noreply@github.com",
            "name": "GitHub",
            "username": "web-flow"
          },
          "distinct": true,
          "id": "2ea33685656248d7dad35a509a7c9cab77d62b6c",
          "message": "Benchmark publish-size: measure shipped files, not raw publish output (#129)\n\n* Results-grid row selection.\n\n* Benchmark publish-size: measure shipped files, not raw publish output\n\nThe publish_size_mb metric did a du -sb over the whole NativeAOT publish\ndirectory, which counts debug-symbol files (*.dbg on linux-x64, *.pdb on\nWindows) that the MSI (Product.wxs) and MSIX (build-msix.ps1) both\nexclude from the actual packages. Result: the ~100MB packaging-size win\nfrom #113 never showed up in the benchmark - the metric measured what\npublish leaves on disk, not what ships.\n\nNow the size sums only installer-shipped files (excluding *.pdb/*.dbg),\nand the publish dir is wiped before publishing so repeated local runs\nnever count stale leftovers (dotnet publish does not clean its output).\nThe metric is renamed to \"Publish size (NativeAOT, shipped files)\" -\ndeliberately, since its meaning changed; its gh-pages history restarts\nunder the new name.\n\nCo-Authored-By: Claude Fable 5 <noreply@anthropic.com>\n\n---------\n\nCo-authored-by: Claude Fable 5 <noreply@anthropic.com>",
          "timestamp": "2026-07-17T08:32:39+02:00",
          "tree_id": "defc0f98249911d554f272024fbd058398d1d159",
          "url": "https://github.com/Shman4ik/pgNimbus/commit/2ea33685656248d7dad35a509a7c9cab77d62b6c"
        },
        "date": 1784272630392,
        "tool": "customSmallerIsBetter",
        "benches": [
          {
            "name": "Startup, launch to first frame (NativeAOT)",
            "value": 161,
            "unit": "ms"
          },
          {
            "name": "Memory at first frame (NativeAOT)",
            "value": 151.6,
            "unit": "MB"
          },
          {
            "name": "Binary size (NativeAOT)",
            "value": 41.1,
            "unit": "MB"
          },
          {
            "name": "Publish size (NativeAOT, shipped files)",
            "value": 54.5,
            "unit": "MB"
          },
          {
            "name": "Startup, launch to first frame (JIT)",
            "value": 1720,
            "unit": "ms"
          },
          {
            "name": "Connect, cold pool",
            "value": 146.9,
            "unit": "ms"
          },
          {
            "name": "Round-trip, SELECT 1 warm",
            "value": 0.38,
            "unit": "ms"
          },
          {
            "name": "First row batch of a 100000-row SELECT",
            "value": 12.9,
            "unit": "ms"
          },
          {
            "name": "Stream 100000 rows",
            "value": 127.7,
            "unit": "ms"
          }
        ]
      },
      {
        "commit": {
          "author": {
            "email": "shman4ik@gmail.com",
            "name": "Dmitrii Shmanev",
            "username": "Shman4ik"
          },
          "committer": {
            "email": "noreply@github.com",
            "name": "GitHub",
            "username": "web-flow"
          },
          "distinct": true,
          "id": "fb57d909f77ccb69d3ce1a5d656ee117ca644a55",
          "message": "Tab drag-reorder, app menu, and a centered command-palette Search pill (#132)\n\n* Tab strip: drag-to-reorder; add app menu with file/tab commands\n\nTabs on the query strip now reorder by dragging (live, browser-style:\nthe dragged tab lands after every tab whose center the pointer passes,\nwith edge auto-scroll when the strip overflows). A plain click still\njust switches tabs - the drag only arms past the existing 4px\nthreshold. The new order persists via the workspace snapshot, which\nalready serializes Tabs in collection order.\n\nNew top-left app menu button: New query tab, Open .sql file, Open\nrecent (rebuilt from the live recent-files list on every open), Save,\nSave as, Close tab, Switch connection, Open connection in new window.\nOpen/save/recent previously lived only in the command palette -\ninvisible to anyone who doesn't know Ctrl+K. Shortcut captions are set\nin BuildKeyBindings so they track the live Ctrl/Cmd scheme, per the\nno-hardcoded-gestures rule.\n\nCo-Authored-By: Claude Fable 5 <noreply@anthropic.com>\n\n* Command bar: centered Search pill opens the command palette\n\nVS Code-style: a quiet card-toned capsule centered in the command bar\n(magnify icon + \"Search\" + the live Ctrl/Cmd+K caption) that opens the\nsame palette as Ctrl+K/P - its one visible entry point. The bar's\nDockPanel becomes an Auto|*|Auto grid so the pill truly centers between\nthe left cluster (menu, sidebar, breadcrumb) and the right icon cluster,\nshrinking on narrow windows instead of overlapping.\n\nCo-Authored-By: Claude Fable 5 <noreply@anthropic.com>\n\n* Search pill: say what it searches\n\n\"Search\" alone reads as editor text search; the palette is a fuzzy\nfinder over tables, saved queries, recent files, and actions - label it\naccordingly (ellipsized on narrow windows).\n\nCo-Authored-By: Claude Fable 5 <noreply@anthropic.com>\n\n* Add Preferences menu item with Cmd+, shortcut\n\nAdded a \"Preferences…\" item to the MainWindow menu flyout, bound to ShowPreferencesCommand. Assigned Command + ',' as its keyboard shortcut in MainWindow.axaml.cs.\n\n* CLAUDE.md: document Preferences in the app menu list\n\nThe ☰ menu gained a Preferences… item (Cmd/Ctrl+,) in 865bf46; the\nproject-memory list of its contents hadn't caught up.\n\nCo-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>\n\n---------\n\nCo-authored-by: Claude Fable 5 <noreply@anthropic.com>",
          "timestamp": "2026-07-18T11:02:26+02:00",
          "tree_id": "225aafad4839d01d84fd3d5bb48aa36715ee5c58",
          "url": "https://github.com/Shman4ik/pgNimbus/commit/fb57d909f77ccb69d3ce1a5d656ee117ca644a55"
        },
        "date": 1784365697901,
        "tool": "customSmallerIsBetter",
        "benches": [
          {
            "name": "Startup, launch to first frame (NativeAOT)",
            "value": 160,
            "unit": "ms"
          },
          {
            "name": "Memory at first frame (NativeAOT)",
            "value": 152,
            "unit": "MB"
          },
          {
            "name": "Binary size (NativeAOT)",
            "value": 41.1,
            "unit": "MB"
          },
          {
            "name": "Publish size (NativeAOT, shipped files)",
            "value": 54.5,
            "unit": "MB"
          },
          {
            "name": "Startup, launch to first frame (JIT)",
            "value": 1765,
            "unit": "ms"
          },
          {
            "name": "Connect, cold pool",
            "value": 148.8,
            "unit": "ms"
          },
          {
            "name": "Round-trip, SELECT 1 warm",
            "value": 0.35,
            "unit": "ms"
          },
          {
            "name": "First row batch of a 100000-row SELECT",
            "value": 10.9,
            "unit": "ms"
          },
          {
            "name": "Stream 100000 rows",
            "value": 144.7,
            "unit": "ms"
          }
        ]
      },
      {
        "commit": {
          "author": {
            "email": "shman4ik@gmail.com",
            "name": "Dmitrii Shmanev",
            "username": "Shman4ik"
          },
          "committer": {
            "email": "noreply@github.com",
            "name": "GitHub",
            "username": "web-flow"
          },
          "distinct": true,
          "id": "93b00162264eb5333a24639282ea1a677bd0cec1",
          "message": "Supply-chain proofs: SLSA attestations, CycloneDX SBOM, NuGet vulnerability gates (#142)\n\n* Supply-chain proofs: SLSA attestations, CycloneDX SBOM, NuGet vulnerability gates\n\n- release job attests build provenance (Sigstore) for every published\n  asset; verify with `gh attestation verify <file> --repo Shman4ik/pgNimbus`\n- build-linux x64 leg generates a CycloneDX JSON SBOM of the App's NuGet\n  graph (-c Release keeps the Debug-only DiagnosticsSupport ref out),\n  shipped/checksummed/attested alongside the binaries\n- Directory.Build.props: NuGetAuditMode=all + NU1902-NU1904 as errors, so\n  any build fails on known moderate+ advisories (transitive included)\n- ci.yml: dependency-review-action blocks PRs adding vulnerable packages\n\nCo-Authored-By: Claude Fable 5 <noreply@anthropic.com>\n\n* README: how to verify a download (attestation, checksums, SBOM)\n\nCo-Authored-By: Claude Fable 5 <noreply@anthropic.com>\n\n---------\n\nCo-authored-by: Claude Fable 5 <noreply@anthropic.com>",
          "timestamp": "2026-07-18T17:43:25+02:00",
          "tree_id": "3e56624ee7c9e34336a279f1113836445744aa57",
          "url": "https://github.com/Shman4ik/pgNimbus/commit/93b00162264eb5333a24639282ea1a677bd0cec1"
        },
        "date": 1784389790944,
        "tool": "customSmallerIsBetter",
        "benches": [
          {
            "name": "Startup, launch to first frame (NativeAOT)",
            "value": 159,
            "unit": "ms"
          },
          {
            "name": "Memory at first frame (NativeAOT)",
            "value": 157,
            "unit": "MB"
          },
          {
            "name": "Binary size (NativeAOT)",
            "value": 41.1,
            "unit": "MB"
          },
          {
            "name": "Publish size (NativeAOT, shipped files)",
            "value": 54.5,
            "unit": "MB"
          },
          {
            "name": "Startup, launch to first frame (JIT)",
            "value": 1379,
            "unit": "ms"
          },
          {
            "name": "Connect, cold pool",
            "value": 119.5,
            "unit": "ms"
          },
          {
            "name": "Round-trip, SELECT 1 warm",
            "value": 0.23,
            "unit": "ms"
          },
          {
            "name": "First row batch of a 100000-row SELECT",
            "value": 7.4,
            "unit": "ms"
          },
          {
            "name": "Stream 100000 rows",
            "value": 100.6,
            "unit": "ms"
          }
        ]
      },
      {
        "commit": {
          "author": {
            "email": "shman4ik@gmail.com",
            "name": "Dmitrii Shmanev",
            "username": "Shman4ik"
          },
          "committer": {
            "email": "noreply@github.com",
            "name": "GitHub",
            "username": "web-flow"
          },
          "distinct": true,
          "id": "e8f114dea24218b5e661868e715b4dcbbd0dd6d7",
          "message": "UX: type-aware display for PostgreSQL types (#146)\n\n* Schema tree: type-category icons on column nodes\n\nAdd a pure Core classifier (PgTypeCategorizer) that maps a Postgres type\nname — from either the format_type spellings the schema tree reads or the\nwire GetDataTypeName strings a result set carries — to a coarse\nPgTypeCategory family (numeric, text, date/time, boolean, uuid, json,\nnetwork, geometric, range, binary, bit-string, vector, full-text, array).\nOne family per icon keeps a single visual language across the dozens of\nconcrete type names. Unit-tested in PgNimbus.Core.Tests.\n\nThe App side maps each category to a small monochrome MDI glyph\n(PgTypeVisuals + two IValueConverters) and renders it next to the column's\ntype text in the schema tree. Geometries are parsed once and cached; a bad\npath falls back to no icon so it can never crash column virtualization.\nEnum/composite/domain types (unresolvable from a bare name) show no icon.\n\nVerified on the demo Neon DB (commerce.products: bigint/citext/text/jsonb/\nbox/text[]/vector(3)/boolean/interval/numrange/timestamptz/tsvector) in\nboth light and dark themes.\n\nCo-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>\n\n* Results grid: type-family icon + rich tooltip in column headers\n\nEach results-grid column header now shows the type-family glyph (the same\nPgTypeVisuals vocabulary as the schema tree) beside the column name, for\nANY result set — the type comes from the wire-protocol DataTypeName that\nevery ColumnInfo carries, not just editable browses. The header tooltip\ncarries the full type name, its family, and — in browse mode, where the\ntable's real columns are known — the primary-key / not-null flags.\n\nQueryViewModel exposes a focused ColumnTypeName(index) accessor rather than\nthe whole ColumnInfo list; RebuildColumns builds an icon+name header via a\nnew CreateColumnHeader helper.\n\nVerified on the demo Neon DB in both themes: commerce.customers headers\nshow uuid (id), text (first/last/full_name), array (tags) and json\n(interests) icons; a domain column (email → commerce.email_addr) correctly\nshows none, matching how enums render in the tree.\n\nCo-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>\n\n* Type icons: resolve domain columns to their base-type family\n\nA domain's own name classifies as Other (no icon), so a domain column used\nto render blank. Add PgTypeCategorizer.ClassifierType, which falls back to a\ndomain's resolved base type when the declared type has no family of its own —\nso a domain over citext shows the text glyph, a domain over inet the network\nglyph, and so on. Unit-tested.\n\nWired through both surfaces: ColumnNode exposes DomainBaseType + a\nTypeClassifier the schema tree binds its icon to; the results-grid header\nclassifies from the base type too, while its tooltip keeps the declared\n(domain) type name plus the resolved family label.\n\nVerified on the demo Neon DB (commerce.customers.email → commerce.email_addr\nover citext) in both tree and grid: the column now shows the text icon.\n\nCo-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>\n\n* Results grid: type-aware cell rendering (numeric align, boolean check/cross)\n\nEach result column now renders by its resolved type family, for any query:\n- Numeric cells right-align and use the mono stack the editor/inspector use,\n  so digits line up and magnitudes compare down the column; the header\n  right-aligns to sit over them.\n- Boolean cells show a centered check/cross glyph (via BoolCellGlyphConverter)\n  instead of the literal \"true\"/\"false\"; SQL NULL keeps the dim \"NULL\"\n  marker every column uses.\n\nRebuildColumns computes each column's category once (domain-resolved) and\nthreads it into both the header and ResultTextColumn; PgTypeVisuals gains\ncategory-based IconFor/LabelFor so the header no longer re-classifies.\n\nVerified on the demo Neon DB (public.customers: id right-aligned/mono,\nis_active check/cross) in both light and dark themes.\n\nCo-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>\n\n* QueryEngine: text-fallback for any unmapped type, not just composites\n\nBrowsing a table with a pgvector column threw \"Reading as 'System.Object'\nis not supported for fields having DataTypeName 'public.vector'\": the\ntext-fallback mask only flagged composites (and containers of them), but\nthe identical failure hits any base type Npgsql has no handler for — an\nextension type with no plugin loaded (pgvector's vector/halfvec/sparsevec,\nPostGIS geometry, ltree, …). Npgsql resolves such a column's CLR type to\nSystem.Object and GetValue then throws.\n\nDetect it generically by resolved CLR type (GetFieldType == object, or an\nunresolvable type) rather than by an allowlist of names, so every such type\nis re-requested in text format and arrives as its Postgres literal — the\nshape the grid already renders. Normal result sets (all types mapped) still\nbuild a null mask and skip the re-execution, and the caller opt-in that\nguards double-executing side effects is unchanged. Covers both the\nstreaming and materialized/script paths.\n\nValidated on the demo Neon DB: commerce.products (vector embedding),\norg.units (ltree path), commerce.orders (address composite + enums) now\nbrowse instead of erroring.\n\nCo-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>\n\n* add git ignore\n\n* Grid: readable display for bit-string and bytea values\n\nTwo Npgsql CLR types whose default ToString is the class name, not the\nvalue, showed up literally in the grid:\n\n- bit / bit varying map to System.Collections.BitArray → cells read\n  \"System.Collections.BitArray\". Route them through the text fallback (by\n  resolved CLR type) so Postgres returns the bit literal (\"10110000\"); this\n  also fixes the inspector and exports uniformly, and bit values are tiny so\n  the extra round trip is cheap.\n- bytea maps to byte[] → cells read \"System.Byte[]\". It's deliberately kept\n  as byte[] (not text-fallback) so a large blob isn't materialized as\n  megabytes of hex inline; instead the grid converter shows a capped \\x-hex\n  preview, matching the full hex the cell inspector already renders.\n\nhstore needs no change: with no hstore plugin enabled it's unmapped, so it\nalready flows through the text fallback as its \"k\"=>\"v\" literal.\n\nValidated on the demo Neon DB: iot.devices firmware bit(16)/flags bit(8)\nshow bit strings; commerce.customers.avatar_thumb (bytea) shows \\x-hex.\n\nCo-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>\n\n* Type icons for enum/composite columns; hstore reads as text\n\nTwo follow-ups from browsing the demo DB:\n\n- Enum and composite columns showed no icon: their family can't be told from\n  a bare type name, so they fell to Other. In browse/tree contexts the\n  catalog kind is already known (ColumnValueEditor), so add PgTypeCategory\n  Enum/Composite and a CategorizeColumn helper that lets the editor decide,\n  with a list glyph for enums and a columns glyph for composites. The tree\n  and grid-header bindings now carry a resolved PgTypeCategory (ColumnNode\n  exposes Category; the two converters take a category), so the string-based\n  Icon/Label helpers are gone. Arbitrary queries, which only have the wire\n  type name, still can't distinguish an enum and fall back to no icon.\n\n- hstore rendered as \"System.Collections.Generic.Dictionary`2[…]\": Npgsql\n  maps it to Dictionary<string,string> (its default ToString is the class\n  name). Add that CLR type to the text fallback alongside BitArray, so it\n  arrives as its \"k\"=>\"v\" literal.\n\nVerified on the demo Neon DB: iot.devices.status shows the enum icon;\ncommerce.customers.attrs shows \"lang\"=>\"de\", \"referrer\"=>\"organic\".\n\nCo-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>\n\n---------\n\nCo-authored-by: Claude Opus 4.8 <noreply@anthropic.com>",
          "timestamp": "2026-07-21T08:03:17+02:00",
          "tree_id": "3fa6b05887872acc67c6a7790299b230c3058533",
          "url": "https://github.com/Shman4ik/pgNimbus/commit/e8f114dea24218b5e661868e715b4dcbbd0dd6d7"
        },
        "date": 1784614186653,
        "tool": "customSmallerIsBetter",
        "benches": [
          {
            "name": "Startup, launch to first frame (NativeAOT)",
            "value": 151,
            "unit": "ms"
          },
          {
            "name": "Memory at first frame (NativeAOT)",
            "value": 157.4,
            "unit": "MB"
          },
          {
            "name": "Binary size (NativeAOT)",
            "value": 41.3,
            "unit": "MB"
          },
          {
            "name": "Publish size (NativeAOT, shipped files)",
            "value": 54.7,
            "unit": "MB"
          },
          {
            "name": "Startup, launch to first frame (JIT)",
            "value": 1330,
            "unit": "ms"
          },
          {
            "name": "Connect, cold pool",
            "value": 113.6,
            "unit": "ms"
          },
          {
            "name": "Round-trip, SELECT 1 warm",
            "value": 0.34,
            "unit": "ms"
          },
          {
            "name": "First row batch of a 100000-row SELECT",
            "value": 8,
            "unit": "ms"
          },
          {
            "name": "Stream 100000 rows",
            "value": 105.6,
            "unit": "ms"
          }
        ]
      },
      {
        "commit": {
          "author": {
            "email": "shman4ik@gmail.com",
            "name": "Dmitrii Shmanev",
            "username": "Shman4ik"
          },
          "committer": {
            "email": "noreply@github.com",
            "name": "GitHub",
            "username": "web-flow"
          },
          "distinct": true,
          "id": "14aac53d6bb971584d8d15a5fb412c76b6ee76a8",
          "message": "Merge pull request #151 from Shman4ik/claude/readme-backlog-priority-s1glb6\n\nAdd a who-blocks-whom lock tree to the Server Activity window",
          "timestamp": "2026-07-21T18:12:47+02:00",
          "tree_id": "f1595dded18571630e33686d177fbda7f0d9a709",
          "url": "https://github.com/Shman4ik/pgNimbus/commit/14aac53d6bb971584d8d15a5fb412c76b6ee76a8"
        },
        "date": 1784661932193,
        "tool": "customSmallerIsBetter",
        "benches": [
          {
            "name": "Startup, launch to first frame (NativeAOT)",
            "value": 119,
            "unit": "ms"
          },
          {
            "name": "Memory at first frame (NativeAOT)",
            "value": 158.1,
            "unit": "MB"
          },
          {
            "name": "Binary size (NativeAOT)",
            "value": 41.7,
            "unit": "MB"
          },
          {
            "name": "Publish size (NativeAOT, shipped files)",
            "value": 55,
            "unit": "MB"
          },
          {
            "name": "Startup, launch to first frame (JIT)",
            "value": 1134,
            "unit": "ms"
          },
          {
            "name": "Connect, cold pool",
            "value": 97.1,
            "unit": "ms"
          },
          {
            "name": "Round-trip, SELECT 1 warm",
            "value": 0.21,
            "unit": "ms"
          },
          {
            "name": "First row batch of a 100000-row SELECT",
            "value": 6.6,
            "unit": "ms"
          },
          {
            "name": "Stream 100000 rows",
            "value": 86.3,
            "unit": "ms"
          }
        ]
      },
      {
        "commit": {
          "author": {
            "email": "shman4ik@gmail.com",
            "name": "Dmitrii Shmanev",
            "username": "Shman4ik"
          },
          "committer": {
            "email": "noreply@github.com",
            "name": "GitHub",
            "username": "web-flow"
          },
          "distinct": true,
          "id": "36812ff5c229908d1e41fe80d7e64f78a298a530",
          "message": "Merge pull request #167 from Shman4ik/claude/screen-optimization-5e0e71\n\nMake reconnecting one keystroke, and thin out the connection dialog",
          "timestamp": "2026-07-26T21:55:21+02:00",
          "tree_id": "2b424154685120592a51b4264d587421eecf3985",
          "url": "https://github.com/Shman4ik/pgNimbus/commit/36812ff5c229908d1e41fe80d7e64f78a298a530"
        },
        "date": 1785096206521,
        "tool": "customSmallerIsBetter",
        "benches": [
          {
            "name": "Startup, launch to first frame (NativeAOT)",
            "value": 202,
            "unit": "ms"
          },
          {
            "name": "Memory at first frame (NativeAOT)",
            "value": 153.9,
            "unit": "MB"
          },
          {
            "name": "Binary size (NativeAOT)",
            "value": 41.9,
            "unit": "MB"
          },
          {
            "name": "Publish size (NativeAOT, shipped files)",
            "value": 55.3,
            "unit": "MB"
          },
          {
            "name": "Startup, launch to first frame (JIT)",
            "value": 1890,
            "unit": "ms"
          },
          {
            "name": "Connect, cold pool",
            "value": 152.2,
            "unit": "ms"
          },
          {
            "name": "Round-trip, SELECT 1 warm",
            "value": 0.32,
            "unit": "ms"
          },
          {
            "name": "First row batch of a 100000-row SELECT",
            "value": 10.9,
            "unit": "ms"
          },
          {
            "name": "Stream 100000 rows",
            "value": 136.6,
            "unit": "ms"
          }
        ]
      },
      {
        "commit": {
          "author": {
            "email": "shman4ik@gmail.com",
            "name": "Dmitrii Shmanev",
            "username": "Shman4ik"
          },
          "committer": {
            "email": "noreply@github.com",
            "name": "GitHub",
            "username": "web-flow"
          },
          "distinct": true,
          "id": "36812ff5c229908d1e41fe80d7e64f78a298a530",
          "message": "Merge pull request #167 from Shman4ik/claude/screen-optimization-5e0e71\n\nMake reconnecting one keystroke, and thin out the connection dialog",
          "timestamp": "2026-07-26T21:55:21+02:00",
          "tree_id": "2b424154685120592a51b4264d587421eecf3985",
          "url": "https://github.com/Shman4ik/pgNimbus/commit/36812ff5c229908d1e41fe80d7e64f78a298a530"
        },
        "date": 1785098748443,
        "tool": "customSmallerIsBetter",
        "benches": [
          {
            "name": "Startup, launch to first frame (NativeAOT)",
            "value": 200,
            "unit": "ms"
          },
          {
            "name": "Memory at first frame (NativeAOT)",
            "value": 153.8,
            "unit": "MB"
          },
          {
            "name": "Binary size (NativeAOT)",
            "value": 41.9,
            "unit": "MB"
          },
          {
            "name": "Publish size (NativeAOT, shipped files)",
            "value": 55.3,
            "unit": "MB"
          },
          {
            "name": "Startup, launch to first frame (JIT)",
            "value": 1731,
            "unit": "ms"
          },
          {
            "name": "Connect, cold pool",
            "value": 146.4,
            "unit": "ms"
          },
          {
            "name": "Round-trip, SELECT 1 warm",
            "value": 0.3,
            "unit": "ms"
          },
          {
            "name": "First row batch of a 100000-row SELECT",
            "value": 10.2,
            "unit": "ms"
          },
          {
            "name": "Stream 100000 rows",
            "value": 130.3,
            "unit": "ms"
          }
        ]
      },
      {
        "commit": {
          "author": {
            "email": "shman4ik@gmail.com",
            "name": "Dmitrii Shmanev",
            "username": "Shman4ik"
          },
          "committer": {
            "email": "noreply@github.com",
            "name": "GitHub",
            "username": "web-flow"
          },
          "distinct": true,
          "id": "05d261cb6875ec8a23ec5433216ef09005d22659",
          "message": "Merge pull request #168 from Shman4ik/fix/aot-reflection-json-cell-inspector\n\nStop the cell inspector crashing the AOT build on JSON",
          "timestamp": "2026-07-26T22:40:18+02:00",
          "tree_id": "2004bb29733d49574cd4f02c589508374c77c2f1",
          "url": "https://github.com/Shman4ik/pgNimbus/commit/05d261cb6875ec8a23ec5433216ef09005d22659"
        },
        "date": 1785171601071,
        "tool": "customSmallerIsBetter",
        "benches": [
          {
            "name": "Startup, launch to first frame (NativeAOT)",
            "value": 180,
            "unit": "ms"
          },
          {
            "name": "Memory at first frame (NativeAOT)",
            "value": 153.8,
            "unit": "MB"
          },
          {
            "name": "Binary size (NativeAOT)",
            "value": 41.9,
            "unit": "MB"
          },
          {
            "name": "Publish size (NativeAOT, shipped files)",
            "value": 55.3,
            "unit": "MB"
          },
          {
            "name": "Startup, launch to first frame (JIT)",
            "value": 1809,
            "unit": "ms"
          },
          {
            "name": "Connect, cold pool",
            "value": 151,
            "unit": "ms"
          },
          {
            "name": "Round-trip, SELECT 1 warm",
            "value": 0.36,
            "unit": "ms"
          },
          {
            "name": "First row batch of a 100000-row SELECT",
            "value": 10.8,
            "unit": "ms"
          },
          {
            "name": "Stream 100000 rows",
            "value": 146.2,
            "unit": "ms"
          }
        ]
      },
      {
        "commit": {
          "author": {
            "email": "shman4ik@gmail.com",
            "name": "Dmitrii Shmanev",
            "username": "Shman4ik"
          },
          "committer": {
            "email": "noreply@github.com",
            "name": "GitHub",
            "username": "web-flow"
          },
          "distinct": true,
          "id": "228cb2b0babba5ebd8a8a341676fbde7b557d789",
          "message": "Merge pull request #169 from Shman4ik/docs/refresh-screenshots\n\nRefresh app screenshots against current UI",
          "timestamp": "2026-07-27T22:16:14+02:00",
          "tree_id": "fe7936f1fcb56d18b38038f4ad2f8e8465573720",
          "url": "https://github.com/Shman4ik/pgNimbus/commit/228cb2b0babba5ebd8a8a341676fbde7b557d789"
        },
        "date": 1785183779139,
        "tool": "customSmallerIsBetter",
        "benches": [
          {
            "name": "Startup, launch to first frame (NativeAOT)",
            "value": 195,
            "unit": "ms"
          },
          {
            "name": "Memory at first frame (NativeAOT)",
            "value": 154.4,
            "unit": "MB"
          },
          {
            "name": "Binary size (NativeAOT)",
            "value": 42,
            "unit": "MB"
          },
          {
            "name": "Publish size (NativeAOT, shipped files)",
            "value": 55.3,
            "unit": "MB"
          },
          {
            "name": "Startup, launch to first frame (JIT)",
            "value": 1702,
            "unit": "ms"
          },
          {
            "name": "Connect, cold pool",
            "value": 146.1,
            "unit": "ms"
          },
          {
            "name": "Round-trip, SELECT 1 warm",
            "value": 0.3,
            "unit": "ms"
          },
          {
            "name": "First row batch of a 100000-row SELECT",
            "value": 9.9,
            "unit": "ms"
          },
          {
            "name": "Stream 100000 rows",
            "value": 120.2,
            "unit": "ms"
          }
        ]
      },
      {
        "commit": {
          "author": {
            "email": "shman4ik@gmail.com",
            "name": "Dmitrii Shmanev",
            "username": "Shman4ik"
          },
          "committer": {
            "email": "noreply@github.com",
            "name": "GitHub",
            "username": "web-flow"
          },
          "distinct": true,
          "id": "cf9991e7904f4167d638342aec86b88e44f7e9c4",
          "message": "Merge pull request #172 from Shman4ik/polish/activity-window\n\nCollapse the activity window into one band of chrome",
          "timestamp": "2026-07-28T07:55:59+02:00",
          "tree_id": "9338114480ca3c4bada5e8c8292ec68a574f1030",
          "url": "https://github.com/Shman4ik/pgNimbus/commit/cf9991e7904f4167d638342aec86b88e44f7e9c4"
        },
        "date": 1785218616934,
        "tool": "customSmallerIsBetter",
        "benches": [
          {
            "name": "Startup, launch to first frame (NativeAOT)",
            "value": 191,
            "unit": "ms"
          },
          {
            "name": "Memory at first frame (NativeAOT)",
            "value": 154.4,
            "unit": "MB"
          },
          {
            "name": "Binary size (NativeAOT)",
            "value": 42,
            "unit": "MB"
          },
          {
            "name": "Publish size (NativeAOT, shipped files)",
            "value": 55.3,
            "unit": "MB"
          },
          {
            "name": "Startup, launch to first frame (JIT)",
            "value": 1899,
            "unit": "ms"
          },
          {
            "name": "Connect, cold pool",
            "value": 153.4,
            "unit": "ms"
          },
          {
            "name": "Round-trip, SELECT 1 warm",
            "value": 0.39,
            "unit": "ms"
          },
          {
            "name": "First row batch of a 100000-row SELECT",
            "value": 11.4,
            "unit": "ms"
          },
          {
            "name": "Stream 100000 rows",
            "value": 152.1,
            "unit": "ms"
          }
        ]
      },
      {
        "commit": {
          "author": {
            "email": "shman4ik@gmail.com",
            "name": "Dmitrii Shmanev",
            "username": "Shman4ik"
          },
          "committer": {
            "email": "noreply@github.com",
            "name": "GitHub",
            "username": "web-flow"
          },
          "distinct": true,
          "id": "46df4ad2500bb18fe80d4b6083b627fcd2ad9afd",
          "message": "Merge pull request #183 from Shman4ik/claude/schema-context-menu-filter-139360\n\nSchema context menu + per-connection autocomplete exclusions",
          "timestamp": "2026-08-01T18:17:07+02:00",
          "tree_id": "eeb117e5bb21e6cd9158190f1da5aec2b38dabf1",
          "url": "https://github.com/Shman4ik/pgNimbus/commit/46df4ad2500bb18fe80d4b6083b627fcd2ad9afd"
        },
        "date": 1785821852233,
        "tool": "customSmallerIsBetter",
        "benches": [
          {
            "name": "Startup, launch to first frame (NativeAOT)",
            "value": 190,
            "unit": "ms"
          },
          {
            "name": "Memory at first frame (NativeAOT)",
            "value": 159.6,
            "unit": "MB"
          },
          {
            "name": "Binary size (NativeAOT)",
            "value": 42,
            "unit": "MB"
          },
          {
            "name": "Publish size (NativeAOT, shipped files)",
            "value": 55.4,
            "unit": "MB"
          },
          {
            "name": "Startup, launch to first frame (JIT)",
            "value": 1657,
            "unit": "ms"
          },
          {
            "name": "Connect, cold pool",
            "value": 137.8,
            "unit": "ms"
          },
          {
            "name": "Round-trip, SELECT 1 warm",
            "value": 0.31,
            "unit": "ms"
          },
          {
            "name": "First row batch of a 100000-row SELECT",
            "value": 9.5,
            "unit": "ms"
          },
          {
            "name": "Stream 100000 rows",
            "value": 121.1,
            "unit": "ms"
          }
        ]
      }
    ]
  }
}