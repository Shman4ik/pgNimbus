using System.Reflection;
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

    /// <summary>
    /// A new application adds nothing to <see cref="Localizer.LanguageChanged"/>,
    /// a static event, which holds its handlers for the life of the process. The
    /// framework strings once added one per <see cref="App"/>, capturing it, and
    /// these tests make a new App per test: a full run kept ~570 alive, every
    /// switch rewrote their resources and walked their windows, and the
    /// pseudo-locale scan ran CI past its 30 minutes.
    /// </summary>
    [Test]
    public async Task A_new_application_adds_no_language_handler()
    {
        try
        {
            await Ui.Run(async () =>
            {
                var before = LanguageHandlers();
                new App().Initialize();
                new App().Initialize();

                await Assert.That(LanguageHandlers()).IsEqualTo(before);

                // And the running application's Fluent strings still follow a switch.
                Localizer.Load(Localizer.Pseudo);
                await Assert.That(Avalonia.Application.Current!.Resources["StringTextFlyoutCutText"])
                    .IsEqualTo(Strings.Get(StringKey.Framework_Cut));
                await Assert.That(Strings.Get(StringKey.Framework_Cut)).Contains("[");

                Localizer.Load(Localizer.English);
                await Assert.That(Avalonia.Application.Current!.Resources["StringTextFlyoutCutText"])
                    .IsEqualTo(Strings.Get(StringKey.Framework_Cut));
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

        static int LanguageHandlers() =>
            (typeof(Localizer).GetField(nameof(Localizer.LanguageChanged), BindingFlags.NonPublic | BindingFlags.Static)!
                .GetValue(null) as Delegate)?.GetInvocationList().Length ?? 0;
    }
}
