# CompletionBench

Measures what the SQL editor's completion costs per keystroke, on synthetic
documents (1k, 10k and 100k characters) and synthetic catalogs (up to 100k
tables and 1M columns). No database and no user SQL are involved.

```
dotnet run -c Release --project tools/CompletionBench
```

It prints one Markdown table per catalog: the median and p95 over 30 timed
calls (after 5 warm-up calls), and the bytes allocated per call. The budgets
and the last recorded results are in
[`docs/design/sql-editing-experience.md`](../../docs/design/sql-editing-experience.md), section 8.
Numbers depend on the machine. Compare runs on the same machine, not against
another machine's numbers.
