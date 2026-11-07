using System.Reflection;
using Avalonia.Controls;
using Avalonia.Interactivity;
using PgNimbus.App.Platform;

namespace PgNimbus.App.Views;

/// <summary>
/// The About box: app name, release version, license. Same version source as the
/// connection dialog's footer — the <c>InformationalVersion</c> the release pipeline
/// embeds, stripped of its "+&lt;git-sha&gt;" build metadata.
/// <para>
/// Hosted in the shell's About <c>OverlayPanel</c> rather than in a window of its own,
/// so it is reached the same way in both Nimbus apps. The macOS app menu's "About
/// pgNimbus" opens the front main window's overlay (see <c>App.ShowAbout</c>) rather
/// than a free-standing box — which also means the app menu item does nothing while
/// only the connection dialog is up, exactly like "Settings…" beside it.
/// </para>
/// </summary>
public partial class AboutView : UserControl
{
    public AboutView()
    {
        InitializeComponent();

        var assembly = Assembly.GetEntryAssembly();
        var version = assembly?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            .Split('+')[0] ?? "0.0.0";
        VersionText.Text = $"Version {version}";
        var copyright = assembly?.GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright;
        CopyrightText.Text = string.IsNullOrEmpty(copyright) ? "MIT License" : $"{copyright} · MIT License";
        ShowsStoreLink = StoreListing.IsPackaged;
    }

    /// <summary>
    /// Whether the "Rate pgNimbus in the Microsoft Store" link shows: only in the
    /// Store MSIX, read from the process itself rather than a view model because
    /// this box is hosted by both the shell and the connection dialog. Settable so
    /// the headless tests can show it.
    /// </summary>
    public bool ShowsStoreLink
    {
        get => RateInStoreLink.IsVisible;
        set => RateInStoreLink.IsVisible = value;
    }

    /// <summary>What the link opens; a seam so a test can click it without starting the Store.</summary>
    public Action OpenStoreReviewPage { get; set; } = StoreListing.OpenReviewPage;

    private void OnRateInStoreClick(object? sender, RoutedEventArgs e) => OpenStoreReviewPage();

    private void OnGitHubClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo("https://github.com/Shman4ik/pgNimbus") { UseShellExecute = true });
        }
        catch
        {
            // No browser to hand off to is not worth crashing the About box.
        }
    }
}
