# SQL editor

![The main window: schema tree, SQL editor and results grid](../screenshots/main-light.png)

## Running SQL

| What you want | How |
| --- | --- |
| Run the whole tab | <kbd>Ctrl</kbd>+<kbd>Enter</kbd> or <kbd>F5</kbd> |
| Run just the statement the cursor is in | <kbd>Shift</kbd>+<kbd>Enter</kbd> |
| Run just the selection | Select it, then <kbd>Ctrl</kbd>+<kbd>Enter</kbd> |
| Stop a running query | <kbd>Esc</kbd> |

<kbd>Esc</kbd> genuinely stops the query mid-flight. It cancels on the server
rather than only detaching the UI.

Results start rendering before the full set has arrived. Rows stream back in
batches, so the first screenful of a large `SELECT` appears immediately.

### Scripts

Several `;`-separated statements run in order on one connection, so session state
carries across them. `BEGIN … COMMIT`, `SET`, and temporary tables all behave as
they would in `psql`. Each statement gets its own result section and timing, and
the script stops at the first error.

## Schema tree

The sidebar reads pg_catalog directly, so it shows what Postgres actually has:
tables, views, materialized views and partitioned tables, with primary keys and
column types. Double-click a table to browse it. Drag any node into the editor to
drop in its quoted name.

Right-click gives each kind of object the few actions that make sense for it. A
schema offers:

| Item | What it does |
| --- | --- |
| New table... | Opens a `CREATE TABLE` starter statement for that schema in a new tab |
| Copy name | Puts the schema name on the clipboard |
| Refresh | Reloads just that schema's contents |
| Exclude from autocomplete | See [below](#leaving-a-schema-out) |
| Drop schema... | Drops it, after a confirmation. Fails if the schema still holds objects |
| Drop schema (cascade)... | Drops it together with everything inside, after a confirmation that says so |

A table offers its reconstructed DDL and the Alter Table dialog; a function
offers its source; an extension offers install or drop.

## Completion

Completion triggers as you type, or on demand with
<kbd>Ctrl</kbd>+<kbd>Space</kbd> (literal <kbd>Ctrl</kbd> on every platform,
because <kbd>Cmd</kbd>+<kbd>Space</kbd> is Spotlight on macOS).

![Typing FROM and a partial table name, then JOIN with an FK-ranked suggestion, then ON auto-completing the whole join condition](../screenshots/completion-demo.gif)

It reads the live catalog, so it knows about:

- schema-qualified tables after `FROM` and `JOIN`
- columns scoped to the tables actually in the statement, in `WHERE`, `ON` and
  `ORDER BY`
- `alias.` member access
- CTE output columns, including a CTE whose body is `SELECT *`, resolved through
  the catalog
- user-defined functions, with signature tooltips
- the `jsonb` function family

### JOIN magic

Two touches that save the most typing. After `JOIN`, tables connected to what you
already have by a foreign key rank first. After `ON`, the complete join
condition, `oi.order_id = o.id`, is the top suggestion, one keystroke away.

### Leaving a schema out

A database with dozens of schemas usually has a few that belong to another team,
and their tables only ever get in the way of yours. Right-click the schema in the
sidebar and pick **Exclude from autocomplete**. Its tables, columns and functions
stop appearing in every suggestion list, and the refresh gets a little faster too,
because those catalog queries are skipped.

The schema stays in the tree, dimmed, with a crossed-out eye next to its name.
Right-click it again to bring it back. The choice is remembered per connection,
so excluding `billing` on one database says nothing about a `billing` schema on
another.

### Auto-alias

With auto-alias on, completing a table also gives it a short alias
(`orders` becomes `orders o`) so the columns you complete next are already
scoped. Toggle it with <kbd>Ctrl</kbd>+<kbd>Shift</kbd>+<kbd>A</kbd>; the status
bar confirms the new state.

## Formatting

<kbd>Ctrl</kbd>+<kbd>Shift</kbd>+<kbd>F</kbd> (or
<kbd>Alt</kbd>+<kbd>Shift</kbd>+<kbd>F</kbd>, if that is the muscle memory you
brought) pretty-prints the statement under the cursor, following
[sqlstyle.guide](https://www.sqlstyle.guide/)'s "river" layout: root keywords
right-aligned to a common column, content to their right.

The formatter re-tokenizes its own output and compares it to the input, so a
format can only ever change whitespace. If the check fails, it leaves your SQL
alone.

## Editing

Beyond the usual, the editor carries the line operations you expect from a code
editor:

| Action | Shortcut |
| --- | --- |
| Toggle line comment | <kbd>Ctrl</kbd>+<kbd>/</kbd> |
| Duplicate line or selection | <kbd>Ctrl</kbd>+<kbd>Shift</kbd>+<kbd>D</kbd> |
| Move line up / down | <kbd>Alt</kbd>+<kbd>↑</kbd> / <kbd>Alt</kbd>+<kbd>↓</kbd> |
| Delete whole line | <kbd>Ctrl</kbd>+<kbd>D</kbd> |
| Find / find & replace | <kbd>Ctrl</kbd>+<kbd>F</kbd> / <kbd>Ctrl</kbd>+<kbd>H</kbd> |
| Zoom font in / out / reset | <kbd>Ctrl</kbd>+<kbd>+</kbd> / <kbd>Ctrl</kbd>+<kbd>−</kbd> / <kbd>Ctrl</kbd>+<kbd>0</kbd> |

Commenting follows the convention you are used to. If every non-blank selected
line is already commented it uncomments them, otherwise it comments the block at
its shared indentation, so the SQL keeps its shape.

`SELECT *` expansion (<kbd>Ctrl</kbd>+<kbd>Shift</kbd>+<kbd>8</kbd>) replaces the
star in the statement under the cursor with the explicit column list. It resolves
both catalog tables and CTEs, and does nothing at all if a table is unknown,
because a missing expansion beats a wrong one.

## Tabs and files

Tabs drag to reorder, browser-style, and the order is restored with your
workspace next session.

| Action | Shortcut |
| --- | --- |
| New tab / close tab | <kbd>Ctrl</kbd>+<kbd>T</kbd> / <kbd>Ctrl</kbd>+<kbd>W</kbd> |
| Next / previous tab | <kbd>Ctrl</kbd>+<kbd>PgDn</kbd> / <kbd>Ctrl</kbd>+<kbd>PgUp</kbd> |
| Jump to tab 1…9 | <kbd>Ctrl</kbd>+<kbd>1</kbd> … <kbd>Ctrl</kbd>+<kbd>9</kbd> |
| Open / save a `.sql` file | <kbd>Ctrl</kbd>+<kbd>O</kbd> / <kbd>Ctrl</kbd>+<kbd>S</kbd> |
| Save as | <kbd>Ctrl</kbd>+<kbd>Shift</kbd>+<kbd>S</kbd> |

The dirty marker distinguishes unsaved scratch from diverges-from-the-file-on-disk,
so a scratch tab does not nag you to save it.

Nothing you open replaces what you are working on. A saved query, a history
entry, generated DDL, a table preview: all of them open in a new tab. That is a
project-wide rule rather than a per-feature choice.

### Workspace restore

Closing the app never prompts. The next session reopens your tabs exactly as you
left them, including never-saved scratch SQL.

### Saved queries and history

The sidebar's Queries tab holds saved queries and the query history. History is
searchable, pinnable, and scoped per connection. Double-click any entry to open
it in a new tab.

## Command palette

<kbd>Ctrl</kbd>+<kbd>K</kbd> (or <kbd>Ctrl</kbd>+<kbd>P</kbd>, or the "Search"
pill in the command bar) fuzzy-jumps to any table, saved query, recent file, or
action.

![The command palette listing actions with their keyboard shortcuts](../screenshots/command-palette.png)

Most features live here rather than on the toolbar. That is deliberate, and it is
why the toolbar stays as short as it is. Each action row shows its shortcut, so
the palette teaches you the keys for the things you do often.

## Next

[Work with results](results.md)
