# Completion audit stand

The fixtures behind the second completion audit,
[`docs/design/sql-completion-audit-2.md`](../../../docs/design/sql-completion-audit-2.md).
They measure how much completion helps someone typing ordinary SQL, and
whether it ever gets in their way. Everything runs offline from the catalog
snapshot below; a server is needed only to take a new snapshot.

| File | What it is |
| --- | --- |
| `catalog.json` | The stand's completion catalog, read by `SqlCompletionProvider.ReadCatalogAsync` (the read the editor's catalog refresh does) |
| `corpus.sql` | Everyday queries, one clause per line, separated by a line holding only `---`: the audit's 25, then the ones each package added for the grammar it taught completion (package M: a window function, FILTER, CASE, ON CONFLICT, MERGE) |
| `saas.sql` | A SaaS-style schema loaded after `scripts/demo` 01–06: shared column names, two FKs to one table, a composite FK, enums, a domain, views, functions, a procedure, a quoted mixed-case table |
| `cases.txt` | Caret positions (`|`) for the `cases` report, grouped by `##` headings |
| `hints.txt` | Calls for the argument-hint report |

## Measurements

From the repository root:

```
dotnet run -c Release --project tools/CompletionBench -- quality
dotnet run -c Release --project tools/CompletionBench -- cases
dotnet run -c Release --project tools/CompletionBench -- hints
dotnet test --project PgNimbus.App.Tests -- --treenode-filter "/*/*/CompletionTypingReplayTests/*"
```

- `quality`: for every word of the corpus, where the intended row lands after
  1, 2 and 3 typed characters (typing left to right), plus the words that were
  not first after two characters.
- `cases` / `hints`: what the popup and the argument hint hold at each listed
  caret.
- `CompletionTypingReplayTests` (headless editor, real keys): the corpus typed
  without looking at the popup must come out unchanged; and the keystroke
  saving of a user who always picks the best row. Both run in every build
  (package K made the first pass; the second has a floor each package raises
  with the saving) and print their numbers into the test report
  (`TestResults/*.tunit-report.json`; a passing test's output isn't printed
  to the console).

The audit's numbers (section 3 of the design doc) are these tools' output at
the audited revision. Compare a change against a run of the same tools on the
base branch, not against numbers in a document.

## A new catalog snapshot

The snapshot has to change when the corpus needs objects it does not hold, or
when the catalog read itself changes (new object kinds, flags, comments).

1. A database with the demo data and the extra schema. The demo needs the
   `vector` extension, so use an image that ships pgvector:

   ```
   wslc run -d --name pgn-demo -e POSTGRES_PASSWORD=postgres -p 5440:5432 pgvector/pgvector:pg17
   psql "postgres://postgres:postgres@localhost:5440/postgres" -c "CREATE DATABASE audit"
   ./scripts/demo/seed.ps1 "postgres://postgres:postgres@localhost:5440/audit"
   psql "postgres://postgres:postgres@localhost:5440/audit" -v ON_ERROR_STOP=1 -f tools/CompletionBench/Audit/saas.sql
   ```

   The search_path must stay the default (`public`): the corpus writes public
   tables without a schema and relies on that.

2. `dotnet run -c Release --project tools/CompletionBench -- dump "Host=localhost;Port=5440;Database=audit;Username=postgres;Password=postgres" tools/CompletionBench/Audit/catalog.json`

A new snapshot moves every number, so take it in its own commit and record
the before/after of `quality` and the replay in the commit message.
