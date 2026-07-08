# Pre-launch checklist — making pgNimbus public and promoting it

A one-time working document. Ordered so that each phase gates the next:
don't promote until the repo is public and installable; don't flip public
until the hygiene and security items are done. Check items off in place;
delete the file (or archive it into an issue) once the launch is behind us.

Status snapshot when this list was written (2026-07-07): no git tags or
GitHub Releases exist yet, CI has only the tag-triggered `release.yml` (no
PR build), there are no tests, and the repo has no CONTRIBUTING /
SECURITY / CODE_OF_CONDUCT / issue templates. No secrets were found in the
working tree or the 97-commit history.

---

## Phase 1 — Repo hygiene (before flipping public)

The history becomes permanently public the moment the switch flips —
anything embarrassing or sensitive must be dealt with *before*, not after.

- [x] **MIT LICENSE present** with correct copyright holder.
- [x] **No secrets in the working tree** — grepped for
      password/token/api-key literals; only a code comment matched.
- [x] **No secrets in git history** — no deleted `.env`/key/credential
      files anywhere in the history.
- [ ] **Run GitHub secret scanning once public** (Settings → Code security →
      Secret scanning + push protection) as a backstop to the manual grep.
- [ ] **Accept that personal commit emails are in the history**
      (`shman4ik@gmail.com` appears as author/committer). This is normal
      for open source, but if you'd rather use GitHub's noreply address
      going forward, set `git config user.email` now — rewriting history
      for the old ones is not worth it.
- [x] **Verify the `AvaloniaUI.DiagnosticsSupport` license situation.**
      Verified against the 2.2.3 nupkg: it ships **no license at all** — no
      `<license>`/`<licenseUrl>` in the nuspec, no LICENSE file in the
      package — and its own README shows the `#if DEBUG` pattern. No
      explicit grant means no redistribution right to assume, so the
      reference is now gated: `Condition="'$(Configuration)' == 'Debug'"`
      on the `PackageReference` and `#if DEBUG` around
      `.WithDeveloperTools()` in `Program.cs`. Release/AOT binaries no
      longer link it; MCP inspection still works against Debug builds
      (`dotnet run` is Debug by default). CLAUDE.md updated to match.
      (The `AVALONIA_TOOLS_LICENSE_KEY` itself is only in local MCP
      config, never committed — that part is fine.)
- [x] **Skim `docs/PROGRESS.md` and `CLAUDE.md` one last time** with
      "public reader" glasses. Done 2026-07-08: no secrets, no internal
      hostnames, nothing embarrassing — both read as engineering notes a
      public audience can see. (CLAUDE.md's DevTools section was updated
      for the Debug-only gating above in the same pass.)
- [x] **Add package metadata to the csproj files** — `Authors`,
      `Description`, `Copyright`, `PackageLicenseExpression`,
      `RepositoryUrl`. Cosmetic, but it's what shows in file properties
      of shipped binaries. Added to `PgNimbus.Core` and `PgNimbus.App`
      (the test project isn't packable/shipped).

## Phase 2 — CI and quality gates

Once outside PRs are possible, an untested default branch becomes a
liability. These exist to protect `main`, not to chase coverage numbers.

- [x] **Add a PR/push build workflow** (`.github/workflows/ci.yml`):
      `dotnet build` + `dotnet test` on ubuntu-latest for every PR and
      push to `main`. Builds both Debug (which includes the Debug-only
      DiagnosticsSupport reference) and Release, then runs the tests.
- [x] **Add a first test project** (`PgNimbus.Core.Tests`). Exists since
      this item was written — TUnit on Microsoft.Testing.Platform, currently
      covering `SqlFormatter` and `SqlScriptSplitter`. Still-open
      good-first-test targets: `ConnectionStringParser` (five input
      syntaxes, quoting/escaping edge cases) and `FuzzyMatcher`.
- [ ] **Enable branch protection on `main`** — require the CI check to
      pass, require PRs (no direct pushes). Do this right after the CI
      workflow exists.
- [x] **Turn on Dependabot** (`.github/dependabot.yml`) for NuGet and
      GitHub Actions, weekly, with Avalonia packages grouped into one PR
      (they bump in lockstep).

## Phase 3 — Community scaffolding

What a stranger needs to file a good issue or PR without asking.

- [x] **CONTRIBUTING.md** — how to build (links the README section), the
      two hard architectural rules that gate every PR (`PgNimbus.Core`
      has zero UI dependencies; streaming + cancellation are
      non-negotiable), coding conventions (records, MVVM source
      generators, no sync-over-async), and how UI changes get verified
      (the Xvfb + screenshot loop from CLAUDE.md).
- [x] **SECURITY.md** — points at GitHub private vulnerability reporting.
      **Still manual:** enable the feature itself (Settings → Code
      security → Private vulnerability reporting) once the repo is
      public, or the link 404s.
- [x] **CODE_OF_CONDUCT.md** — stock Contributor Covenant 2.1.
- [x] **Issue templates** (`.github/ISSUE_TEMPLATE/`): a bug-report form
      (OS, install method MSI/dmg/source, pgNimbus + PostgreSQL versions,
      repro steps) and a feature-request form that links the README
      backlog first, plus a config that routes security reports to
      private reporting.
- [ ] **Curate "good first issue" candidates.** Move 5–10 small README
      backlog items into actual GitHub issues with the `good first
      issue` label. An empty issue tracker at launch reads as "not
      really open to contributors". *Note (2026-07-08): the examples
      originally listed here (connection-dialog empty state, theme
      persistence, abbreviated column types, tab-bar extras) have all
      shipped since — pick from what's still open, e.g.
      `ConnectionStringParser`/`FuzzyMatcher` test coverage, the Win32
      min-track-size clamp from PROGRESS.md's open items, or slices of
      the "Later" backlog.*

## Phase 4 — First release

Promotion without a downloadable build wastes the launch spike. The
README already points to Releases — right now that page is empty.

- [ ] **Dry-run the pipeline** via `workflow_dispatch` (builds everything,
      publishes nothing) and smoke-test both artifacts: install the MSI on
      real Windows, open the dmg on an Apple Silicon Mac, connect to a
      real database with each.
- [ ] **Tag `v0.1.0`** (or `v0.9.x` if you want headroom before a 1.0
      story) and let `release.yml` cut the release. Releases are already
      marked pre-release until signing exists — keep that.
- [ ] **Write real release notes** for the first tag — the README
      feature list condensed, plus the unsigned-binary caveats verbatim
      (SmartScreen "More info → Run anyway", Gatekeeper right-click →
      Open). First-run friction surprises are the #1 source of angry
      launch-day comments.
- [ ] **Fix the screenshots' freshness** — confirm the four
      `docs/screenshots/*.png` still match the current UI (theme toggle,
      tab strip, status bar all changed recently). Screenshots are the
      first thing every visitor judges.
- [ ] **Code signing — decide, don't necessarily block.** Authenticode
      (~$100–400/yr) and an Apple Developer account ($99/yr) +
      notarization remove the single biggest first-impression blocker.
      The pipeline already has the slot. If the budget isn't there yet,
      ship unsigned with loud documentation (done in README) and treat
      signing as the top post-launch item — but make the decision
      consciously before promoting, because "is it signed?" will be the
      first question on every thread.
- [ ] **winget first submission** — after the first real release, open
      the manual `winget-pkgs` PR using the generated
      `winget-manifests.zip` asset to register `pgNimbus.pgNimbus`.
      `winget install pgnimbus` is itself a promotable moment.

## Phase 5 — Flip the repo public

- [ ] **Repo description + topics** — description: the one-liner ("A
      fast, open-source PostgreSQL GUI client — .NET + Avalonia, native
      speed, modern UI"); topics: `postgresql`, `postgres`, `gui`,
      `database-client`, `sql-client`, `avalonia`, `dotnet`, `csharp`,
      `windows`, `macos`. This is what GitHub search and topic pages
      index.
- [ ] **Social preview image** (Settings → General → Social preview) —
      the dark-theme main-window screenshot, 1280×640. This is what
      renders when the repo link is pasted into HN/Reddit/X.
- [ ] **Enable Discussions** — gives "how do I…" questions somewhere to
      go that isn't the issue tracker.
- [ ] **Enable private vulnerability reporting** (pairs with SECURITY.md).
- [ ] **Make it public** (Settings → Danger Zone). Immediately verify:
      README renders, screenshots load, Releases page shows v0.1.0,
      LICENSE is detected by GitHub, the About sidebar looks right.

## Phase 6 — Promotion

Sequence matters: seed the quiet channels first, save the big spike (HN)
for when the repo has a release, screenshots, and issue templates —
launch-day traffic doesn't come back for a second look.

- [ ] **Prepare the pitch once, reuse everywhere.** The README's market
      thesis is the pitch: *pgAdmin/DBeaver are heavy, TablePlus is paid
      and closed, Beekeeper is Electron — pgNimbus is native-fast
      (~100 ms cold start, NativeAOT), open source (MIT), and
      PostgreSQL-first.* Lead with a 20–30 s GIF/video of: paste a
      connection string → schema tree → Ctrl+K palette → run query →
      streaming results. Record it once, use it in every post.
- [ ] **awesome-postgres** (and similar curated lists like
      awesome-dotnet, awesome-avalonia) — PR to add pgNimbus. Slow-burn
      but permanent discovery channels.
- [ ] **r/PostgreSQL** — "I built a fast, open-source Postgres GUI
      client" post with the GIF. Read the sub's self-promotion rules
      first; be present in the comments all day.
- [ ] **Hacker News "Show HN"** — title close to: *Show HN: pgNimbus –
      fast, open-source PostgreSQL GUI (.NET NativeAOT, ~100 ms
      start)*. Post on a weekday morning US time. Expected top
      comments to have answers ready for: "why not Electron/why
      Avalonia", "Linux support when" (the core is
      cross-platform-capable; honest answer about what's tested),
      "unsigned binaries", "how is this different from Beekeeper/
      DBeaver", and benchmark-methodology questions about the 100 ms
      claim (`docs/PROGRESS.md` iteration 1 has the receipts — link it).
- [ ] **Lobsters, r/dotnet, r/csharp, r/programming** — staggered over
      the following days, angle adjusted per audience (r/dotnet cares
      about the NativeAOT + Avalonia story more than the Postgres story).
- [ ] **X/Mastodon/Bluesky thread** — the GIF plus 3–4 screenshots;
      tag @AvaloniaUI (they actively retweet apps built on it — free
      reach into exactly the right audience).
- [ ] **Avalonia community showcase** — their docs/site list production
      apps, and their Telegram/Discord has a showcase channel.
- [ ] **Product Hunt** — optional, more consumer-flavored; if used, do
      it after HN so the momentum (stars, testimonials) is visible.

## Phase 7 — Launch week operations

- [ ] **Block time for triage.** The launch spike is 48–72 hours; issues
      and comments answered within hours convert visitors into watchers
      and contributors. Slow first responses kill the momentum.
- [ ] **Label incoming issues immediately** (`bug`, `enhancement`,
      `good first issue`) and push quick fixes fast — a visible
      commit-in-response-to-issue within a day is the strongest possible
      signal that the project is alive.
- [ ] **Capture the recurring questions** from HN/Reddit into README/FAQ
      edits the same week, while they're fresh.
- [ ] **Set the post-launch roadmap publicly** — pin an issue or open a
      Discussion with the "Now" backlog (signing, winget, transaction
      control), so the top of the funnel sees where it's going.
