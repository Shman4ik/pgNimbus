# Microsoft Store screenshots

The screenshots for the Microsoft Store listing, in the order the listing shows them.
Every PNG here is **generated** by the headless harness in
[`tools/Screenshot`](../../../tools/Screenshot) from its fixture data; nothing is captured
by hand. The mapping lives in [`Marketing.cs`](../../../tools/Screenshot/Marketing.cs), and
this table mirrors it:

| This file | Harness scenario |
|---|---|
| `01-query-results.light.png` | `main-window`, light |
| `02-query-plan.dark.png` | `main-window-plan-tree`, dark |
| `03-command-palette.light.png` | `main-window-palette`, light |
| `04-completion.dark.png` | `main-window-completion`, dark |
| `05-server-activity.light.png` | `activity-window`, light |
| `06-database-overview.dark.png` | `database-overview-window`, dark |

Half are the light theme and half the dark, alternating, so the listing shows both without
one dominating. The Store asks for desktop screenshots of 1366x768 or larger; a shot that
renders smaller is padded to that on a backdrop sampled from its own chrome.

A change to a screen shown here re-renders the set in the same PR (CLAUDE.md, UI design
rule 9):

```bash
scripts/screenshots/update-published.sh
```

Uploading them to Partner Center is a manual step; nothing in the release workflow does it.
