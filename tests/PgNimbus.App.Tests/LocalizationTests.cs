using Avalonia.Controls;
using Avalonia.VisualTree;
using PgNimbus.App.Views;
using PgNimbus.Core.Localization;
using PgNimbus.Screenshot;

namespace PgNimbus.App.Tests;

/// <summary>
/// The interface language changes while the app runs: <c>{l:Tr Key}</c> binds to
/// <see cref="Localizer.Observe(StringKey)"/>. The language is process-wide, so
/// every test here is <c>[NotInParallel]</c> (TUnit runs those after the parallel
/// tests, one at a time) and puts English back however it ends.
/// </summary>
[NotInParallel]
public class LocalizationTests
{
    [Test]
    public async Task Text_in_an_open_window_follows_a_change_of_language()
    {
        try
        {
            await Ui.Run(async () =>
            {
                var (window, vm) = Scenarios.Shell();
                Ui.Show(window);
                vm.IsAboutOpen = true;
                Ui.Settle();

                var about = window.GetVisualDescendants().OfType<AboutView>().Single();
                bool Shows(string text) => about.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == text);

                await Assert.That(Shows(Strings.About_Tagline)).IsTrue();
                await Assert.That(Strings.About_Tagline).IsEqualTo("A fast, open-source PostgreSQL client");

                // A collection between the binding and the change must not lose it:
                // the language list holds observers weakly.
                GC.Collect();
                GC.WaitForPendingFinalizers();

                Localizer.LoadTable("zh-Hans", new Dictionary<string, string>
                {
                    ["About_Tagline"] = "快速、开源的 PostgreSQL 客户端",
                });
                Ui.Settle();
                await Assert.That(Shows("快速、开源的 PostgreSQL 客户端")).IsTrue();

                Localizer.Load(Localizer.Pseudo);
                Ui.Settle();
                await Assert.That(Shows(Localizer.Pseudolocalize("A fast, open-source PostgreSQL client"))).IsTrue();

                Localizer.Load(Localizer.English);
                Ui.Settle();
                await Assert.That(Shows("A fast, open-source PostgreSQL client")).IsTrue();

                window.Close();
            });
        }
        finally
        {
            await Ui.Run(() =>
            {
                Localizer.Load(Localizer.English);
                return Task.CompletedTask;
            });
        }
    }
}
