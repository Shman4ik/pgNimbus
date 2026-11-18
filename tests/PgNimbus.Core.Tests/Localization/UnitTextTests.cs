using PgNimbus.Core.Commands;
using PgNimbus.Core.Localization;
using PgNimbus.Core.QueryLab;
using PgNimbus.Core.Text;

namespace PgNimbus.Core.Tests.Localization;

/// <summary>
/// Text Core writes for the screen in the interface language: the units of sizes and
/// durations (Russian writes мс and МБ; the numbers stay invariant), Query Lab's
/// row-count note, and the words the palette finds a command by. Each test switches
/// the process-wide language, so it is <c>[NotInParallel]</c> and puts English back.
/// </summary>
public class UnitTextTests
{
    [Test]
    [NotInParallel]
    public async Task Russian_writes_its_own_units_around_invariant_numbers()
    {
        try
        {
            Localizer.Load("ru");

            await Assert.That(ByteSize.Format(1)).IsEqualTo("1 байт");
            await Assert.That(ByteSize.Format(2)).IsEqualTo("2 байта");
            await Assert.That(ByteSize.Format(512)).IsEqualTo("512 байт");
            await Assert.That(ByteSize.Format(1536)).IsEqualTo("1.5 КБ");
            await Assert.That(ByteSize.Format(67_738_829)).IsEqualTo("64.6 МБ");
            await Assert.That(ByteSize.Format(1_073_741_824)).IsEqualTo("1.0 ГБ");

            await Assert.That(DurationText.Format(12.4)).IsEqualTo("12 мс");
            await Assert.That(DurationText.Format(3300)).IsEqualTo("3.3 с");
            await Assert.That(DurationText.Format(90_000)).IsEqualTo("1.5 мин");
            await Assert.That(PlanMetricRow.Format(1025, PlanMetricUnit.Milliseconds)).IsEqualTo("1,025 мс");
            await Assert.That(PlanMetricRow.Format(20_900, PlanMetricUnit.Milliseconds)).IsEqualTo("20.9 с");

            // A generated script's comment is not translated.
            await Assert.That(DurationText.FormatEnglish(12.4)).IsEqualTo("12 ms");
        }
        finally
        {
            Localizer.Load(Localizer.English);
        }
    }

    [Test]
    [NotInParallel]
    public async Task Chinese_keeps_the_latin_units()
    {
        try
        {
            Localizer.Load("zh-Hans");

            await Assert.That(ByteSize.Format(1536)).IsEqualTo("1.5 KB");
            await Assert.That(DurationText.Format(12.4)).IsEqualTo("12 ms");
            await Assert.That(PlanMetricRow.Format(22, PlanMetricUnit.Milliseconds)).IsEqualTo("22 ms");
        }
        finally
        {
            Localizer.Load(Localizer.English);
        }
    }

    [Test]
    [NotInParallel]
    public async Task The_row_count_note_reads_right_for_one_row_and_several()
    {
        await Assert.That(Strings.QueryLabCore_RowCountsDetail(1, 3)).StartsWith("The two runs returned different numbers of rows (before: 1, after: 3)");
        try
        {
            Localizer.Load("ru");

            // It used to say "вернул 1 строк": one plural form for two counts.
            await Assert.That(Strings.QueryLabCore_RowCountsDetail(1, 3)).StartsWith("Запуски вернули разное число строк («До»: 1, «После»: 3)");
        }
        finally
        {
            Localizer.Load(Localizer.English);
        }
    }

    [Test]
    [NotInParallel]
    public async Task A_command_is_found_by_its_search_words_and_its_english_title()
    {
        try
        {
            Localizer.Load("zh-Hans");
            var notify = CommandCatalog.Get(CommandId.NotifyMonitor);

            await Assert.That(notify.Title).IsEqualTo("LISTEN / NOTIFY 监视器");
            await Assert.That(notify.SearchAliases).Contains("LISTEN / NOTIFY monitor");
            await Assert.That(notify.SearchAliases.Any(a => FuzzyMatcher.Score(a, "通知") is not null)).IsTrue();
            await Assert.That(notify.SearchAliases.Any(a => FuzzyMatcher.Score(a, "监听") is not null)).IsTrue();
            await Assert.That(notify.SearchAliases.Any(a => FuzzyMatcher.Score(a, "notifications") is not null)).IsTrue();

            // Every command carries its English title while the interface is translated.
            var run = CommandCatalog.Get(CommandId.Run);
            await Assert.That(run.SearchAliases).Contains(Strings.EnglishOf(run.TitleKey));
        }
        finally
        {
            Localizer.Load(Localizer.English);
        }

        // In English the title is not repeated as an alias.
        await Assert.That(CommandCatalog.Get(CommandId.Run).SearchAliases).IsEmpty();
    }
}
