# Keyboard shortcuts

<!-- Generated from PgNimbus.Core.Commands.CommandCatalog by ShortcutDocs.ToMarkdown(). Do not edit by hand — run the Core tests with PGNIMBUS_UPDATE_DOCS=1 to regenerate. -->

Every shortcut below is also discoverable in the app: press <kbd>F1</kbd> for the
cheat sheet, or <kbd>Ctrl</kbd>+<kbd>K</kbd> to search commands by name.

The **Windows / Linux** column uses Ctrl as the main modifier and spells keys
out. The **macOS** column uses Command and writes keys with Apple's symbols:
⌘ Command, ⌥ Option, ⇧ Shift, ⌃ Control, ↩ Return, ⌫ Delete, ⌦ Forward Delete,
⎋ Escape, ⇥ Tab, ⇞ ⇟ Page Up and Page Down. A few commands also answer the Mac's
own shortcut there, and list it first. Which column applies follows the platform
by default and can be forced either way in Preferences → Hotkey scheme.

## Query

| Action | Windows / Linux | macOS |
| --- | --- | --- |
| Run query | Ctrl+Enter / F5 | ⌘↩ / F5 |
| Run just the statement under the cursor | Shift+Enter | ⇧↩ |
| Cancel query | Esc | ⎋ / ⌘. |
| Explain — estimated plan | Ctrl+E | ⌘E |
| Explain Analyze — runs the query | Ctrl+Shift+E | ⇧⌘E |
| Format the statement under the cursor | Ctrl+Shift+F / Alt+Shift+F | ⇧⌘F / ⌥⇧F |
| Expand SELECT * into columns | Ctrl+Shift+8 | ⇧⌘8 |
| Begin transaction | Ctrl+Shift+B | ⇧⌘B |
| Commit transaction | Ctrl+Shift+Enter | ⇧⌘↩ |
| Rollback transaction | Ctrl+Shift+Backspace | ⇧⌘⌫ |

## Tabs & files

| Action | Windows / Linux | macOS |
| --- | --- | --- |
| New query tab | Ctrl+T | ⌘T |
| Close tab | Ctrl+W | ⌘W |
| Reopen closed tab | Ctrl+Shift+T | ⇧⌘T |
| Next tab | Ctrl+PgDn / Ctrl+Tab | ⇧⌘] / ⌘⇟ / ⌃⇥ |
| Previous tab | Ctrl+PgUp / Ctrl+Shift+Tab | ⇧⌘[ / ⌘⇞ / ⌃⇧⇥ |
| Go to tab 1…9 | Ctrl+1 … Ctrl+9 | ⌘1 … ⌘9 |
| Open file… | Ctrl+O | ⌘O |
| Save — to the tab's file, or to Saved Queries | Ctrl+S | ⌘S |
| Save as — a new file, or a new saved query | Ctrl+Shift+S | ⇧⌘S |

## SQL editor

| Action | Windows / Linux | macOS |
| --- | --- | --- |
| Autocomplete (also triggers while typing) | Ctrl+Space | ⌃Space |
| Show the argument hint (also opens after typing "(") | Ctrl+Shift+Space | ⌃⇧Space |
| Find in editor | Ctrl+F | ⌘F |
| Find & replace in editor | Ctrl+H | ⌘H |
| Next / previous match | F3 / Shift+F3 | F3 / ⇧F3 |
| Toggle line comment | Ctrl+/ | ⌘/ |
| Duplicate line (or selection) | Ctrl+Shift+D | ⇧⌘D |
| Move line up | Alt+↑ | ⌥↑ |
| Move line down | Alt+↓ | ⌥↓ |
| Delete whole line | Ctrl+D | ⌘D |
| Undo / Redo | Ctrl+Z / Ctrl+Y | ⌘Z / ⌘Y |
| Toggle word wrap | Alt+Z | ⌥Z |
| Toggle auto-alias on table completion | Ctrl+Shift+A | ⇧⌘A |
| Zoom font size in / out | Ctrl++ / Ctrl+− / Ctrl+wheel | ⌘+ / ⌘− / ⌘ + wheel |
| Reset font size | Ctrl+0 | ⌘0 |
| Move to word start / end | Ctrl+← / Ctrl+→ | ⌘← / ⌘→ |

## Results grid

| Action | Windows / Linux | macOS |
| --- | --- | --- |
| Edit selected cell | F2 / double-click | F2 / double-click |
| Commit / cancel a cell edit; stage / revert row-detail edits | Enter / Esc | ↩ / ⎋ |
| Inspect cell (full value, pretty-printed JSON) | Space / double-click (read-only) / context menu | Space / double-click (read-only) / context menu |
| Copy the selected cells | Ctrl+C | ⌘C |
| Delete the selected row (editable results) | Delete | ⌦ |
| Show / hide row details | Ctrl+I | ⌘I |
| Filter rows of the browsed table… | Ctrl+F in the results grid while browsing a table | ⌘F in the results grid while browsing a table |
| Set cell to NULL | context menu | context menu |

## Navigation

| Action | Windows / Linux | macOS |
| --- | --- | --- |
| Command palette (jump to table / query / action) | Ctrl+K / Ctrl+P | ⌘K / ⌘P |
| Refresh database & schema | Ctrl+Shift+R | ⇧⌘R |
| Switch focus: editor ↔ results grid | F6 | F6 |
| Collapse / show the sidebar | Ctrl+B | ⌘B |
| Preview a table (in the schema tree) | Double-click | Double-click |
| Server activity | Ctrl+Shift+M | ⇧⌘M |
| Database overview | Ctrl+Shift+G | ⇧⌘G |
| LISTEN / NOTIFY monitor | Ctrl+Shift+L | ⇧⌘L |
| Roles and permissions | Ctrl+Shift+U | ⇧⌘U |
| Switch connection… | Ctrl+Shift+O | ⇧⌘O |
| Open connection in new window… | Ctrl+Shift+N | ⇧⌘N |
| Preferences… | Ctrl+, | ⌘, |
| Keyboard shortcuts | F1 | ⌘? / F1 |

