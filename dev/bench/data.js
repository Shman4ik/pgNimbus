window.BENCHMARK_DATA = {
  "lastUpdate": 1783969694723,
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
      }
    ]
  }
}