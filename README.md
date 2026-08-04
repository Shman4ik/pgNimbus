# nimbusUi

The shared Avalonia design system behind the Nimbus desktop apps —
[pgNimbus](https://github.com/Shman4ik/pgNimbus) (PostgreSQL client) and
[kubeNimbus](https://github.com/Shman4ik/kubeNimbus) (Kubernetes client).

It exists because the design system was originally copied from pgNimbus into
kubeNimbus by hand, and copies drift. Within a few months the same red had two
names and two values (`AppDangerBrush` `#E5484D` vs `ErrorBrush` `IndianRed`),
the window chrome was rebuilt twice with different platform behaviour, and the
UI rules were written down twice under different numbers.

## What is in here

| Path | What |
|---|---|
| `src/Nimbus.Ui/Theme/` | Tokens (colour, radii, scrollbars), the shared MDI icon geometries, and the primitive style classes (`card`, `layer`, `chip`, `toolbar`, `searchpill`, `statusBar`, `infoBar`, …). |
| `src/Nimbus.Ui/Chrome/` | One-bar window chrome: extended client area, the `TitleBar` decoration role, and the drawn caption buttons Windows requires once the client area is extended. |
| `src/Nimbus.Ui/Hotkeys.cs` | Ctrl-vs-Cmd resolution and gesture labelling. No Ctrl gesture is hardcoded in either app. |
| `DESIGN.md` | The UI rules both apps are held to, and the reasoning behind each. Single source — the apps' own `CLAUDE.md` files link here rather than restating them. |

What is **not** in here: anything that names Postgres or Kubernetes. That is the
whole membership test.

## How the apps consume it

Not as a NuGet package — as a `git subtree` under `shared/nimbusUi`, referenced
as an ordinary `ProjectReference`. Sources in the tree keep the NativeAOT
publish (the shipping configuration for both apps) working with no feed, no
version bumps, and no restore step between editing a colour and seeing it.

```bash
# First time, in the consuming repo:
git subtree add   --prefix shared/nimbusUi https://github.com/Shman4ik/nimbusUi main --squash

# Pull changes made in the other app:
git subtree pull  --prefix shared/nimbusUi https://github.com/Shman4ik/nimbusUi main --squash

# Push changes made here back up:
git subtree push  --prefix shared/nimbusUi https://github.com/Shman4ik/nimbusUi main
```

## Licence

MIT, same as both apps.
