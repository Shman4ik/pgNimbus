# Results grid

## Browsing a table without writing SQL

Double-click a table in the schema tree to browse it. Paging and click-to-sort
headers are pushed down to Postgres as `ORDER BY` / `LIMIT` / `OFFSET`, so a
hundred-million-row table costs the same as any other single page.

The SQL that produces the view sits in the editor, and doubles as the filter: add
a `WHERE` clause and run it.

## Column widths

Columns size themselves to their content, up to a limit that keeps one long
value from pushing every other column off screen. Drag the divider between two
headers to set a width yourself, and that column stops sizing itself and keeps
the width you gave it. Dragging is not bound by the limit, so a column holding
long JSON can be pulled as wide as you need.

Widths you set are remembered per tab, by column name, so re-running the query,
turning a page, or switching to another tab and back keeps them.

## Editing cells

| Action | How |
| --- | --- |
| Edit the selected cell | <kbd>F2</kbd>, or double-click it |
| Commit / cancel the edit | <kbd>Enter</kbd> / <kbd>Esc</kbd> |
| Inspect the full value | <kbd>Space</kbd>, or double-click a read-only cell |
| Set a cell to `NULL` | Context menu |
| Delete the selected row | <kbd>Delete</kbd> |
| Copy the selected cells | <kbd>Ctrl</kbd>+<kbd>C</kbd> |

Results are editable when the row can be identified unambiguously. That covers
browsed tables with a primary key, and also hand-written `SELECT`s, whenever the
wire metadata proves it is safe to map a column back to its table.

Add a row opens a dialog with the same type-aware editors as the grid.

### Type-aware editors

Postgres types get the right control rather than a text box:

- `enum` columns get a dropdown of their actual `pg_enum` labels
- `boolean` gets a checkbox
- `date` and `timestamp` get a calendar picker
- arrays and composites are syntax-checked before anything is sent
- domains resolve to their base type

Types that Postgres will not assign from plain text, such as `inet`, `cidr`,
`macaddr`, ranges, geometric types, bit strings, `xml` and `tsvector`, are sent
through an explicit server-side `CAST` to the column's declared type. Postgres
itself then validates them and gives you a precise error, rather than pgNimbus
guessing at a client-side conversion.

### JSON is a first-class type

`json` and `jsonb` cells get more than a text box. Double-click one and the cell
inspector opens straight on its Edit tab, with:

- pretty-printing and minifying
- JSON syntax highlighting
- client-side validation before anything is sent
- a collapsible read-only tree view of the value

Validation is driven by the column's type rather than by what the value looks
like, so a plain `text` column holding something JSON-shaped still accepts any
string.

## Safe mode

![Editing cells across two tabs in safe mode, then committing both staged changes together as one transaction](../screenshots/safe-mode-commit-demo.gif)

Safe mode is for the "inline edit on production" nerves. With it on, grid edits,
inserts and deletes are staged locally instead of being sent:

- dirty rows are highlighted, amber for edited and red for pending delete
- "Review & commit…" shows the exact SQL that will be sent
- everything applies as one transaction, or gets discarded with nothing ever
  having reached the server

Toggle it from the command palette. It has no keyboard shortcut on purpose:
flipping it by accident changes whether your edits hit the database immediately.

## Following foreign keys

Right-click a cell in the grid. On a foreign key column, you can jump to the row
it references. On a key column, you can list every row that references it.

Each hop opens a new, pre-filtered browse tab, so you can walk a relationship
graph and still have every step you came through.

## Cell inspector

<kbd>Space</kbd> quick-peeks the current cell in an overlay: the full value, no
truncation, with JSON pretty-printed and one-click copy. On an editable cell it
also has an Edit tab, which is the comfortable way to edit anything longer than a
line.

## Transactions

An explicit transaction runs on one held connection, with a status-bar indicator
so you always know you are inside one.

| Action | Shortcut |
| --- | --- |
| Begin | <kbd>Ctrl</kbd>+<kbd>Shift</kbd>+<kbd>B</kbd> |
| Commit | <kbd>Ctrl</kbd>+<kbd>Shift</kbd>+<kbd>Enter</kbd> |
| Rollback | <kbd>Ctrl</kbd>+<kbd>Shift</kbd>+<kbd>Backspace</kbd> |

If a statement inside the transaction fails, pgNimbus rolls the block back for
you, so you are never stranded in Postgres's aborted-transaction state where
every subsequent statement errors out.

## Import and export

Import CSV and JSON files, streamed into the table via `COPY`, with type
inference on the incoming columns.

Export results, or copy them straight to the clipboard, as TSV, CSV, JSON, a
Markdown table, or `INSERT` statements.

## Next

[Read a query plan](explain.md)
