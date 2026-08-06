# nimbusUi — Claude working notes

The shared Avalonia layer under [pgNimbus](https://github.com/Shman4ik/pgNimbus)
and [kubeNimbus](https://github.com/Shman4ik/kubeNimbus). Read `DESIGN.md` first —
it is the contract; this file is only how to work on the code that implements it.

## The membership test

A thing belongs here if it can be described without naming Postgres or
Kubernetes. Tokens, primitive style classes, window chrome, hotkey resolution:
yes. `SidebarGrouping`, the SQL editor, `ClusterTabViewModel`, the results grid:
no, even when the two apps end up with similar-looking code.

When in doubt, leave it in the app. A wrong thing pulled up here has to be
un-shared later against two consumers; a wrong thing left down there is one
future move.

## Hard rules

1. **No app may be named, referenced, or special-cased.** No `if (app == …)`,
   no Postgres/Kubernetes vocabulary in a public name. A style that only one
   app can use is a style that belongs in that app.
2. **Everything stays NativeAOT- and trim-safe.** Both consumers ship
   NativeAOT; `IsAotCompatible` is on so a trim-unsafe addition fails here
   rather than in someone's `dotnet publish`. No reflection, no
   reflection-based bindings, no `Activator.CreateInstance`.
3. **The assembly name `Nimbus.Ui` is load-bearing.** It is the `avares://`
   authority both apps reference (`avares://Nimbus.Ui/Theme/Theme.axaml`).
   Renaming it compiles fine everywhere and dies at XAML load.
4. **Avalonia's `PackageReference` version is pinned to the lowest version
   either app is on**, not the newest available. NuGet unifies upward in the
   consumer, so a low pin costs nothing; a high pin breaks the app that hasn't
   upgraded yet. Today: 12.1.0 (pgNimbus), while kubeNimbus is on 12.1.1.
5. **A change here is not done until both apps have been built against it.**
   There is no test suite that can catch a broken style — the apps' screenshot
   harnesses are the only check, and they live in the apps. Building is not
   enough on its own: a XAML file that fails to *load* compiles perfectly, so
   render the scenarios too. And an *incremental* build is not enough either:
   adding a control here and referencing it from an app's XAML before the file
   reached that app's copy of the subtree built clean, because nothing had
   invalidated the XAML compiler's output. `dotnet build -t:Rebuild` is what
   surfaced `AVLN2000: Unable to resolve type OverlayPanel`.
6. **A style both apps need but only one has is a bug, not a gap.** The first
   extraction pulled up the shell vocabulary and left the whole Fluent control
   layer in pgNimbus, so kubeNimbus drew stock inputs, lists and grids next to
   pgNimbus's toned ones. Nobody noticed from inside either app — you only see
   it with the two windows side by side, which is exactly the comparison this
   library exists to survive. When adding a style, ask what the *other* app
   renders for the same control today.

## Working on this from inside an app repo

The usual case: you are in pgNimbus or kubeNimbus, the change turns out to be
shared, and the files are right there under `shared/nimbusUi`.

Edit them in place, build the app, and then push the subtree back up
(`git subtree push --prefix shared/nimbusUi …`) as its own step — the app's own
commit and the shared commit are separate histories, and forgetting the second
half is how the copies started drifting in the first place.

Then pull it into the sibling app and build that one too. Both working copies
are normally checked out side by side (`X:\source\pgNimbus`, `X:\source\kubeNimbus`),
so this is one session's work, not a follow-up ticket.

## Layout

```
src/Nimbus.Ui/
  Theme/Tokens.axaml      Colour, radii, scrollbars, Fluent resource overrides.
  Theme/Icons.axaml       MDI geometries used by both apps (Apache-2.0).
  Theme/Controls.axaml    Fluent control retheming: inputs, lists, trees, grids,
                          the .soft/.danger button families, TabControl.
  Theme/Overlay.axaml     The ControlTheme for OverlayPanel (DESIGN.md rule 13).
  Theme/Theme.axaml       The include point. Merges the dictionaries, pulls in
                          Controls.axaml, and holds the shell vocabulary itself
                          (card, layer, chip, searchpill, toolbar, statusBar, …).
  Controls/               The library's own controls. OverlayPanel is the first —
                          a dismissable panel over the shell, which is what both
                          apps use instead of a secondary Window.
  Chrome/                 One-bar window chrome + drawn caption buttons.
  Hotkeys.cs              Ctrl/Cmd resolution, gesture labels.
```

A control here is a `TemplatedControl` with its theme in `Theme/`, never a
`UserControl` with markup baked in: the apps have to be able to restyle it, and a
`ControlTheme` keyed on the type is the only thing an app can override. The
`PART_` names in a template are load-bearing — code finds them by name in
`OnApplyTemplate`, so a rename is a dead gesture rather than a build error.
