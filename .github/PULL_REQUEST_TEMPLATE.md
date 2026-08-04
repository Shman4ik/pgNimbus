<!--
  Thanks for the PR. Keep this short — the goal is to tell a reviewer what
  changed, why, and how much to trust it. Delete sections that don't apply.
-->

## What and why

<!-- What this changes, and the problem it solves. Link the issue if there is one. -->

## Verified

<!--
  Tick what you actually ran, and say what you didn't. "Not verified against a
  live database" is a useful, welcome answer — an unverified claim of
  verification is not.
-->

- [ ] `dotnet build PgNimbus.slnx`
- [ ] `dotnet test --project PgNimbus.Core.Tests/PgNimbus.Core.Tests.csproj`
      — against a live Postgres? <!-- yes / no, tests skip cleanly without one -->
- [ ] NativeAOT publish, no new trim/AOT warnings — RID: <!-- win-x64 (shipping) / linux-x64 -->
- [ ] `dotnet run --project tools/Screenshot -- <dir>` (UI changes)

Anything left unverified:

## Screenshots

<!-- Before/after for any UI change, both themes if the change is visual. -->

## Checklist

- [ ] [CLAUDE.md](../CLAUDE.md) updated if this changes anything it describes
      — it's the contract, not a record of the past
- [ ] **Touches `shared/nimbusUi`?** Then the change is kubeNimbus's too: push
      the subtree up (`git subtree push --prefix shared/nimbusUi …`), open the
      matching kubeNimbus PR, and link it here. A shared change that lands in
      one app only is how the copies drifted in the first place — see
      [DESIGN.md](../shared/nimbusUi/DESIGN.md).
- [ ] Anything general enough for kubeNimbus (a token, a style class, a window
      behaviour) went into `shared/nimbusUi` rather than this app's
      `Styles/Theme.axaml`
- [ ] No new UI state that renders as a blank rectangle (loading / empty /
      disconnected / error each have an explicit visual)
- [ ] `PgNimbus.Core` still has zero UI dependencies
- [ ] No credentials persisted anywhere they weren't already
