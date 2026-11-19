# Design: Localization (i18n)

Status: accepted (owner, 2026-11-15) · Issue [#435](https://github.com/Shman4ik/pgNimbus/issues/435) · Created 2026-11-15

## What was promised

On #435 the owner promised, for the next release: UI strings move out of code and
XAML into resource files with English as the base language; a language setting
in Settings, defaulting to the OS language; and a comment on the issue linking
the English base file with notes on how to add a language. A contributor
(sevencat) offered the Simplified Chinese translation.

The goal: **adding a language is translating one file**, with nothing else to
build or register.

## The mechanism

```
src/PgNimbus.Core/Localization/
  Strings.resx            English, the base. Every key, with a <comment> for context.
  Strings.zh-Hans.resx    One file per language: only the keys it translates.
  Localizer.cs            Loads one translation; Get / Plural / Format.
  PluralRules.cs          CLDR plural categories for whole numbers.
src/PgNimbus.Strings.Generator/   Source generator: Strings.resx → typed Strings class.
src/PgNimbus.App/Platform/SystemLanguages.cs   The OS language list.
```

**`.resx` files, read by our own code.** `.resx` is the .NET resource format, so
Visual Studio, Rider, ResXManager, Crowdin and Weblate all edit it, and each entry
carries a `<comment>` that tells the translator where the text appears. What we do
*not* use is the rest of the `.resx` machinery:

- **No `ResourceManager`, no satellite assemblies.** A translation is embedded in
  `PgNimbus.Core.dll` as the file itself (`Type="Non-Resx" WithCulture="false"`,
  logical name `PgNimbus.Strings.zh-Hans.resx`) and parsed with `XmlReader` when the
  app starts. `ResourceManager` finds translations by `CultureInfo` in satellite
  assemblies, and both halves fail here: the App runs with `InvariantGlobalization`
  (only the invariant culture exists), and a culture-named satellite under NativeAOT
  crashed Avalonia's asset resolver at startup (the App csproj's
  `SatelliteResourceLanguages=en` comment). A language here is a plain string tag.
- **English is compiled in.** The generator writes each English value into its
  accessor (`Strings.About_Tagline => Localizer.Get("About_Tagline", "A fast, …")`),
  so the base file is not shipped or read at run time, English can never fail to
  load, and a key a translation lacks falls back to English one key at a time. A
  half-finished translation is usable.
- **Typed accessors from a source generator.** `PgNimbus.Strings.Generator`
  (netstandard2.0, analyzer reference from Core, nothing ships) turns every entry of
  `Strings.resx` into a member of `PgNimbus.Core.Localization.Strings`:
  - plain text → a property: `Strings.About_Tagline`;
  - text with `{name}` placeholders → a method with one parameter per name:
    `Strings.About_Version(version)`; a translation may reorder the placeholders,
    since they are filled by name;
  - a `Key_one` + `Key_other` pair → a plural method `Strings.Rows(count)`; a
    translation supplies the forms its language has (`_few`, `_many`, … per CLDR; Chinese
    has `_other` only), chosen by `PluralRules`.

  It also writes a `StringKey` enum of the plain entries. A typo in a key is a
  compile error in C# and in XAML alike, and the IDE shows the English text on
  hover.

**XAML reads strings with `{l:Tr Key}`** (`xmlns:l="using:PgNimbus.App.Localization"`),
a markup extension whose constructor takes a `StringKey`: the XAML compiler
converts the name at build time and refuses an unknown one (AVLN3000). It returns
`Localizer.Observe(key).ToBinding()`, an observable binding with no reflection, so
compiled bindings and NativeAOT are content. Strings built in view models and
code-behind call `Strings.X` directly.

**Why Core, not the App or nimbusUi.** Many user-facing strings are made in Core:
command titles in `CommandCatalog`, the TLS mode labels in `SslModes`, plan
warnings from `PlanAnalyzer`, `PlanComparison`'s notes, the backup tool guide,
error messages written for a form (`SshTunnelException`). Core has no UI
dependency, and the string table needs none, so hard rule 1 holds. nimbusUi
draws no text of its own today (its caption buttons and controls carry no
strings), so nothing shared moves. If kubeNimbus wants localization later, the
runtime (`Localizer`, `PluralRules`, the generator) is product-neutral and can
become a small non-UI nimbusUi project then; until then it stays here (subtree
rule 2: when in doubt, leave it here).

## Choosing the language

`AppSettings.Language` is `"auto"` (the default) or a tag. Settings ▸ General
gets a **Language** combo: "System (English)" first, naming what auto resolves
to, then every carried language in its own name (`English`, `简体中文`), read
from each file's `_LanguageName` entry. A note under it says the change applies
on the next start.

**The OS language is asked from the OS**, because under `InvariantGlobalization`
`CultureInfo.CurrentUICulture` is invariant on every machine
(`Platform/SystemLanguages`): Windows' `GetUserPreferredUILanguages`, macOS's
`CFLocaleCopyPreferredLanguages` (the System Settings list, per-app language
included), and on Linux `LANGUAGE`, then `LC_ALL`/`LC_MESSAGES`/`LANG`. Every call
is blittable. `Localizer.Resolve` walks that list, best first, and takes the first
language the build carries: an exact tag, then Chinese by script (`zh-CN`,
`zh-SG`, `zh-Hans-*` → `zh-Hans`; `zh-TW`, `zh-HK`, `zh-MO`, `zh-Hant-*` →
`zh-Hant`), then language and script, then the bare language, then any regional
file of that language. Nothing matches → English.

**Loaded at startup, changed live.** `Program.Main` loads the language before the
first window. A change in Settings applies at once (the owner's choice over "on
the next start"): `Localizer.Load` pushes the new text to every `Observe`
subscriber (every `{l:Tr}` binding; observers are held in a
`ConditionalWeakTable`, so a closed window is not kept alive), then calls the
`Localizer.WhenChanged(owner, handler)` handlers, held weakly by owner, with which
a view model re-raises the properties that compose text. A status line written by
an action stays in the language it was written in. Menus built in code are built
per opening or rebuilt.

## What is not translated

- SQL: keywords, completion items, generated scripts and their `--` comments
  (`DdlTemplates`, the grant/role/policy builders, the review script). They are
  code, and a comment in another language in a shared migration helps nobody.
- Server messages: Postgres translates its own (`lc_messages`).
- The crash log, and the GitHub issue the crash window pre-fills: they are read
  by maintainers. The crash *window's* own text is translated.
- `docs/reference/keyboard-shortcuts.md` and the rest of the docs site: English.
  The generator test pins English.
- Data: names, values, the `ISO` dates and invariant numbers the grid shows
  (CLAUDE.md's "Dates and times in the grid are ISO" stays as it is: those are
  values that must read back, not prose).
- Brand and product names (pgNimbus, PostgreSQL, pg_dump), key names on keycaps.

## Fonts

Inter and JetBrains Mono NL have no CJK glyphs. Avalonia falls back per character
to a system font that has the glyph (`FontManager.TryMatchCharacter`), so Chinese
draws on Windows (Microsoft YaHei), macOS (PingFang) and Linux with Noto CJK
installed. The interface face defaults to the system face already (CLAUDE.md UI
rule 11), which on a Chinese Windows or macOS is the CJK-capable one. `KeyCapFont`
only ever draws key glyphs. Checked headlessly on Windows (the harness's
`--language` option renders any scenario in a translation): Chinese draws.
**Open:** which face the fallback picks for Han characters. With no language
hint, Windows may choose a Japanese face for some of them. If a native reader
sees that, the fix is a per-language fallback list ahead of the system's
(`Microsoft YaHei UI`, `PingFang SC`, `Noto Sans CJK SC`) applied with the
interface font. Also on the release pass: the Linux packages' font dependency
(`fonts-noto-cjk` as a recommendation, not a requirement).

## Proving nothing is left behind

1. **The pseudo-locale** `qps-ploc` is built in: every string is its English text
   accented and bracketed (`[Ćöññéçţ]`). A UI test opens every screenshot scenario
   in it and fails on any visible text (TextBlock, button and menu header,
   placeholder, tooltip) that is neither pseudo-localized nor data from the
   fixture. That catches strings from code-behind and view models, which no
   static check can. It covers every scenario but the controls gallery (a page of
  sample controls), and a scenario added later fails a test until it is listed.
2. **A XAML lint** test fails on a literal `Text=`, `Content=`, `Header=`,
   `ToolTip.Tip=`, `PlaceholderText=`, `Watermark=`, `Title=` or
   `cmd:CommandTip.Text=` with letters in any view.
3. **The translation files are checked**: every key a translation has exists in
   the base, its placeholders are the base's, plural forms belong to its language,
   and `_LanguageName` is present. A stale translation fails the build's tests,
   not a user's screen.
4. **Screenshots stay English**: the harness and the UI tests never call
   `Localizer.Load` with anything but English (and the pseudo test), so the
   baselines and published screenshots don't change. The harness's `--language`
   renders a translation for review and refuses `--baseline`; with `--publish` it
   writes only the Store listing's set, to `design/store/screenshots/<tag>/`
   (1.4.0: zh-Hans, for the Chinese listing).

## Verified so far

- A NativeAOT `win-x64` publish with a translation embedded: no satellite
  directory, no IL2026/IL3050 warnings; the published exe loads the embedded
  translation, the OS language reads `en-US`, and a `{l:Tr}` binding returns the
  translation and follows a live switch to the pseudo-locale.
- TUnit runs a key-less `[NotInParallel]` test only after every parallel test has
  finished (measured with a probe), so a test that switches the process-wide
  language disturbs no other test.

## Delivery

A GitHub stack, each PR reviewable alone:

1. **Infrastructure**: generator, `Localizer`, `PluralRules`, `SystemLanguages`,
   the Language setting, the pseudo-locale, the file checks, this note, the
   contributor guide (`docs/dev/translating.md`), CLAUDE.md. First slice of
   strings: Settings, About, the connection dialog, the command catalog.
2. **Main window**: shell, sidebar, editor, results grid, row details, filters.
3. **Dialogs and secondary windows**: monitoring windows, Security, Backup and
   Restore, Query Lab, the confirm and edit dialogs.
4. **Core messages** (plan warnings, comparisons, TLS labels, tool guide, error
   messages) and the pseudo-locale UI test turned on for every scenario.

Then the comment on #435 pointing at `Strings.resx` and `docs/dev/translating.md`.
