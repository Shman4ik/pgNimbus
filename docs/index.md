# pgNimbus documentation

pgNimbus is a fast, open-source PostgreSQL GUI client with a modern, native UI.
It launches in about 100 ms, streams results before your query finishes, and
sends no telemetry.

These pages cover how to use it. For why it exists, the benchmark numbers, and
the roadmap, see the [README on GitHub](https://github.com/Shman4ik/pgNimbus).

<div class="grid cards" markdown>

- **[Installation](getting-started/installation.md)**

    Microsoft Store, WinGet, MSI, `.dmg`, AppImage, `.deb`, `.tar.gz`, and how to
    verify a download you didn't get from the Store.

- **[Connecting to a database](getting-started/connecting.md)**

    Paste any connection string, save profiles, colour-code production, tunnel
    over SSH, and open several servers side by side.

- **[SQL editor](guide/editor.md)**

    Schema-aware completion, FK-aware `JOIN` suggestions, formatting, scripts,
    tabs, and file handling.

- **[Results grid](guide/results.md)**

    Browsing, inline editing, safe mode, following foreign keys, the cell
    inspector, and import/export.

- **[Query plans](guide/explain.md)**

    `EXPLAIN` and `EXPLAIN ANALYZE` as a heat-mapped tree with plain-language
    warnings, plus pasting a plan from somewhere else.

- **[Monitoring](guide/monitoring.md)**

    Server activity, the who-blocks-whom lock tree, the database overview, and
    the `LISTEN`/`NOTIFY` monitor.

</div>

## Two things worth learning first

The command palette. <kbd>Ctrl</kbd>+<kbd>K</kbd> (or <kbd>Ctrl</kbd>+<kbd>P</kbd>)
fuzzy-searches every table, saved query, and action in the app. Most features
have no toolbar button on purpose, so the palette is where they live. Each row
shows its keyboard shortcut, which is how you stop needing the palette for the
things you do often.

The cheat sheet. <kbd>F1</kbd> lists every shortcut, grouped by area. The same
list is published here as the
[keyboard shortcut reference](reference/keyboard-shortcuts.md). Both come from
one catalog in the source, so neither can drift from what the app does.

## Conventions in these pages

Shortcuts are written with <kbd>Ctrl</kbd>. On macOS, <kbd>Cmd</kbd> takes its
place automatically, and you can force either scheme in Preferences, under Hotkey
scheme. The one exception is autocomplete, which stays on
<kbd>Ctrl</kbd>+<kbd>Space</kbd> everywhere, because
<kbd>Cmd</kbd>+<kbd>Space</kbd> is Spotlight on macOS.
