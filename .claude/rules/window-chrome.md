---
description: "Platform window chrome: merged title bar, macOS traffic lights, menus, shutdown, connection-dialog chrome, results-grid resize and scroll."
paths:
  - "**/Chrome/**"
  - "**/MainWindow*"
  - "**/ConnectionDialog*"
  - "PgNimbus.App/App.axaml*"
  - "PgNimbus.App/Platform/**"
  - "**/MacMenus*"
  - "**/MacAppMenu*"
  - "**/ResultsGridPanel*"
  - "**/DialogChrome*"
  - "**/ThemedWindowChrome*"
---

<!-- Moved out of the root CLAUDE.md so it loads only when working on these paths. Same rule applies: keep it current in the same PR. -->

## Platform window chrome

- **The command bar IS the title bar, on Windows and macOS.**
  `MainWindow.SetUpTitleBar()` calls `NimbusWindowChrome.Attach` (shared with
  kubeNimbus — `shared/nimbusUi/Chrome/`, and DESIGN.md rule 9 states the four
  platform traps, three of which fail silently). Linux keeps its system
  decorations deliberately.

  This replaced a **macOS-only** version that hand-rolled the drag from
  `BeginMoveDrag` plus a `ClickCount == 2` zoom: that reproduced two of the four
  gestures a title bar owes the user (drag, double-click-maximize) and lost the
  right-click window menu and Win11 Snap Layouts, all four of which now come from
  the OS via `WindowDecorationProperties.ElementRole="TitleBar"` on the bar. It
  also returned early on Windows, so Windows carried two bars until 2026-08.
  On Windows the caption buttons are now **ours to draw** — Avalonia 12's Win32
  backend disables the system ones under an extended client area — from the
  `CommandBarWindowDecorations` theme in `shared/nimbusUi/Chrome/Decorations.axaml`.

  The "pgNimbus" wordmark is gone on every platform (rule 9), not just macOS. The
  ☰ button is still hidden on macOS only, and that is not chrome: the native menu
  bar (`BuildMacNativeMenu`) is the file-command home there, so it would be a
  second copy of the same commands. The sidebar toggle icon is platform-picked
  via `{OnPlatform}` (SF-style geometry on macOS).

  **The traffic lights are centred on the 40px bar** (2026-09-28). AppKit placed
  them for its own ~28pt title bar, about 5pt above the centre line every other
  control in the bar sits on. `NimbusWindowChrome.Attach` now also calls the
  shared `MacTrafficLights`, which moves the three buttons through the Objective-C
  runtime and re-applies after resizes, state changes and activation (DESIGN.md
  rule 9 has why Avalonia 12 offers no way to ask for it). Headless tests cannot
  see this; it is checked on a Mac.

  **The connection dialog has the same one bar** (2026-09). It is the app's first
  screen and a resizable, maximizable window like the main one, yet it arrived
  under an OS caption reading "pgNimbus — Connect" beside the app icon, so the
  first thing anyone saw was the one window that did not look like the app.
  `ConnectionDialog` attaches `NimbusWindowChrome` to its own 40px `ConnectBar`,
  which carries only the "Saved connections" heading over the list. The
  connection-string box stays at the top of the form: it was tried in the bar,
  where the main window keeps its search pill, and the owner rejected it on
  sight. The window `Title` stays set for the taskbar and Alt+Tab. The modal
  dialogs keep their OS captions on Windows and Linux; they are not places (on
  macOS `DialogChrome` hides the caption text, UI rule 6).
  **The form stops widening at 720px** (2026-09, macOS audit): maximized, every
  field used to run across the screen, the port box ~1500px from its host. The
  second column of `FormLayout` is `1000*` with `MaxWidth="720"` and a plain `*`
  column after it takes the rest, so the form stays left-aligned under the
  "Saved connections" heading instead of being centred away from it, and in a
  narrow window the near-zero third column leaves it all the width (a `Stretch`
  element with a `MaxWidth` would have *centred*, and a `Left` one shrinks to its
  content). Every row spans the first two columns, so Connect stays under the
  fields' right edge. **New is a compact + under the list** (`NewConnectionButton`,
  tooltip "New Connection") rather than a 240px bar; there is deliberately no −
  beside it, Delete stays on the right-click menu (UI rule 1). **The switches sit
  right of their labels**, a `*,Auto` grid as on the Settings page, rather than
  leading them like checkboxes. Buttons read Test, Connect (DESIGN.md rule 16).
  **The identity sits in the button row instead**: the mark, the name and the
  version, centred between the + and Test, in the one strip of the window that
  is always there and always empty. It replaced the separate "v1.0.0 · Copyright"
  line below the buttons (the full text is its tooltip), so the window got that
  row back, and it opens the About overlay, which from this window was otherwise
  reachable only through macOS's app menu.
  **A big window centres the form instead of stretching it** (2026-09): the list
  and the form are one block capped at 1000 x 760 and centred, and the bar's
  heading follows the block's left edge (`AlignBarHeading`). Maximized on a Mac it
  used to leave the form against the left edge with half the window empty and
  the buttons a screen's height below the fields (`connection-dialog-wide`). A social-card lockup in the space under
  the fields was tried first and moved: that space comes and goes with the SSH
  section and the window's height, so the identity did too. It is a `chip` with a
  local `Opacity="1"` (a chip rests at 0.6). The mark is vector:
  `Styles/LogoMark.axaml`, generated from `design/logo.svg` (see
  `.claude/rules/logo-assets.md` for the chain).
- **The connected window opens in the display mode the connect form was left
  in (2026-08).** `App.CarryWindowState`, called from the dialog's `Connected`
  handler before `Show()`. Connecting reads as one continuous act — the form is
  the app's first screen, not a separate program — so a full-screen (macOS
  green button) or maximized dialog handing off to a small window on the
  desktop behind it reads as the app losing the user's place. It only ever
  *promotes*: a normal-state dialog leaves the window on its own restored
  placement (`WindowPlacementPersistence`, which may itself be maximized).
  macOS enters full screen through an animated Space transition that a window
  which has not been shown yet can drop, so the state is re-asserted once from
  `Opened`.
- **macOS: closing the last window does not quit the app (2026-08).** Closing a
  window and quitting are two separate actions there, and the app that exits
  when its last window closes is the one Mac users report as a bug. So
  `App.KeepRunningWithNoWindowsOnMac` sets `ShutdownMode.OnExplicitShutdown` on
  macOS only — Windows and Linux keep Avalonia's default `OnLastWindowClose`,
  where a windowless background app would read as "close did nothing" — and
  subscribes to `IActivatableLifetime.Activated` (via
  `Application.TryGetFeature`) for `ActivationKind.Reopen`, the Dock-icon click.
  Reopen raises an existing window if there is one, and otherwise builds a fresh
  connection dialog: the closed `MainWindow`'s `Closed` handler already disposed
  its data source and SSH tunnel, so there is nothing to resurrect. What still
  quits, and why `OnExplicitShutdown` is safe: Cmd+Q and the app menu's Quit
  arrive as a platform shutdown request, which
  `ClassicDesktopStyleApplicationLifetime` routes straight to `DoShutdown`
  without consulting `ShutdownMode`, as does the `Shutdown()` that
  `CrashReporter` and `StartupProbe` call directly.
- **macOS: the app ends its own process, and must (2026-08).** Shipped 0.7.5
  aborted with SIGABRT on every quit, *after* the shutdown had already run
  cleanly (windows closed, workspace and placement saved). AppKit's
  `-[NSApplication terminate:]` asks Avalonia's delegate first — that is the
  whole managed shutdown, answering `NSTerminateNow` — and then calls C's
  `exit()`, which runs libAvaloniaNative's C++ static destructors. One of them
  releases a `ComPtr<IAvnDispatcher>` whose vtable is a managed MicroCom proxy,
  so `__cxa_finalize` reverse-P/Invokes into managed code on a main thread whose
  NativeAOT runtime state is already torn down: a `RhFailFast`, not a catchable
  exception (`ThreadStore::AttachCurrentThread` → "Attempt to execute managed
  code after the .NET runtime thread state has been destroyed";
  AvaloniaUI/Avalonia#12459). No frame in that trace is ours, so the fix is to
  never reach `__cxa_finalize`: `MacShutdown.ExitProcessOnShutdown` hooks the
  lifetime's `Exit` event — raised after every window has closed and only when
  the shutdown really goes through — and calls libc `_exit(2)`, which skips
  atexit handlers and static destructors entirely. Consequences to keep in mind:
  an `Exit` handler registered after that one never runs, nothing `Program.Main`
  would do on the way out runs either, and `_exit` flushes nothing — hence the
  explicit `Console` flush, without which `StartupProbe`'s single line (the
  release smoke gate) can be lost. `Environment.Exit` is not a substitute: it
  runs the very `exit()` teardown this avoids. macOS-only; Windows and Linux
  exit through their own teardown cleanly.
- **Windows** — every remaining window still calls `ThemedWindowChrome.Attach(this)`
  for the **icon** (details in `.claude/rules/logo-assets.md`). Its caption-colour half is
  moot on `MainWindow` and `ConnectionDialog`, whose captions are ours, and still
  applies to the dialogs and the reference windows. kubeNimbus deleted its copy outright once its last two
  secondary windows became overlays; ours stays because the connection dialog and the
  crash reporter exist *before* or *instead of* a main window and can never be one.
- **macOS native menu bar (2026-07)** — two layers. App-level (`App.axaml`,
  needs `Name="pgNimbus"` or Avalonia shows "Avalonia Application"): About
  pgNimbus, pgNimbus on GitHub, and Settings… (Cmd+,). The first and last both
  route to the *active* MainWindow's view model through
  `App.ActiveMainViewModel()`, because both are overlays on a window now rather
  than free-standing boxes. **About falls back to the connection dialog** when
  there is no main window yet (2026-08): `ConnectionDialog` hosts its own
  `AboutView` overlay against `ConnectionDialogViewModel.IsAboutOpen`, because
  the connect form is the app's first screen and often its only one — with the
  ☰ menu absent there and no window for the overlay to land on, the menu item
  used to do nothing exactly where a Mac user is most likely to reach for it.
  One consequence to keep: `ConnectAsync` returns early while that overlay is
  open, since the profiles list binds Enter to Connect and the Connect button is
  the window's `IsDefault`, so Escape-the-overlay's sibling gesture would
  otherwise connect instead of dismissing. Settings… has no such fallback — the
  preferences page hangs off a connected window's view model — so it is
  **disabled** while no main window is open (2026-09; it used to sit there
  enabled and do nothing): `App.TrackAppMenuState` re-reads it on every window
  open/close (posted, so a closing window has left the lifetime's list first)
  and when the menu opens, through `MacAppMenu.UpdateSettingsItem`.
  **Avalonia's own Services / Hide / Hide Others / Show All / Quit block is
  corrected in place** (`MacAppMenu.FixStandardItems`, same method): Avalonia
  12.1 binds Hide Others to ⌥⌘Q, one key from Quit, and labels Quit without the
  app's name. Replacing the block (`MacOSPlatformOptions.DisableDefaultApplicationMenuItems`)
  is not an option because the hide/show commands and the Services-submenu flag
  are internal to Avalonia.Native; its items are ordinary `NativeMenuItem`s added
  to our app menu during `AfterSetup`, before `OnFrameworkInitializationCompleted`,
  and the exporter watches their Header and Gesture. Window-level:
  `MainWindow.BuildMacNativeMenu()` installs `CreateNativeMenuBar()` — File /
  Edit / Query / View / Window — via `NativeMenu.SetMenu`, rebuilt from
  `BuildKeyBindings` so gestures track the live Ctrl/Cmd scheme; the builder
  runs on every platform so `MenuTests` can read the menus. The shared pieces are
  `Views/MacMenus` (Edit, Window, Appearance), and `ConnectionDialog` builds its
  own File (Close Window ⌘W) / Edit / Window bar from them — it had no menu at
  all, so the bar showed the app menu alone and Cmd+W did nothing there.
  **The Edit menu routes to focus, and must** (2026-09, DESIGN.md rule 19):
  AppKit matches a menu item's key equivalent before the key reaches the
  window, so once Edit carries Cmd+C/V/X/Z/A/F, those presses arrive as menu
  clicks. `EditCommands.Execute` walks up from the focused element: an
  `IEditCommandTarget` answers first (`ResultsGridPanel`: Copy is the grid's TSV
  copy, Select All selects every row, Find in a browsed grid opens a filter —
  the same three things its key handler does; `MainWindow`: Find opens the SQL
  editor's search, as the Find chord does from anywhere), then a `TextBox` (the
  palette box, every field, a grid cell being edited), then an AvaloniaEdit
  `TextEditor` (the SQL editor, the cell inspector's JSON editor). The gestures
  come from `EditCommands.GestureFor` (Undo/Copy/Find from the catalog, the rest
  the standard text keys on the live modifier). The dialog's Edit has no Find.
  **The Window menu** is Minimize / Zoom, then (main window only) Show Previous
  Tab / Show Next Tab on the catalog's `PreviousTab`/`NextTab` chords, then Bring
  All to Front and a list of the open windows, checked on the active one and
  rebuilt on `NeedsUpdate`: AppKit keeps its own list only for the menu set as
  `NSApp.windowsMenu`, which Avalonia neither sets nor exposes. **View → Appearance**
  is System / Light / Dark radio items through `App.SetTheme`, replacing a "Toggle
  Light/Dark Theme" item that could not return to following the system; the
  checkmark is read on `NeedsUpdate`, never at build time, so building a menu
  bar never reads the settings file. File uses the Mac names: Open…, Save to
  Saved Queries…, Save to File…, New Connection Window…. Landmines, all learned the hard way: (a) menu
  items use `Click` + a CanExecute check, **not** `NativeMenuItem.Command` —
  the exporter snapshots enabled-state from `CanExecute` at assignment time
  (before the DataContext exists), and a wrapper that never raises
  `CanExecuteChanged` leaves every item permanently grayed out; (b) there is
  deliberately **no Help menu** — AppKit force-inserts a search field into
  any menu named "Help" (searching a help book the app doesn't have), so
  Keyboard Shortcuts lives in View and the GitHub link in the app menu;
  (c) don't add an "Enter Full Screen" item — AppKit appends its own to the
  menu titled "View"; (d) the File → Open Recent submenu rebuilds on the
  menu's `NeedsUpdate`, same contract as the ☰ menu's, and View's
  Show/Hide Sidebar header re-resolves the same way; (e) a `NativeMenuItem` in
  `App.axaml` can't take `x:Name` (AVLN2000), so the Settings item is found by
  its header; (f) AppKit appends Emoji & Symbols and Dictation to the menu titled
  "Edit" by itself — expected, not a bug.
- **Results-grid columns resize by dragging, and the drag lifts the auto-width
  cap.** Every generated column is `Width=Auto` with `MaxWidth=AutoWidthCap`
  (560), so one long value can't blow a column past the viewport — but
  `DataGridColumnHeader` clamps *every* step of a resize drag to the column's
  `ActualMaxWidth` too, so with the cap left on, widening a capped column stops
  dead with nothing on screen saying why. `ResultsGridPanel` therefore lifts the
  cap for the column a press is about to resize. Three things make that work:
  the handler is **tunneled** (the header marks a resize press handled before it
  bubbles, so `DataGridColumn.HeaderPointerPressed` — an ordinary bubbling
  subscription — never fires for the very presses that matter); it repeats the
  grid's own 5px-edge test to decide *which* column the press resizes (the right
  grip resizes this column, the left one its neighbour); and it pins
  `Width = ActualWidth` **before** raising `MaxWidth`, because
  `DataGrid.OnColumnMaxWidthChanged` re-expands a column sitting exactly at its
  cap to the full width its content wants — without the pin the column jumps on
  mouse-down, before the drag. The header→column mapping rides on the header
  content's `Tag` (`DataGridColumnHeader.OwningColumn` is internal). Dragged
  widths are handed back to the tab in `QueryViewModel.ColumnWidths`, keyed by
  column name, because the grid is window-central and rebuilds its columns from
  scratch on every re-run, page turn, `EditContext` arrival and tab switch;
  only dragged columns are saved, so an untouched column keeps growing with the
  values that scroll into view.
- **Results-grid scrolling is the DataGrid's own.** Avalonia 12's DataGrid
  handles both wheel axes natively (`UpdateScroll`). Don't reintroduce a
  tunneled wheel handler that writes `ScrollBar.Value` directly — the
  DataGrid only reacts to user `Scroll` events, so that moves the bar
  without the content (the 2026-07 macOS "scrollbar moves, results don't"
  bug, since removed).
