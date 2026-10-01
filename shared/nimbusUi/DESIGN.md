# The Nimbus design rules

These hold for **both** [pgNimbus](https://github.com/Shman4ik/pgNimbus) and
[kubeNimbus](https://github.com/Shman4ik/kubeNimbus). Each app's `CLAUDE.md`
carries only rules about its own domain and links here for the rest — the rules
below were previously written down twice, under different numbers, and started
disagreeing.

Every rule states the failure it prevents. A rule with no failure behind it is a
preference, and preferences do not belong in a contract.

---

### 1. Minimalist by default

Every always-visible control must be justified before it is added; the default
answer is no. Secondary and rare actions live in the command palette (Ctrl/Cmd+K)
or a context menu.

A context menu is not a dumping ground either. pgAdmin answers a right-click on a
schema with 15 items plus an 18-item submenu; a Nimbus context menu earns each
entry the same way a toolbar button would.

### 2. Double-click performs the default action

Anywhere a list, tree or grid row has an obvious primary action, double-clicking
must do it — table → browse, pod → logs, saved query → open, context → connect.
Space quick-peeks. Apply this to any new list-like UI without being asked.

### 3. Opening something never overwrites the active editor

Saved queries, history entries, generated DDL, a resource's YAML: all open in a
**new** tab. Losing unsaved work to a single click is not recoverable by undo.

### 4. No hardcoded Ctrl gestures

`Nimbus.Ui.Hotkeys` resolves Ctrl vs Cmd, and palette labels and the cheat sheet
derive from it. This includes gestures built in a loop — Ctrl/Cmd+1…9 for tab
jumps are registered from `Hotkeys.Primary` in code-behind, not as nine XAML
`KeyBinding`s.

The scheme decides the **spelling** as well as the key. On the Ctrl scheme a chord
is words joined by "+" (`Ctrl+Shift+F`). On the Cmd scheme it is what every Mac app
prints: Apple's glyphs, modifiers in the order ⌃ ⌥ ⇧ ⌘, then the key, run together
(`⇧⌘F`, `⌘↩`, `⌥⇧F`, `⎋`, `⇥`, `⌫`, `⌦`, `⇞`/`⇟`; `⌘?` for ⇧⌘/). "Alt", "Cmd",
"Shift", "Enter" never appear as words there. That covers every place a gesture is
written: palette rows, cheat-sheet keycaps, tooltips, the search pill, hints. The
glyphs need a font that carries them at text size (Inter doesn't, and macOS's
fallback draws them at half height), so text spelling a gesture names one
explicitly. And where the Mac has its own convention for a command the other
platforms don't share (⇧⌘] / ⇧⌘[ for the next and previous tab, ⌘. to stop, ⌘? for
help), the Cmd scheme answers it too and names it first; the cross-platform chord
stays a synonym. pgNimbus implements this in its command catalog; kubeNimbus's
`Hotkeys.Label`/`Describe` callers still spell words and are the open half.

### 5. A click target hit-tests across its whole area, and says it is one

In Avalonia a `Panel` or `Border` with a **null** `Background` does not hit-test
where no child covers it, and a container's own `Padding` lies outside its content
template entirely. A pointer handler on an item template's root panel therefore
fires on the text and nowhere else: the row highlights on click but does nothing,
which reads as "is this one click or two, or is it broken?".

Handle taps on the **items control** and resolve the row from the event source, or
give the target an explicit `Background="Transparent"`. Anything clickable also
gets `Cursor="Hand"` and a pressed state — and `:pressed` is a pseudo-class only
button-like controls set, so on a `Border` it must be a real class toggled from the
pointer handlers (`Border.tab.pressed`), never `Border.tab:pressed`, which compiles
and silently never matches.

### 6. A `ToggleButton` gets EITHER a two-way `IsChecked` binding OR a toggling `Command` — never both

`ToggleButton.IsChecked` is registered `defaultBindingMode: TwoWay`, and
`ToggleButton.OnClick()` calls `Toggle()` **before** `Button.OnClick()` invokes the
`Command`. A control wired with both flips the property twice per click and lands
exactly where it started: a guaranteed no-op that compiles, renders, animates its
checked state, and does nothing.

This shipped three times in kubeNimbus alone — including the log **Follow** toggle,
which stopped the stream it was meant to start, so pod logs never streamed at all.
Put the work in the generated `On<Property>Changed` partial. If a command is
genuinely needed (the palette, a screenshot fixture), give it an explicit target
value rather than an inversion, so it cannot race the control's own toggle.

### 7. Every state gets an explicit visual, and no command silently does nothing

Loading, empty, disconnected, conflict, delete-confirm, filter-matched-nothing:
each gets its own visual, never a blank rectangle that looks like a bug. "This
namespace has no pods" and "no pod here is called that" send you looking for
opposite problems, so they are different states.

This includes the shell's own empty state — no kubeconfig, no saved connection —
which must explain what was searched and offer the way forward. Any command that
cannot run is disabled through `CanExecute`, never a silent no-op.

### 8. A form puts its label above the input, and its state in an InfoBar

Both are WinUI's own patterns, and both replace something that had gone wrong by
hand. A label *beside* its input sits in an `Auto` column with no gap of its own,
so it runs into its own text box, and every pane that tries invents a different
hand-tuned spacer column. A bare status dot next to a sentence carries the
information only for someone who already knows the colour code.

Two corollaries settled by the same pass: fields read in the direction the data
goes, and **a control pair where one half is always disabled is one control** —
Start and Stop are the same slot, swapped on state, not a live button beside a dead
one.

### 9. The command bar *is* the title bar, and nothing in the window says its own name

One row of chrome at the top, not two. `Nimbus.Ui.Chrome.NimbusWindowChrome.Attach`
does it; read that file's comments before changing anything here, because four
things are easy to get wrong and three of them fail silently:

- **Roles, not `BeginMoveDrag`.** `WindowDecorationProperties.ElementRole="TitleBar"`
  maps to Win32 `HTCAPTION`, which is what keeps dragging, double-click-to-maximize,
  the right-click window menu and Win11 Snap Layouts. Hand-rolling the drag
  reproduces one of those four and loses three.
- **On Windows the caption buttons become ours, and that is not optional.**
  Avalonia 12's Win32 backend answers an extended client area by *disabling* the
  system buttons. Without a decorations theme the window has no way to close.
- **The caption reserve must be recomputed, not set once.** In full screen there
  are no buttons to reserve for, and a reserve that stayed is a dead 135px (or
  78px on macOS) hole in the bar.
- **Linux keeps its system decorations.** Extending there hands the app the whole
  frame, and CSD that matches GNOME is wrong on KDE and every tiling WM.
- **The macOS traffic lights are ours to centre.** AppKit places them for its own
  ~28pt title bar, about 5pt above the centre of a 40px command bar. Avalonia 12 has
  no option for it (its title-bar height hint only sizes the backdrop, and its native
  side always clears the `NSToolbar` that would make AppKit centre them), so
  `Chrome/MacTrafficLights` moves the three buttons through the Objective-C runtime,
  the way Electron's `trafficLightPosition` does, and again after every resize, state
  change and activation, since AppKit puts them back. Full screen is left alone.

The wordmark goes with it: the window title and the taskbar icon already carry the
identity, and a bar under the title bar printing the title again is a row spent on
nothing.

### 10. Tabs drag-reorder, and the workspace restores them

Multi-tab is the shell in both apps — query tabs, cluster tabs. They reorder by
dragging, and a workspace snapshot restores them on next launch in the same order.

### 11. The fixed brand accent, never the OS accent

`AppAccentBrush` is a fixed blue. The Windows accent can land anywhere on the wheel,
and every selection/hover/primary surface would then have to stay legible against
an unknown hue. It is also what keeps the two apps looking like one family on a
machine whose accent is orange.

Pinning `AppAccentBrush` on our own styles is not enough, because Fluent keeps
painting with the OS accent wherever a template part draws from its own resources:
an `accent` button at rest, a checked box, a toggle that is on. `Theme/Tokens.axaml`
therefore also overrides `SystemAccentColor` and its six light and dark steps, and
`Button.accent` / `Button.danger` pin their states on `PART_ContentPresenter`. Until
2026-09 every primary button was the OS accent on a real machine (grey on the
owner's) while the headless renders, which use Avalonia's default blue, looked right,
so no screenshot caught it. **Check a colour change live on a machine whose accent
is not blue.**

### 12. A `DataGridCell` needs a gutter on both sides

Fluent's cell padding is left-only, which is invisible while every column is
left-aligned and actively *misleading* as soon as one isn't. kubeNimbus's
right-aligned Memory column put its "—" placeholder hard against Age's "5d" and the
pair read as `—5d`, i.e. a negative age; a real value did the same (`48 MiB16d`).

The gutter is not free — nine columns × 10px comes out of a fixed width — so column
`MinWidth`s have to be re-cut with it.

> **Status: kubeNimbus only.** Not yet applied to pgNimbus's results grid, because
> it moves column widths there and that needs its own visual pass. First candidate
> on the cross-port list.

### 13. A panel you open, use and dismiss is an overlay, not a window

`Nimbus.Ui.Controls.OverlayPanel`: dimmed backdrop (`scrim`), centred `overlayCard`, title row
with a ✕, dismissed by the backdrop, the ✕ or Esc. The cheat sheet, the About box and
the preferences page are all one of these in both apps.

A secondary `Window` loses on three counts. It arrives with an OS-painted caption the
app does not control — which is the *entire* reason rule 9's sibling
`ThemedWindowChrome` has to pin the Windows 11 caption colour, or a dialog opened from
a Light app on a Dark desktop gets a black title bar. It is a second Alt+Tab and
taskbar entry for something that is not a second place to be. And it renders in its own
chrome rather than inside the two-tone shell, so it never quite looks like the app that
opened it.

Three things follow:

- **`IsOpen` binds two-way and is the only wiring.** The panel closes itself. Never
  pair it with a closing `Command` — that is rule 6's double-toggle, and it compiles.
- **Escape is handled on the `TopLevel`, bubbling.** Nothing inside a cheat sheet holds
  focus, so a handler on the panel would never see the key; bubbling from the top level
  still lets a focused search box in another overlay refuse it first.
- **Opening takes focus, closing gives it back.** Bubbling only works if focus is not in
  a control that answers Escape first. An overlay opened from a menu (macOS's app-menu
  Settings and its key equivalent) left focus in the code editor under the scrim: Escape
  never reached the top level, and typed text went into the document behind the panel.
  `OverlayPanel` is focusable, remembers the focused element when `IsOpen` turns true,
  takes focus, and restores it on close unless focus has gone elsewhere on purpose.
- **Anything you need to *watch* while it is open stays a window.** An overlay covers
  the shell. That is the line: pgNimbus's server-activity and database-overview windows
  are reference views you read beside your work and are deliberately not converted, and
  the connection dialog and crash reporter cannot be — they exist before, or instead
  of, a main window.

### 14. A table's header is section-header type, and its rules are barely there

Every `DataGrid` in both apps (`Theme/Controls.axaml`): column headers at 11px,
semibold, slightly spaced and dimmed to `BaseMedium` — the same type a `sectionHeader`
uses — with **no background of their own**; horizontal rules at 10% grey.

Both halves came from one comparison. kubeNimbus grew a hand-built list (its
Applications mode) next to its DataGrid-based resource list, and the owner preferred
the hand-built one on sight: its header read as a caption rather than as a row, and
nothing ruled its rows into boxes. The grid's rules were `BaseLow`, which in the dark
theme is the brightest line on the page. And its header row was painted by Fluent in
`AltHigh` — pure black in the dark theme — which nobody had seen because the grid sat
on a `layer`, which was the same black then (rule 15 made it grey). Moved onto a
`card`, it was a black band.

The cell text size stays in each app, beside rule 12's gutter: it moves column widths,
and those are the app's own.

### 15. A raised surface is lighter than what it sits on, and nothing is Fluent's page black

Four surfaces, one direction (`Theme/Tokens.axaml`, all theme-split, so always
`DynamicResource`):

| Surface | Light | Dark | What sits on it |
|---|---|---|---|
| Shell (`SystemControlBackgroundChromeMediumLowBrush`, Mica where the OS has it) | `#F2F2F2` | `#2B2B2B` | the command bar, the sidebar, **every secondary window** |
| `AppLayerBrush` — `layer`, `overlayCard` | white | `#2E2E2E` | a work area, an overlay's card |
| `card` (6% grey) | a shade under its parent | a shade over it | a group inside any of the above |
| `AppPopupBrush` | white | `#3A3A3A` | what floats over a layer: completion, hints, the find bar |

Behind a modal overlay, `scrim` (`AppScrimBrush`) — one dim for all of them.

Fluent's own page brush (`SystemControlPageBackgroundAltHighBrush`) is white in the
light theme and **pure black** in the dark one, and it was the default for all four
jobs: `layer`, every popup, every overlay card and — as `Window`'s default background —
every dialog. So the dark theme had a `#000` work area inside a `#2B2B2B` shell (a
raised surface darker than its base, the opposite of how Windows 11 layers a dark UI),
dialogs that were a grey title bar on a black body, and a completion list that was a
black hole in the editor. Light never showed it: white over `#F2F2F2` already reads as
raised. Two more of the same, found in the same pass: Fluent fills a *focused* text box
with `ChromeBlackHigh` (black again, so the dark tokens pin `TextControlBackgroundFocused`
to the resting well), and it paints a selected `ListBoxItem` with the OS accent at
60–80% on the template part, which is why the list rule now targets
`/template/ ContentPresenter#PART_ContentPresenter` (rule 11).

Never paint a surface with `SystemControlPageBackgroundAltHighBrush` or a hand-set hex;
pick the row of the table it is.

A zebra grid's alternate row (`AppAltRowBrush`, rule 20) is the one tint below a card:
4% black on a light surface, 4% white on a dark one.

### 16. A dialog's button row: primary last, Cancel just before it, on the right

`[secondary…] [Cancel/Close] [Primary]`, right-aligned, 8px apart, 16px below the
content — the macOS order, on every platform. The primary is `accent` (`danger` when
it destroys), everything else `soft`; a secondary that destroys (`soft danger`: Discard
all, Drop column) goes furthest left, away from the primary. A dialog with no primary
ends on Close.

It used to be the Windows order, `[Primary] [secondary…] [Cancel]`, and that is the
one a Mac user noticed first (2026-09): "Save | Cancel" and "Connect | Test" put the
affirmative where every native Mac dialog puts Cancel, so the hand went to the wrong
corner in every dialog. One order everywhere rather than one per platform, because
the rule before this one was already a fix for the same button moving corners between
two dialogs of one app (pgNimbus had drifted to both orders), and a per-OS flip is
that drift again, made deliberate. Windows users meet primary-rightmost in wizards
and the Store's own dialogs; macOS users meet the other order nowhere. Pair it with
`IsDefault` on the primary and `IsCancel` on Cancel, so Enter and Esc never depend on
where either sits.

The same pass made **modal dialogs non-minimizable and non-maximizable** on every
platform, and on macOS hid their caption text (the client area extends under the
title bar, the window keeps its `Title` for VoiceOver and Mission Control), because
the body already opens with `dialogTitle` and AppKit printed the same words above it.

### 17. Secondary text is dimmed to 0.6, not below

A caption, an empty-state hint, a size or a type beside a name, a footer: text that
says something is dimmed with `Opacity="0.6"` (or `BaseMedium`, the same tone) and no
further. At 0.4–0.5 it comes out `#8C8C8C`–`#808080` on white, about 3:1 against the
4.5:1 that small text needs, and pgNimbus had 28 of them — "No matches", "Queries you
run will appear here", the relation sizes in the schema tree. The dark theme hid how
faint they were; the light theme is where they failed. Lower opacities stay for what
is not read: a separator glyph, a decorative icon, a disabled control (which Fluent
dims on purpose), or a state the dimming itself announces (an excluded schema).

Inside a list or tree row, give such text the `TextBlock.secondary` class (0.6) rather
than a local `Opacity`: on a focused list's accent row (rule 20) it has to come up to
0.85, since white at 0.6 over the brand blue is under 2.5:1, and a local value
outranks every style that could raise it.

### 18. Menu items are in Title Case, and a row's menu starts with its default action

Every menu label — the macOS menu bar, the ☰ menu, every context menu and every
`MenuFlyout` — uses Apple's title-style capitalization, on all platforms: capitalize
every word except articles, coordinating conjunctions and prepositions of four
letters or fewer, unless the word comes first or last ("Copy Name", "Close to the
Right", "Exclude from Autocomplete", "Save As…", "Copy As ▸"). A label that opens
something needing more input ends in `…` (the character, not three dots). Identifiers
and SQL keywords keep their own case ("Set Cell to NULL", "CREATE ROLE Script"), and
a label built from data (a column name, a filter expression) is data, not a label.

pgNimbus had both conventions at once: the native menu bar said "Alter Table…" while
the context menu one row below it said "Copy name" and "Drop schema...", so the same
app read as two. Microsoft's guidance leans to sentence case and Apple's HIG requires
title case; the choice (2026-09) was one convention on every platform rather than a
per-OS switch, and the Mac's, since it is the one platform where the other looks wrong.

A row's context menu opens with the action a double-click performs (rule 2), by
name: a table's menu used to offer only its DDL and Alter Table, so the one thing
most people right-click a table for was reachable only by knowing to double-click.

### 19. On macOS the menu bar is the app's, and the Edit menu goes to what has focus

A window that is key on macOS brings a full menu bar: the window's own menus plus
**Edit** (Undo, Redo, Cut, Copy, Paste, Select All, and Find where there is something
to search) and **Window** (Minimize, Zoom, Bring All to Front, the open windows). A
window with no menu of its own leaves AppKit showing the app menu alone, which reads
as a broken app and takes Cmd+W and Cmd+M with it.

The Edit items are not decoration. AppKit matches a menu item's key equivalent
*before* the key reaches the window, so the moment Edit carries Cmd+C, the text box,
editor or grid that used to receive Cmd+C as a key press receives a menu click
instead. Each verb therefore has to be routed to the focused control — walk up from
focus, let a view that means something else by the verb (a grid's copy of its rows)
answer first, then a text box or code editor — or adding the menu breaks copy and
paste everywhere. Don't add a Help menu (AppKit inserts a search field into one) or
an Enter Full Screen item (AppKit adds its own to View).

Avalonia 12.1's standard app-menu block has two defects, fixed in place after
setup: Hide Others is bound to ⌥⌘Q (one key from Quit) instead of ⌥⌘H, and Quit
does not name the app.

### 20. Native density, and one look for focus and selection

A macOS audit of pgNimbus (2026-09-28) found both apps reading a size larger and a
shade heavier than every native app beside them, with Windows-95 focus boxes. Fluent's
defaults were the cause each time; `Theme/Tokens.axaml`, `Theme/Controls.axaml` and
`Theme/ToggleSwitch.axaml` replace them, on every platform:

- **13px body text.** `ControlContentThemeFontSize` is 13, the macOS system size, so
  every window and every control that does not set its own size draws at 13 (Fluent:
  14). List and tree rows are ~24px (`ListBoxItem` padding 8,3; `TreeViewItem`
  `MinHeight` 24; Fluent: 38 and 32). A tree's node names are regular weight;
  semibold at most for a root group row.
- **Selection has two faces.** The selected row of the list or tree that holds
  keyboard focus is the solid accent with white text and icons; the selected row of
  any other list is neutral grey (`AppSelectionInactiveBrush`) with ordinary text.
  So the one list the keyboard is driving is the one that looks it, as in Finder or
  Mail. Two classes adjust it: `ListBox.emphasized` for a list driven from a search
  box beside it that never takes focus itself (a command palette), which is always
  accent; and `ListBox.strip` for choices laid out as a list (a tab strip, a row of
  result sections), where "selected" means "the one showing" and keeps the light
  `AppSelectionBrush` wash. Rows draw no focus ring (below); the accent fill is the
  focus indication.
- **One focus ring.** `Controls/FocusRing` is every adorner layer's default focus
  adorner: a 2px ring of semi-transparent brand accent (`AppFocusRingBrush`), 2px
  outside the control and rounded to its own corner radius. Fluent drew a square 2px
  black (white in dark) box with a 1px inner rule around everything, rows and round
  chips included. The ring opts out of the adorner layer's clipping, which otherwise
  cuts a ring drawn outside the control down to nothing.
- **The switch is Apple's small one**: a 32x18 filled track with no outline and a
  14px white knob, grey off and accent on, in both themes. It is a whole
  `ControlTheme`, because the sizes in Fluent's template are set at Template priority
  and no style can restyle them.
- **Menus**: 13px items in ~24px rows, 6px-rounded menu, the highlighted item a
  4px-rounded accent pill with white text, inset 4px from the edge.
- **Flat icon buttons answer the pointer.** `chip` and `toolbar` pin a hover and a
  press wash on the template part (`AppToolbarHoverBrush` / `AppToolbarPressedBrush`)
  rather than leaving it to Fluent: the command bar's icon buttons read as having
  no hover at all on macOS.
- **Zebra tables, opt-in**: `DataGrid.zebra` draws every row the owner marks `odd`
  in `AppAltRowBrush` and drops the horizontal rules. The DataGrid has no
  alternating-row property or odd/even pseudo-class, and it recycles rows, so the
  owner sets the class from the row's index in `LoadingRow`, and again after a row
  is inserted or removed mid-list. The cell text size and row height stay per app
  (rules 12 and 14).

### 21. A tooltip opens anywhere over what carries it, and a cut line keeps its text in one

A tooltip opens on the element the pointer is over, and Avalonia's hit test finds
an element only where it draws something. A `TextBlock` or a `Panel` with no
`Background` draws nothing of its own, glyphs included, so the pointer over its
text reaches the list row, card or header behind it and the tooltip never opens.
pgNimbus measured it headlessly: none of 4,536 points over its status line reached
the text block, and a walk of every window then found about 50 more dead tooltips
(history rows, connection endpoints, cut query text, grid column headers). It is
rule 5's failure seen from the other side: there the click fell through, here the
hover does.

- **`Controls/ToolTipHitTesting` fixes it once for every element**, so no view
  writes `Background="Transparent"` beside a `ToolTip.Tip`. Each app calls
  `ToolTipHitTesting.Install()` from `Application.Initialize`. Any `Panel`,
  `TextBlock`, `Border`, `ContentPresenter` or templated control that gets a
  tooltip and has no background gets a transparent one as a *current* value: a
  background the markup or a style sets still wins, one that goes back to null is
  filled again, and nothing changes on screen. A disabled control still shows no
  tooltip (Avalonia's choice; `ToolTip.ShowOnDisabled` opts in).
- **A status line is `TextBlock.statusMessage`**: one line even for a message that
  has two (a server error's detail often comes after a newline, which grew the bar
  a line), cut with an ellipsis, and its whole text in a tooltip only while it is
  cut (`Converters/CutTextTip`, which reads the block's text layout). A tooltip
  that repeats what is already on screen is noise, so a line that fits has none. It
  combines with `statusText` and its `dim`/`warn`/`error` variants.

Each app checks the first half in a headless test that walks its windows,
hit-tests the middle of every tooltip-bearing element and fails on any the pointer
passes through.

---

## What is deliberately *not* shared

Sharing the wrong thing costs more than duplicating it. Known exceptions, with
reasons:

| Thing | Why it stays per-app |
|---|---|
| `TabItem` styling | pgNimbus styles only its sidebar's Schemas/Queries switch, under its own `TabControl.sidebar` class (a full-width capsule of equal segments); kubeNimbus styles the bare selector for the compact inspector strip (12,6, `MinHeight` 0). Genuinely different jobs. |
| `TabControl.segmented` | pgNimbus's segmented strip. kubeNimbus does the same job with `ListBox.segmented` + `TabControl.headerless` on purpose — a `TabControl` cannot host a panel's own tools on its header row, and its inspector dock needs exactly that (its rule 10). Sharing a mechanism the sibling has explicitly rejected buys nothing. |
| Domain icons | A Kubernetes cube and a Postgres elephant are not shared vocabulary. `Theme/Icons.axaml` holds only glyphs both apps actually use. |
| Everything in `*.Core` | Both engines are UI-free by their own hard rule and share nothing but coincidence. This is why each app has its own copy of the command catalog and chord types: they are UI-free by design, so they cannot live in a library that references Avalonia. |

## Cross-port list

Improvements that landed in one app and should reach the other. This list is the
mechanism — a rule nobody tracks is a rule that decays.

- [ ] `DataGridCell` gutter → pgNimbus (rule 12).
- [x] Table header type and faint rules → both apps, via `Theme/Controls.axaml` (rule 14).
- [x] Checked `ToggleButton.soft` foreground pinned to the accent → kubeNimbus. It was
      fixed in pgNimbus's copy of `Controls.axaml` and never reached the library.
- [x] `Cursor="Hand"` on `Button.chip` / `Button.searchpill` → pgNimbus, via the
      shared `Theme/Theme.axaml` (rule 5).
- [x] One-bar window chrome on Windows → pgNimbus (rule 9). It previously extended
      the client area on macOS only, with a hand-rolled `BeginMoveDrag`.
- [x] **Surfaces off Fluent's page black → both** (rule 15). `layer`, `OverlayPanel` and
      the list-selection rule changed here; pgNimbus's dialogs, popups and palette and
      kubeNimbus's command palettes moved onto `overlayCard`/`scrim`/`AppPopupBrush`.
- [ ] Secondary text at 0.6 (rule 17) → kubeNimbus: audit its `hint` class and inline opacities,
      and move the ones inside list rows to `TextBlock.secondary` so a focused list's accent
      row can bring them up (rule 20).
- [ ] Title Case menus and default-action-first context menus (rule 18) → kubeNimbus.
- [ ] The macOS Edit and Window menus, their focus routing, and the app-menu fix
      (rule 19) → kubeNimbus. pgNimbus's `EditCommands`, `MacMenus` and `MacAppMenu`
      name no Postgres and are the candidates to lift into this library.
- [ ] Rule 20 → kubeNimbus: its lists pick up the two selection faces from the shared rule;
      check which of them are really strips (`ListBox.strip`) or palette-style lists
      (`ListBox.emphasized`), and whether its resource grids want `DataGrid.zebra`. Its own
      `ListBox.segmented` and switcher styles override the shared row rules and are unaffected.
      Its window chrome gets the centred traffic lights for free through `NimbusWindowChrome`.
- [ ] `AppSuccessBrush` → pgNimbus. The status trio was two-thirds defined there.
- [x] **Tooltips that answer the pointer, and the cut status line → both** (rule 21).
      `ToolTipHitTesting` and `TextBlock.statusMessage` came up from pgNimbus;
      kubeNimbus installs the handler and its status bar uses `statusMessage`.
- [x] **The Fluent control layer → `Theme/Controls.axaml`.** Inputs, lists, trees,
      grids and the `.soft`/`.danger` button families were defined in pgNimbus only,
      so kubeNimbus rendered every `TextBox`, `ComboBox`, `ListBox`, `TreeView` and
      `DataGrid` as stock Fluent beside pgNimbus's toned versions — two apps sharing a
      design system and visibly not looking like it. This was the single biggest cause
      of the family drifting apart, and it was invisible from inside either app.
- [x] **The help-circle glyph → `Theme/Icons.axaml`, and the ☰ menu's tail → both.**
      kubeNimbus drew a real `PathIcon` for the command bar's help button while
      pgNimbus drew a bare `?` text button, which sits on the glyph baseline rather
      than the icons' box and takes the default foreground rather than theirs — the
      one control in that bar that did not look like the rest of it. The geometry
      named neither app, so it was simply in the wrong file. The menu behind ☰ now
      ends the same way in both — Preferences…, Keyboard shortcuts, About — and on
      Windows and Linux that is the *only* route to About: pgNimbus had it wired
      exclusively to the macOS native app menu, so two thirds of its users could not
      reach it at all.
- [x] **The cheat sheet, About and preferences → `OverlayPanel`** (rule 13). kubeNimbus
      already drew its cheat sheet as a hand-written overlay inside `MainWindow.axaml`
      and pgNimbus drew all three as windows, so the two apps disagreed about what a
      panel even *is*. The overlay won and the pattern moved into a real control here,
      rather than being copied into the second app — the one thing guaranteed to make
      them drift again.
- [ ] **`ThemedWindowChrome`'s caption-colour half → shared.** It pins a secondary
      window's Windows 11 caption to the shell tone (without it, a dialog gets a black
      title bar while the app is in Light). Only pgNimbus has a copy now — rule 13 left
      kubeNimbus with no secondary windows at all, so its copy was deleted rather than
      left as dead code. pgNimbus's also carries a native-icon half that is genuinely
      its own; only the DWM colour part should move. Moving it is what makes a future
      kubeNimbus dialog safe, since that failure is invisible on a machine whose OS
      theme matches the app's.
- [ ] **Preferences page shape → keep them converging.** Both apps now use the same
      page: section header, one card per setting, label and explanation left, control
      right, immediate apply, no OK/Cancel. It is duplicated markup rather than a
      shared control today, and that is fine — but a change to one is a change both
      should get.
- [ ] **Load the window icon through `AssetLoader`, not `Icon="/Assets/…"`** — see the
      note under kubeNimbus's release section. The XAML attribute goes through
      `IconTypeConverter`, which cannot resolve a relative asset path under NativeAOT
      and killed kubeNimbus's published binary on *every* RID. pgNimbus already loads
      its icons in code and is unaffected; worth confirming no window there still uses
      the attribute form.
