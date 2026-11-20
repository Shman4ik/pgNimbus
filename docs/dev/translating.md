# Translating pgNimbus

pgNimbus keeps all of its interface text in one file. English is the base:
[`src/PgNimbus.Core/Localization/Strings.resx`](../../src/PgNimbus.Core/Localization/Strings.resx).
A language is one more file next to it, holding the same keys with translated
values. Nothing else needs to change: the build embeds the file, and Settings
lists the language.

## Add a language

1. Copy `Strings.resx` to `Strings.<tag>.resx` in the same folder. The tag is a
   [BCP 47](https://www.rfc-editor.org/info/bcp47) language tag:
   `zh-Hans` (Simplified Chinese), `zh-Hant` (Traditional Chinese), `de`, `fr`,
   `ja`, `pt-BR`, and so on. Use the script subtag for Chinese, not a region:
   pgNimbus maps `zh-CN` and `zh-SG` systems to `zh-Hans`, and `zh-TW`, `zh-HK` and
   `zh-MO` to `zh-Hant`.
2. Set `_LanguageName` to the language's name in that language (`简体中文`,
   `Deutsch`). Settings shows it in the Language list.
3. Translate each `<value>`. Leave the `name` attributes and the `<comment>`s
   alone. You can also delete entries you have not translated yet: a missing key
   shows in English, so a file that is half done is still useful.
4. Add `<Resource Language="<tag>" />` to `packaging/msix/Package.appxmanifest`,
   so the Microsoft Store lists the language as supported (`MsixManifestTests`
   fails until you do). A maintainer adds the Store listing in that language.
5. Open a pull request.

Each entry looks like this:

```xml
<data name="Settings_LanguageSystem" xml:space="preserve">
  <value>System ({language})</value>
  <comment>Settings, General tab: the first choice in the Language list, which follows the operating system. {language} is that language's name in itself, such as English or 简体中文.</comment>
</data>
```

The comment tells you where the text appears and what it is: a button, a
heading, a tooltip, a sentence in a dialog.

Any tool that edits `.resx` files works too: Visual Studio's resource editor,
Rider, [ResXManager](https://github.com/dotnet/ResXResourceManager), Weblate or
Crowdin. A plain text editor is fine.

## Rules for the values

- **Placeholders.** Text in braces, such as `{language}` or `{count}`, is filled
  in by the app. Keep each placeholder exactly as written (do not translate the
  word inside the braces). You can move it to wherever your language needs it.
  You cannot add a placeholder that the English text does not have.
- **`{chord:…}` and `{key:cmd}`** in a keyboard-shortcut note stand for a key
  combination and for the Ctrl or Command key. Keep them as written.
- **Plurals.** A key ending in `_one` and `_other` (such as `Rows_one`,
  `Rows_other`) is one text with plural forms. English has two forms. Write the
  forms your language uses, following the
  [CLDR plural rules](https://www.unicode.org/cldr/charts/latest/supplemental/language_plural_rules.html):
  Chinese and Japanese use only `_other`, Russian uses `_one`, `_few`, `_many` and
  `_other`, Czech uses `_one`, `_few` and `_other`. If your language is not in
  [`PluralRules.cs`](../../src/PgNimbus.Core/Localization/PluralRules.cs), say so in
  the pull request, and it will be added.
- **What stays in English.** SQL keywords, key names on keyboard shortcuts (`Ctrl`,
  `Shift`, `Cmd`), and product names (pgNimbus, PostgreSQL, pg_dump) are not in the
  file, or appear inside a sentence that you translate around them.
- **Length.** Some text is on buttons and in narrow columns. Keep it as short as
  the English where you can, and look at it in the app (below).
- **XML.** Write `&amp;` for `&`, `&lt;` for `<` and `&gt;` for `>`.

## Try your translation

You do not need to build pgNimbus to see your file. Start a released version of
the app with the environment variable `PGNIMBUS_TRANSLATION_FILE` set to the
path of your file:

On Windows, with the portable zip from the
[releases page](https://github.com/Shman4ik/pgNimbus/releases) unpacked:

```powershell
$env:PGNIMBUS_TRANSLATION_FILE = "C:\path\to\Strings.zh-Hans.resx"
& "C:\path\to\pgNimbus-<version>-win-x64\PgNimbus.App.exe"
```

On Linux, with the `.deb` installed:

```bash
PGNIMBUS_TRANSLATION_FILE=~/Strings.zh-Hans.resx pgnimbus
```

On macOS:

```bash
PGNIMBUS_TRANSLATION_FILE=~/Strings.zh-Hans.resx /Applications/pgNimbus.app/Contents/MacOS/PgNimbus.App
```

The app then uses your file instead of its own languages. The name of the file
gives the language tag, which selects the plural rules.

If you build from source, the tests check your file:

```bash
dotnet test --project tests/PgNimbus.Core.Tests -- --treenode-filter "/*/PgNimbus.Core.Tests.Localization/*/*"
```

The check fails for a key that is not in `Strings.resx` (it was renamed or
removed), a placeholder that differs from the English text, a plural form your
language does not have, or a missing `_LanguageName`.

## Your pull request gets screenshots of every screen in your language

When you open a pull request that changes a file in
`src/PgNimbus.Core/Localization/`, a check named **Translation review** runs. It
draws every screen of the app in English and in each translation, and adds one
comment to your pull request:

- At the top, a table of text that fits in English but not in your language:
  the screen, the English text, your text, and how many pixels it needs and
  has. Make that text shorter, or say in the pull request that a screen needs
  more room.
- Under it, one section per language. Open your language and look at each
  screen. Look for text that is cut off, text that runs into other text, and
  characters that show as boxes.

Each new push to the pull request updates the same comment. The images are
removed when the pull request is closed. A pull request from a fork gets no
comment, because the check cannot write to the repository from a fork. The
images are still in the check's run on the Actions tab, as the `l10n-review`
download, for one day.

To make the same images on your computer, from a clone of the repository:

```bash
scripts/screenshots/render-languages.sh /tmp/l10n
dotnet run --project tests/PgNimbus.App.Tests -c Release -- --treenode-filter "/*/*/TranslationFitTests/*"
```

The first command writes `/tmp/l10n/<language>/<screen>.png`. The second prints
the table of text that does not fit, and writes it to the file that it names.

## When the English text changes

New keys are added to `Strings.resx` as pgNimbus grows. Until a translation has
them, they show in English. To find what is new, compare your file's keys with
`Strings.resx`. When the meaning of an English text changes, its key is renamed,
so an old translation is not shown for a new meaning.

## For developers

How to add a string to the code, and how the table works, is in the
"Localization" section of [`.claude/CLAUDE.md`](../../.claude/CLAUDE.md) and in
[`docs/dev/design/localization.md`](design/localization.md).
