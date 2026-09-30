using Avalonia.Controls;
using PgNimbus.App.Views;

namespace PgNimbus.App.Tests;

/// <summary>
/// A copy button acknowledges the click: the label turns into a check for a
/// moment without moving its neighbours, then comes back.
/// </summary>
public class CopyFeedbackTests
{
    [Test]
    public async Task Copy_button_shows_a_check_then_restores_its_label()
    {
        await Ui.Run(async () =>
        {
            var button = new Button { Content = "Copy" };
            var window = new Window { Content = new StackPanel { Children = { button } }, Width = 300, Height = 100 };
            Ui.Show(window);
            var width = button.Bounds.Width;

            CopyFeedback.Show(button);
            Ui.Settle();

            await Assert.That(button.Content).IsTypeOf<PathIcon>();
            await Assert.That(button.Bounds.Width).IsGreaterThanOrEqualTo(width);

            // A second click while the check is up restarts it rather than
            // capturing the check as the content to restore.
            CopyFeedback.Show(button);

            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (button.Content is not string && DateTime.UtcNow < deadline)
            {
                await Task.Delay(50);
                Ui.Settle(passes: 1);
            }

            await Assert.That(button.Content).IsEqualTo("Copy");
            await Assert.That(button.MinWidth).IsEqualTo(0d);

            window.Close();
            Ui.Settle();
        });
    }
}
