window.BENCHMARK_DATA = {
  "lastUpdate": 1783786741730,
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
      }
    ]
  }
}