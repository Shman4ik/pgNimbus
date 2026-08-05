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

---

## What is deliberately *not* shared

Sharing the wrong thing costs more than duplicating it. Known exceptions, with
reasons:

| Thing | Why it stays per-app |
|---|---|
| `TabItem` styling | pgNimbus styles it for the query tab strip (12,9 padding, a margin, a corner radius), kubeNimbus for the compact inspector strip (12,6, `MinHeight` 0). Same selector, genuinely different jobs. |
| `TabControl.segmented` | pgNimbus's segmented strip. kubeNimbus does the same job with `ListBox.segmented` + `TabControl.headerless` on purpose — a `TabControl` cannot host a panel's own tools on its header row, and its inspector dock needs exactly that (its rule 10). Sharing a mechanism the sibling has explicitly rejected buys nothing. |
| Domain icons | A Kubernetes cube and a Postgres elephant are not shared vocabulary. `Theme/Icons.axaml` holds only glyphs both apps actually use. |
| Everything in `*.Core` | Both engines are UI-free by their own hard rule and share nothing but coincidence. This is why each app has its own copy of the command catalog and chord types: they are UI-free by design, so they cannot live in a library that references Avalonia. |

## Cross-port list

Improvements that landed in one app and should reach the other. This list is the
mechanism — a rule nobody tracks is a rule that decays.

- [ ] `DataGridCell` gutter → pgNimbus (rule 12).
- [x] `Cursor="Hand"` on `Button.chip` / `Button.searchpill` → pgNimbus, via the
      shared `Theme/Theme.axaml` (rule 5).
- [x] One-bar window chrome on Windows → pgNimbus (rule 9). It previously extended
      the client area on macOS only, with a hand-rolled `BeginMoveDrag`.
- [ ] `AppSuccessBrush` → pgNimbus. The status trio was two-thirds defined there.
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
- [ ] **`ThemedWindowChrome`'s caption-colour half → shared.** Both apps have a copy
      that pins a secondary window's Windows 11 caption to the shell tone (without it,
      a dialog gets a black title bar while the app is in Light). pgNimbus's copy also
      carries a native-icon half that is genuinely its own; only the DWM colour part
      should move.
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
