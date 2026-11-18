using PgNimbus.App.Localization;
using PgNimbus.App.ViewModels;
using PgNimbus.Core.Backup;
using PgNimbus.Core.Localization;
using PgNimbus.Screenshot;

namespace PgNimbus.App.Tests;

/// <summary>
/// What the 2026-11 release walk in Russian and Simplified Chinese found in code
/// rather than in the translations: the face Chinese is drawn in, the palette's
/// search in a translated interface, and the Settings backup card's status lines.
/// The tests that switch the process-wide language are <c>[NotInParallel]</c> and
/// put English back.
/// </summary>
public class TranslatedTextLogicTests
{
    [Test]
    [NotInParallel]
    public async Task Simplified_Chinese_names_its_face_for_Han_text_and_follows_a_switch()
    {
        await Ui.Run(async () =>
        {
            // English: no fallback at all, so an English frame is drawn as before.
            await Assert.That(LanguageFonts.Current).IsEmpty();
            try
            {
                Localizer.Load("zh-Hans");

                // One fallback, tried before the system's, for the CJK ranges only:
                // Latin and Cyrillic keep the face in use.
                var fallback = LanguageFonts.Current.Single();
                await Assert.That(fallback.FontFamily.Name).IsEqualTo(LanguageFonts.FaceFor("zh-Hans")!);
                foreach (var c in "。、（），：编辑器「」")
                {
                    await Assert.That(fallback.UnicodeRange.IsInRange(c)).IsTrue();
                }

                foreach (var c in "Aé Ж—…→⌘")
                {
                    await Assert.That(fallback.UnicodeRange.IsInRange(c)).IsFalse();
                }

                Localizer.Load("ru");
                await Assert.That(LanguageFonts.Current).IsEmpty();
                Localizer.Load("zh-Hans");
                await Assert.That(LanguageFonts.Current.Count).IsEqualTo(1);
            }
            finally
            {
                Localizer.Load(Localizer.English);
            }

            await Assert.That(LanguageFonts.Current).IsEmpty();
        });
    }

    [Test]
    public async Task Each_platform_names_its_own_simplified_face()
    {
        var face = LanguageFonts.FaceFor("zh-Hans");
        await Assert.That(face).IsEqualTo(
            OperatingSystem.IsWindows() ? "Microsoft YaHei UI" : OperatingSystem.IsMacOS() ? "PingFang SC" : "Noto Sans CJK SC");
        await Assert.That(LanguageFonts.FaceFor("ru")).IsNull();
        await Assert.That(LanguageFonts.FaceFor(Localizer.English)).IsNull();
    }

    [Test]
    [NotInParallel]
    public async Task The_palette_finds_a_translated_command_by_its_search_words_and_its_English_title()
    {
        await Ui.Run(async () =>
        {
            var (window, vm) = Scenarios.Shell();
            Ui.Show(window);
            try
            {
                Localizer.Load("zh-Hans");
                var title = Strings.Command_NotifyMonitor;

                // Not awaited: the palette's table fetch never returns against the
                // fixture data source (see ShellTests).
                _ = vm.OpenCommandPaletteAsync();
                foreach (var search in new[] { "通知", "监听", "notify monitor", "notifications" })
                {
                    vm.CommandPalette.SearchText = search;
                    Ui.Settle();
                    await Assert.That(vm.CommandPalette.Results.Select(r => r.Title)).Contains(title);
                }

                vm.CommandPalette.CloseCommand.Execute(null);
            }
            finally
            {
                Localizer.Load(Localizer.English);
                window.Close();
            }
        });
    }

    [Test]
    public async Task The_backup_card_says_found_automatically_only_when_something_was_found()
    {
        await Ui.Run(async () =>
        {
            var found = new PgToolScan([new PgToolInstall("/usr/lib/postgresql/17/bin", new PgVersion(17, 2), null, [])], [], null);
            var missing = new PgToolScan([], [], null);
            var tools = new PgToolStatusViewModel(PgTool.PgDump, scan: _ => Task.FromResult(missing));

            // Searching: neither line yet.
            await Assert.That(tools.ShowsFolderLabel).IsFalse();

            tools.Apply(missing);
            await Assert.That(tools.StatusLine).IsEqualTo("pg_dump not found");
            await Assert.That(tools.ShowsFolderLabel).IsFalse();

            tools.Apply(found);
            await Assert.That(tools.FolderLabel).IsEqualTo("Found automatically");
            await Assert.That(tools.ShowsFolderLabel).IsTrue();

            // A folder the user chose is named whether or not it held the programs.
            tools.Apply(missing with { ConfiguredDirectory = "/opt/pg" });
            await Assert.That(tools.FolderLabel).IsEqualTo("From the folder you chose: /opt/pg");
            await Assert.That(tools.ShowsFolderLabel).IsTrue();
        });
    }
}
