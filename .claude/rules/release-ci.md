---
description: "Benchmarks, release pipeline, Actions storage budget, supply chain, Microsoft Store, website and docs publishing."
paths:
  - ".github/**"
  - "scripts/**"
  - "installer/**"
  - "packaging/**"
  - "website/**"
  - "docs/**"
  - "mkdocs.yml"
  - "PgNimbus.Benchmarks/**"
  - "Directory.Build.*"
---

<!-- Moved out of the root CLAUDE.md so it loads only when working on these paths. Same rule applies: keep it current in the same PR. -->

## Benchmarks pipeline

"Fast" is measured, not asserted. `.github/workflows/benchmark.yml` runs
[`scripts/benchmarks/run-benchmarks.sh`](scripts/benchmarks/run-benchmarks.sh)
(ubuntu runner + a `postgres:17` service container). It's a reusable
workflow (`workflow_call`) invoked as a job from `release.yml` — it no
longer runs on every PR or push to `main`, only as part of the release
pipeline (tag push, or a manual `workflow_dispatch` test run of
`release.yml`), so it measures a real tagged build rather than every commit.
It's also directly `workflow_dispatch`-able on its own for ad hoc
measurement. Results go to the job summary and a `bench-results` artifact;
real tag-triggered releases also append to the gh-pages history via
`benchmark-action/github-action-benchmark` (charts at
`https://shman4ik.github.io/pgNimbus/dev/bench/`) — controlled by the
`record_history` input, which `release.yml` sets from
`startsWith(github.ref, 'refs/tags/v')` so `workflow_dispatch` test runs of
the release pipeline don't pollute the trend history. Three moving parts:

1. **Startup probe** — `PGNIMBUS_STARTUP_PROBE=1` makes the app print
   `PGNIMBUS_STARTUP_PROBE window_ms=… rss_bytes=…` after its first window
   renders its first frame, then exit (`PgNimbus.App/StartupProbe.cs`, armed
   in `App.OnFrameworkInitializationCompleted`). `window_ms` is measured from
   OS process start, so it captures AOT-vs-JIT differences honestly.
2. **`PgNimbus.Benchmarks`** — console project measuring connect (cold pool),
   `SELECT 1` round-trip, time-to-first-`RowBatch`, and full-stream
   throughput of a 100k-row mixed-type SELECT, through `QueryEngine`'s
   streaming path (the same API the UI uses). Prints `PGNIMBUS_BENCH
   name=value` lines; config via `PGNIMBUS_BENCH_CONN/ROWS/ITERS`.
3. **The script** — builds JIT Release, publishes linux-x64 NativeAOT (or
   measures an existing publish dir given via `PGNIMBUS_BENCH_PUBLISH_DIR` —
   the release pipeline passes build-linux's x64 output through the
   `publish_artifact` workflow input this way, as a `.tar.gz` because
   artifact zips drop the exec bit, so the slow AOT publish isn't done
   twice), runs
   the startup probe N times per mode under Xvfb (one discarded warm-up run,
   then medians), runs the query benchmarks, and writes
   `bench-results/benchmarks.json` (github-action-benchmark
   `customSmallerIsBetter` format — keep every metric smaller-is-better, so
   throughput is reported as stream *time*) plus `summary.md`.
   `PGNIMBUS_BENCH_SKIP_AOT=1` skips the slow AOT publish for local runs. Also
   tracks size: the AOT exe alone (`binary_size_mb`) and the shipped publish
   files (`publish_size_mb` — the publish output minus `*.pdb`/`*.dbg` debug
   symbols, mirroring the exclusion the MSI/MSIX packaging applies, so the
   metric tracks what installers actually package rather than what publish
   leaves on disk; the publish dir is wiped before publishing so repeated
   local runs never count stale leftovers) — the latter is the more honest
   "app size" number since side-car native libs bundled alongside the exe
   (`libSkiaSharp`, `libHarfBuzzSharp`) dwarf it.

Numbers are machine-relative (this sandbox: ~160 ms AOT / ~2 s JIT to first
frame; CI runners differ) — the point is the trend per commit, not the
absolute value. If a change renames a metric in `benchmarks.json`, its
gh-pages history starts over under the new name.

**User-facing copy quotes the CI number, never a local one** (2026-09). The
README, the docs home page and the landing page say "about 0.2 s", which is what
the GitHub runner has recorded for NativeAOT startup on every release since late
July. They used to say "~100 ms", a figure from a development sandbox that no
public chart backed, which is exactly the kind of number a Show HN thread asks
about first. If the CI figure moves for good, change all three together.

## Release pipeline

What to walk before tagging, and what past release passes found, is
[`docs/RELEASE-CHECKLIST.md`](docs/RELEASE-CHECKLIST.md) (living, one log row per
release); this section is how the pipeline itself works.

`.github/workflows/release.yml` runs on every `vX.Y.Z` tag push (or manually
via `workflow_dispatch`, which builds everything but skips the "release"
job so it never publishes).

**What a tag is allowed to mean, and who holds the token** (2026-09, security
audit findings 5 and 15). Four things the audit found, each now a rule:
- **The built commit has to be on `main`.** A tag can be pushed on any commit,
  and the attestation only ever proved "built by this workflow" — nothing said
  "from a reviewed commit". The first step of every build job (and `sbom`) is
  `.github/actions/require-on-main` (`git merge-base --is-ancestor "$GITHUB_SHA"
  origin/main`, over a `fetch-depth: 0` checkout). It runs on every event, not
  only tag pushes, so a `workflow_dispatch` rehearsal exercises the gate too —
  which means a rehearsal has to start from a commit already on `main`. The
  `release` job and the benchmark's `record_history` gate on
  `github.event_name == 'push' && startsWith(github.ref, 'refs/tags/v')`: a
  dispatch with a tag chosen under "Use workflow from" carries the same
  `github.ref`, and used to publish while the file's header said it never would.
  The repo-side half (a tag ruleset for `refs/tags/v*`, immutable releases)
  is a setting, not a workflow change; the PR that added this listed the
  commands.
- **The workflow token is read-only.** Top-level `permissions: contents: read`;
  only `release` (assets, attestations) and the `benchmark` call (gh-pages
  history) get `contents: write`, and every checkout in `release.yml` and
  `benchmark.yml` sets `persist-credentials: false` — the benchmark action
  pushes through its own `github-token` input (it puts the token in the remote
  URL itself), so nothing needs one left in `.git/config`. Before this, every
  build job inherited a workflow-wide `contents: write`, and the MSBuild tasks
  of ~35 packages, the packaging tools and the smoke-launched app all ran with
  a token that could rewrite releases and `gh-pages`.
- **Nothing user-influenced is spliced into a script.** `inputs.version`,
  `github.ref_name` and `$VERSION` reach every `run:` as environment variables
  (an expression inside `run:` is expanded before the shell sees it, so a tag
  named `v1.2.3;id` would have run `id`), and
  `.github/actions/version/action.yml` refuses anything that is not
  `^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.]+)?$`. Same shape in `screenshots.yml`.
  `docs.yml` is split the same way: a read-only `build` job on every event, and
  a `publish` job that only runs for `main` and is the only one with write.
- **The release is created by `gh release create`**, the preinstalled
  first-party CLI, not `softprops/action-gh-release`: that is the one step
  holding `contents: write` beside `id-token: write`, and third-party code
  there could upload different binaries *and mint valid provenance for them*.
  Deliberately create-only — a re-run after the release exists fails rather
  than quietly replacing assets someone may already have downloaded; delete
  the release and re-run, or cut a new tag. Once immutable releases are on,
  the latter is the only way.

**Every package is launched before it ships.** Each build job runs
`scripts/release/smoke-launch.sh` (or `Smoke-Launch.ps1` on Windows) against
its own artifacts with `PGNIMBUS_STARTUP_PROBE=1`, asserting both a clean exit
*and* the probe line — an app that quit before drawing anything also exits 0.
Windows smokes the publish output and the MSI after a silent per-user install
(uninstalled in the same step); macOS smokes the publish output and the binary
inside the mounted `.dmg`; Linux smokes the publish output, the `.tar.gz`, the
`.AppImage` (`--appimage-extract-and-run`, runners have no FUSE) and the `.deb`
after `apt-get install` resolves its own `Depends` — that last one is how a
missing runtime library gets caught here instead of on a user's machine. The
Linux legs need `xvfb`; macOS runners have a real window server. `release`
already `needs` all three jobs, so this is the publish gate. Note
`PgNimbus.App` is a `WinExe` with no console of its own — the probe line is
still readable because redirecting stdout gives the process a handle to write
to (verified, not assumed).

It produces, per tag:

- **Windows** — `dotnet publish -r win-x64 -p:PublishAot=true`, then a
  per-user WiX v5 MSI built from [`installer/windows/Product.wxs`](installer/windows/Product.wxs)
  via the `wix` .NET tool from the repo's tool manifest
  (`.config/dotnet-tools.json`, restored with `dotnet tool restore`, run as
  `dotnet wix build ... -d PublishDir=... -d Version=...`). The manifest
  pins `wix` and `CycloneDX` to exact versions that Dependabot's nuget
  ecosystem bumps; it replaced `dotnet tool install --global wix --version
  5.*`, which floated (2026-09). Per-user (installs to `%LocalAppData%`, no elevation) is
  deliberate: the MSI is currently **unsigned** (no code-signing cert yet),
  and per-machine + unsigned is a much worse UAC/SmartScreen experience.
  The `UpgradeCode` GUID in `Product.wxs` is fixed forever — never
  regenerate it, that's what makes installing a newer tag upgrade in place
  instead of side-by-side.
- **macOS** — `osx-arm64` only, built on a `macos-14` runner. GitHub retired
  the last Intel macOS runner image (`macos-13`) in December 2025 and has
  said x86_64 macOS support ends entirely once the `macos-15` image retires
  (Fall 2027) — there's no GitHub-hosted way to build `osx-x64` anymore, so
  don't re-add an Intel matrix leg without a self-hosted Intel Mac runner.
  Also pins to the newest pre-installed Xcode below major version 26:
  Xcode 26 changed Swift auto-linking in a way that breaks NativeAOT's
  static link of `libSystem.Security.Cryptography.Native.Apple.a`
  ("symbol(s) not found for architecture arm64" / `pal_swiftbindings`),
  closed "not planned" upstream
  ([dotnet/runtime#116448](https://github.com/dotnet/runtime/issues/116448)).
  The publish output is wrapped into an **ad-hoc signed** (`codesign --sign -`),
  un-notarized `.app` + `.dmg` by
  [`scripts/macos/build-app-bundle.sh`](scripts/macos/build-app-bundle.sh),
  which also generates `.icns` directly from the `design/masters/icon/` tiles
  via `sips`/`iconutil` (stock macOS tools, no extra dependency) — each
  iconset slot uses the exact-size master when one exists, else downscales
  from `icon-1024.png`.
  **The ad-hoc signature is not decoration, it picks which Gatekeeper dialog a
  user sees** (added 2026-08, after 0.11.1 shipped with none): a quarantined
  bundle carrying *no* signature is reported as "pgNimbus is damaged and can't
  be opened. You should eject the disk image", which reads as a corrupt
  download, sends people back to Releases for the same bytes, and has no
  right-click → Open escape. The same bundle ad-hoc signed fails the same
  Gatekeeper check as "Apple cannot check it for malicious software" — true,
  and clearable by right-click → Open (System Settings → Privacy & Security →
  Open Anyway on Sequoia). It is also what makes an arm64 binary loadable at
  all. Two consequences for the script: the NativeAOT `*.dsym` is deleted
  before signing (a `.dsym` is itself a bundle directory, the one shape
  `codesign --deep` won't seal inside `Contents/MacOS`, mirroring the Linux
  packages' `*.dbg` exclusion), and dylibs are signed inside-out before the
  bundle. The `.dmg` also carries the `/Applications` symlink it had always
  claimed to (the volume used to hold the app alone, so the obvious gesture was
  double-clicking it on the read-only image — that is where "the disk image
  should be ejected" came from). `release.yml`'s mounted-`.dmg` smoke step
  gates both: `Signature=adhoc` present after `hdiutil`, and the symlink there.
  **The bundle must not ship debug symbols, and must not promise an older macOS
  than it was built for** (2026-09-29, first real-Mac pass of 1.0.0). The script's
  `rm -rf *.dsym` never matched NativeAOT's `PgNimbus.App.dSYM` (bash globs are
  case-sensitive), so 130 MB of symbols shipped in every `.dmg` (189 MB installed
  instead of 58). It now removes `*.dSYM` and `*.pdb` with `find -iname`. The
  Info.plist's `LSMinimumSystemVersion` said 11.0 for a binary whose `minos` is 12.0,
  so macOS 11 failed in dyld instead of showing "requires macOS 12"; it is 12.0 now.
  The mounted-`.dmg` step gates both (no `*.dsym`/`*.pdb` anywhere in the `.app`;
  the plist value not older than the highest `vtool -show-build` `minos` of any
  Mach-O), and `MacOSEntitlementsTests` keeps the template from going under 12.
  **Ad-hoc signing has a Keychain cost** that only a Developer ID removes: see
  hard rule 4 in `CLAUDE.md` and "Known caveats" in `docs/RELEASE-CHECKLIST.md`.
  None of this substitutes for a Developer ID signature plus notarization,
  which needs a paid Apple account and would remove the warning outright.
  **Both `codesign` calls also pass `--options runtime`** (security audit
  2026-09, finding 18): without hardened runtime, any process running as the
  same user can launch pgNimbus with `DYLD_INSERT_LIBRARIES` and run
  arbitrary code as it — inheriting whatever the app's ad-hoc code hash is
  trusted for, a Keychain item's ACL included. Hardened runtime also turns on
  library validation, which refuses to load a dylib unless it carries the
  main executable's own Team ID; every dylib in the bundle is ad-hoc signed
  alongside the app (no Team ID at all), so library validation would refuse
  them all at launch. `installer/macos/Entitlements.plist` sets
  `com.apple.security.cs.disable-library-validation` to allow exactly that
  and nothing else, and both `codesign` calls pass `--entitlements` pointing
  at it. Developer ID plus notarization (ROADMAP T5) is still the fix that
  removes the Gatekeeper warning outright; this closes the arbitrary-code-
  execution gap in the meantime, on the same ad-hoc signature.
  The plist is only read by `codesign` on the release runner, so `MacOSEntitlementsTests`
  parses it as XML in every build: the first 1.0.0 tag run failed there with "Failed to
  parse entitlements: AMFIUnserializeXML: syntax error near line 6", because a comment said
  `(--options runtime)` and `--` may not appear inside an XML comment.
- **Linux** — `linux-x64` + `linux-arm64` (the arm64 leg runs natively on
  GitHub's free `ubuntu-24.04-arm` runners — no cross-compile toolchain).
  Each RID is packaged three ways by
  [`scripts/linux/build-packages.sh`](scripts/linux/build-packages.sh):
  `.AppImage` (appimagetool **1.9.1** and the type2 runtime release
  **20251108** downloaded at build time, each checked against a hardcoded
  sha256 — the `digest` GitHub publishes per release asset — and the runtime
  handed over with `--runtime-file`, so appimagetool never fetches "latest"
  on its own; it used to come from the moving `continuous` release with no
  checksum, and the runtime it then downloaded is the first code that runs
  when a user starts the AppImage (2026-09). Run with
  `--appimage-extract-and-run` since CI runners lack
  FUSE; `AppRun` is a plain symlink to the binary — NativeAOT resolves the
  side-car `libSkiaSharp`/`libHarfBuzzSharp` next to `/proc/self/exe`, so
  no wrapper script), `.tar.gz` (the publish output under a versioned top
  dir), and `.deb` (`dpkg-deb`, package id `pgnimbus`, binary at
  `/usr/lib/pgnimbus/` + `/usr/bin/pgnimbus` symlink; `Depends` lists the
  X11-family libs Avalonia's X11 backend uses at runtime plus fontconfig
  for Skia — Skia/HarfBuzz themselves are bundled; a semver prerelease `-` becomes Debian `~` so CI test versions
  sort before releases). The desktop entry comes from
  [`installer/linux/pgnimbus.desktop.template`](installer/linux/pgnimbus.desktop.template)
  (`__EXEC__` placeholder: the AppImage execs `PgNimbus.App`, the deb
  `pgnimbus`), icons from the `design/masters/icon/` tiles. The NativeAOT
  `*.dbg` symbols side-file is excluded from all three packages. Unsigned,
  like the other direct-download channels.
- **winget** — the `build-windows` job renders (via
  [`scripts/winget/render-manifest.sh`](scripts/winget/render-manifest.sh)
  and the templates in `packaging/winget/`) the three manifest files
  winget requires and validates them with `winget validate` right after
  building the MSI (same job — the MSI and its SHA256 are already at
  hand, no separate runner), but does
  **not** submit them anywhere, and that is now a decision rather than a
  pending step (2026-09 backlog review, issue #134 closed): `winget install
  pgNimbus` already resolves through the `msstore` source to the
  Microsoft-signed Store package, so a community-source entry would only add
  the unsigned MSI beside it. The generated `winget-manifests.zip` release
  asset stays, so the first `winget-pkgs` PR (which registers the
  `pgNimbus.pgNimbus` identifier) can still be filed by hand if the msstore
  source turns out not to be enough — e.g. machines where it is disabled.

The direct-download MSI is **unsigned** and stays that way — deliberately
**not** pursuing a paid signing service (Azure Artifact Signing / a purchased
Authenticode cert): pgNimbus is a free OSS project with no revenue. macOS is
the one exception on the plan (ROADMAP T5, confirmed 2026-09-27): a Developer
ID signature plus notarization for the `.dmg`, because there is no free
equivalent of the Store's re-signing there and the ad-hoc signature above still
leaves every Mac user at "Open Anyway". Until that lands the `.dmg` is ad-hoc
signed only. Microsoft
Store publishing gets the trust/SmartScreen benefit for $0 instead (Store
re-signs an uploaded MSIX with its own trusted certificate during
certification — the package only needs a throwaway self-signed cert to
satisfy the upload requirement, not a purchased one), and Store apps are
automatically discoverable via winget's built-in `msstore` source with no
separate winget submission. It's an *additional* channel, not a replacement
for the direct MSI, and the two coexist.

### Actions storage is a 0.5 GB budget (2026-08)

The account's included GitHub Actions storage is **0.5 GB**, and it is a
*standing* budget, not a per-run one: an artifact counts for every day it
stays alive. So **every `upload-artifact` must set `retention-days`** — the
default is 90, and at 90 days this pipeline held ~6.8 GB (13x the allowance)
in copies of things that were already stored for free somewhere else. Two
rules keep it there:

1. **An artifact that ships in the GitHub Release gets `retention-days: 1`.**
   Release assets don't count against the Actions allowance, and the `release`
   job consumes these in the same run — the artifact is a job-to-job hand-off,
   not storage. That covers `windows-msi`, `macos-dmg-arm64`,
   `linux-packages-*`, `sbom` (from its own job since 2026-09), `winget-manifests`, and `publish-linux-x64`
   (benchmark input). A day is still long enough for a human to grab a
   `workflow_dispatch` test build, where the `release` job never runs.
   `windows-msix` is the one exception at 14 days: Partner Center submission is
   a manual download-and-upload, so it has to outlive the run.
2. **Diagnostic artifacts upload on `failure()` only, for days not months.**
   The CI `screenshots` artifact is worth looking at exactly when the visual
   regression went red; on a green run it is a byte-for-byte re-render of
   `tools/Screenshot/baselines/`, which is already in git. Pair the condition
   with `if-no-files-found: ignore` — the usual failure is the render step
   throwing, which leaves the directory empty, and a missing diagnostic must
   not turn one red step into two.

Retention is **not retroactive**: lowering it leaves already-uploaded artifacts
on their original 90-day clock, so a change like this needs a one-time purge of
the backlog (`gh api repos/OWNER/REPO/actions/artifacts` → `DELETE`) to actually
free anything. The repo's default retention is set to 7 days as a backstop for
uploads that forget rule 1.

Runner *minutes* ride the same fix from the other side: every workflow that
triggers on both `push` and `pull_request` keys its `concurrency` group on
`github.event.pull_request.head.ref || github.ref`. Keyed on `github.ref` the
two triggers land in different groups (`refs/heads/x` vs `refs/pull/N/merge`)
and run the whole job twice for one commit — which is also two artifacts.

### Supply-chain proofs (2026-07)

Unsigned binaries still get verifiable provenance, six layers (security audit
2026-09: finding 5 added the pinned inputs, finding 18 the pinned SDK and the
pinned NuGet sources):

- **SLSA attestations** — the release job runs
  `actions/attest-build-provenance` over every published asset (needs the
  job's `id-token: write` + `attestations: write` permissions). Verify a
  download with `gh attestation verify <file> --repo Shman4ik/pgNimbus
  --signer-workflow Shman4ik/pgNimbus/.github/workflows/release.yml
  --source-ref refs/tags/v<ver>` — plain `--repo` with no `--signer-workflow`/
  `--source-ref` accepts an attestation from *any* workflow or ref in the
  repo, which proves nothing about which build produced the file. Without
  `gh`, `sha256sum -c SHA256SUMS.txt --ignore-missing` checks a download
  against the same release's checksum file, which is attested too. This is
  the $0 substitute for Authenticode on the direct-download channel; it does
  nothing for SmartScreen (the Store channel covers that). What it does *not*
  prove is that the workflow ran only reviewed inputs — which is the next
  layer.
- **Pinned inputs** (2026-09, audit finding 5). Every `uses:` in
  `.github/workflows/` and `.github/actions/` is a full 40-character commit
  SHA with a `# vX.Y.Z` comment; Dependabot's `github-actions` ecosystem
  (`.github/dependabot.yml`, already configured) moves the SHA and the comment
  together, so a pin is never a freeze. Two of the actions were pinned to
  *branches* before this (`dependency-review-action@v5`,
  `github-action-benchmark@v1`, the latter running with `contents: write`
  during a release). The .NET tools (`wix`, `CycloneDX`) are exact versions in
  `.config/dotnet-tools.json`; appimagetool and the AppImage runtime are fixed
  releases with sha256 checks in `build-packages.sh` (see the Linux bullet
  above). The repo-side lock — "Require actions to be pinned to a full-length
  commit SHA" (`actions/permissions` `sha_pinning_required`) — is a setting for
  the owner to turn on; without it a future `@v8` slips past review.
- **SBOM** — its own `sbom` job (ubuntu, `contents: read`) generates a
  CycloneDX JSON SBOM of the App's full NuGet graph (`dotnet dotnet-CycloneDX`
  from the tool manifest on `PgNimbus.App.csproj`, `-c Release`, the
  configuration the binaries ship in), then
  `scripts/release/sbom_add_runtime.py` (stdlib-only, unit-tested in
  `scripts/release/test_sbom_add_runtime.py`) patches in two components the
  NuGet graph can't see: the `Microsoft.NETCore.App.Runtime.linux-x64`
  runtime pack a NativeAOT publish statically links in (GC, TLS and crypto
  code — exactly the code a security audit would want listed) and the
  matching `Microsoft.DotNet.ILCompiler` toolchain pack. Neither is a
  `<PackageReference>`; the SDK resolves them at publish time for the target
  RID, so `dotnet-CycloneDX` never lists them on its own. The version is read
  from `dotnet --list-runtimes`, filtered to the SDK's own major.minor (from
  `global.json`'s pinned `sdk.version`) rather than the first line, since a
  GitHub-hosted runner carries several side-by-side major versions; the
  `sbom` job installs the same SDK the build legs do, so that is the runtime
  build-linux's x64 leg links. Ships as the `pgNimbus-<ver>-sbom.cdx.json`
  release asset, checksummed and attested like the binaries. Generated once —
  the NuGet graph is RID-independent, and the one runtime component names
  linux-x64 only (the Windows and macOS packs are not listed). It used to be a
  step of the linux-x64 build leg, after the packages were built; moved out so
  a third-party tool never shares a runner with the binaries that ship.
- **Vulnerability gates** — the repo-root `Directory.Build.props` sets
  `NuGetAuditMode=all` (transitive packages too) and promotes
  moderate/high/critical audit warnings (NU1902–NU1904) to errors, so any
  `dotnet build`/`restore` — local or CI — fails on a known advisory; ci.yml
  additionally runs `dependency-review-action` on PRs to block newly-added
  vulnerable packages at review time.
- **Pinned SDK line** — `global.json` carries `sdk.version` `10.0.100` with
  `rollForward: latestFeature`: any 10.0 SDK at or above it builds, so the
  1xx-band SDK Ubuntu's apt package ships (the verify skill's sandbox recipe)
  still works, while an 11.0 SDK does not pick the build up. It is a floor, not
  the release's exact SDK (CI installs the newest `10.0.x`); the runtime pack a
  release actually links is what the SBOM records (above), and
  `sbom_add_runtime.py` reads the major.minor from this file.
- **Pinned NuGet sources** — the repo-root `nuget.config` clears every
  package source but nuget.org and maps every package id to it with
  `packageSourceMapping`. Without it, a restore reads whatever sources a
  machine has accumulated (a corporate feed, a leftover from another
  project), which is the opening `dotnet restore`/`build` needs for a
  dependency-confusion attack: a package on an extra source, named like one
  this repo already restores, silently wins.

### Microsoft Store (MSIX)

`build-windows` also packs `publish/win-x64` into a self-signed `.msix` via
[`scripts/windows/build-msix.ps1`](scripts/windows/build-msix.ps1), uploaded
as the `windows-msix` CI artifact — **not** attached to the public GitHub
Release, since a self-signed MSIX can't be installed without the user
manually trusting the cert first, and Store re-signing only happens after
you upload it to Partner Center.

- **Manifest**: [`installer/msix/Package.appxmanifest`](installer/msix/Package.appxmanifest)
  is a template (`$VERSION$` placeholder) with `Identity/Publisher` hardcoded
  to this repo's reserved Partner Center product identity
  (`DmitriiShmanev.pgNimbus` / `CN=04FDF7B0-6D86-4EB7-B798-21CD434897BC`,
  Store ID `9N6SZT42XJ24` — the listing is **live** as of 2026-07:
  <https://apps.microsoft.com/detail/9N6SZT42XJ24>) — plain
  Win32/Desktop Bridge (`runFullTrust`
  capability, `EntryPoint="Windows.FullTrustApplication"`), not Windows App
  SDK, since the app is a native AOT exe with no WinUI dependency.
- **Tile assets**: `PgNimbus.App/Assets/Msix/*.png` (Square44x44Logo,
  Square150x150Logo, StoreLogo — each as 5 DPI-scale files, plus
  Square44x44Logo's 10 unplated targetsize files) are generated by
  [`scripts/windows/make-app-icons.ps1`](scripts/windows/make-app-icons.ps1)
  from the `design/masters/icon/` tiles (44/50/150 px scale-100 bases from the
  48/48/256 px masters respectively; scale-200/400 sizes that exceed their
  small master fall back to the 1024 px master to avoid upscale blur; the
  unplated variants reuse the transparent `window-{dark,light}-256.png`
  masters) — excluded from `AvaloniaResource` in the App csproj since they're
  packaging-time-only. A single flat file per logo used to be enough for the
  package to *build*, but Windows silently backplates/shrinks it on the
  taskbar, Start, and the sideload "Install app?" dialog when it can't find a
  qualifier-matched size — hence the scale/targetsize sets (fixed 2026-07).
- **`build-msix.ps1`**: stages the publish output + tile assets + rendered
  manifest, then runs `makepri.exe` (`createconfig` + `new`) to compile those
  qualified filenames into a single `resources.pri` — without it, Windows
  only ever resolves the scale-100/unqualified assets and the rest just sit
  in the package unused. `createconfig`'s default `priconfig.xml` splits
  scale-qualified resources into separate `resources.scale-*.pri` side files
  (meant for `AppxBundle` resource packages with matching manifest
  `<ResourcePackage>` entries); since this is one flat non-bundle package,
  the script strips that `<autoResourcePackage>` splitting so everything
  lands in the one `resources.pri` actually included in the package. Then
  packs with `makeappx.exe`, signs with an ephemeral
  `New-SelfSignedCertificate` (Subject matching the manifest's `Publisher`,
  deleted from the cert store right after signing). Resolves `makeappx`/
  `makepri`/`signtool` by globbing every installed Windows SDK's
  `bin\<ver>\x64` dir and taking the newest, so it doesn't hardcode an SDK
  version that'll drift on GitHub's runner images. MSIX versions are 4-part
  with the last field forced to `0` (Store convention) —
  `ConvertTo-MsixVersion` strips any prerelease suffix like `-ci.42` from
  `VERSION` before padding.
- **Listing media** (2026-09): screenshots (PNG only; GIFs are refused) in
  `design/store/screenshots/`, the trailer and its thumbnail in
  `design/store/trailer/`, and the 16:9 Super hero art the trailer needs in
  `design/store/`. How each is regenerated: `design/LOGO-ASSETS.md` and
  `scripts/demo/record/README.md`.
- **Submission** (manual, not automated yet): the first submission passed
  certification and the listing is live. For updates: download the
  `windows-msix` artifact from the release workflow run and upload it through
  Partner Center → this product → Packages, then submit for certification.
  Could move to the Microsoft Store submission API later (needs its own
  Entra ID app registration under the Partner Center account — free,
  unrelated to Azure Artifact Signing).

## Project website + docs (GitHub Pages)

The `gh-pages` branch hosts three independent things at three paths, and nothing
that writes to one may touch the others: `/` is the landing page, `/docs/` is
the documentation site, `/dev/bench/` is the benchmark history.

### Documentation site (`/docs/`)

MkDocs Material, configured in the repo-root [`mkdocs.yml`](mkdocs.yml), built
from `docs/`. `docs/` doubles as the repo's internal notes directory, so
`exclude_docs` keeps `marketing/`, `design/`, `PROGRESS.md`,
`PRE-LAUNCH-CHECKLIST.md` and `RELEASE-CHECKLIST.md` out of the published site — **only pages listed in
`nav` ship**. Published by
[`scripts/website/publish-docs.sh`](scripts/website/publish-docs.sh), which
replaces `gh-pages:/docs/` alone; `.github/workflows/docs.yml` builds it with
`--strict` on every PR touching `docs/`/`mkdocs.yml` (so a broken link or a page
missing from `nav` fails the check) and publishes on push to `main`. Local
preview: `pip install -r docs/requirements.txt && mkdocs serve`.
`docs/reference/keyboard-shortcuts.md` is **generated** — see UI design rule 5,
don't hand-edit it. `docs/assets/{logo,favicon}.png` are copies of the
`design/masters/icon/` tiles; refresh them if the masters change.

**User-facing prose goes through the `humanizer` skill.** `.claude/skills/humanizer/`
is vendored from <https://github.com/blader/humanizer> (MIT; see its `SOURCE.md`
for the update procedure and the one standing deviation — the README keeps its
emoji section headings). Apply it to the README, the `docs/` pages, release
notes and website copy — its hardest rule is no em/en dashes in user-facing prose,
which is why those files read differently from this one. `CLAUDE.md` and code
comments are internal and keep their own voice.

### Landing page (`/`)

<https://shman4ik.github.io/pgNimbus/> is a hand-written static landing page.
Source of truth is [`website/index.html`](website/index.html) (self-contained
HTML+CSS, light/dark via `prefers-color-scheme`, no external requests);
[`scripts/website/publish-site.sh`](scripts/website/publish-site.sh) assembles
it with assets copied from `design/masters/` and `docs/screenshots/` into the
**root of the `gh-pages` branch** and pushes. The same branch hosts the
benchmark history under `dev/bench/` (written by benchmark-action from the
release pipeline) — the publish script must never touch that directory.
Publishing is manual: edit `website/index.html`, run the script. If the
screenshots or download links change (e.g. a new install channel), update the
page in the same PR.
