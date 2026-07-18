using System.Reflection;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace PgNimbus.App.Views;

/// <summary>
/// The About box ("About pgNimbus" in the macOS app menu, and anywhere else
/// that wants it): app name, release version, license. Same version source as
/// the connection dialog's footer — the InformationalVersion the release
/// pipeline embeds, stripped of its "+&lt;git-sha&gt;" build metadata.
/// </summary>
public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        ThemedWindowChrome.Attach(this);

        var assembly = Assembly.GetEntryAssembly();
        var version = assembly?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            .Split('+')[0] ?? "0.0.0";
        VersionText.Text = $"Version {version}";
        var copyright = assembly?.GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright;
        CopyrightText.Text = string.IsNullOrEmpty(copyright) ? "MIT License" : $"{copyright} · MIT License";

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close();
            }
        };
    }

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
