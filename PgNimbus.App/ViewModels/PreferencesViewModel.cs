using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace PgNimbus.App.ViewModels;

/// <summary>
/// Backs the preferences window. Every change applies immediately and persists
/// through the same per-setting App helpers the inline toggles use, so the
/// page and the rest of the UI can't disagree.
/// </summary>
public sealed partial class PreferencesViewModel : ObservableObject
{
    private readonly MainViewModel _main;

    /// <summary>0 = system (follow the OS), 1 = light, 2 = dark.</summary>
    [ObservableProperty]
    private int _themeIndex;

    /// <summary>0 = auto (Cmd on macOS, Ctrl elsewhere), 1 = always Ctrl, 2 = always Cmd.</summary>
    [ObservableProperty]
    private int _hotkeySchemeIndex;

    public PreferencesViewModel(MainViewModel main)
    {
        _main = main;
        var settings = App.LoadSettings();
        _themeIndex = settings.Theme switch { "light" => 1, "dark" => 2, _ => 0 };
        _hotkeySchemeIndex = settings.HotkeyScheme switch { "windows" => 1, "mac" => 2, _ => 0 };
        _main.PropertyChanged += OnMainPropertyChanged;
    }

    /// <summary>
    /// Proxies the main view-model's setting (rather than duplicating it) so
    /// its own persistence hook runs and the command-palette toggle and this
    /// checkbox stay in sync while the window is open.
    /// </summary>
    public bool AutoAliasTables
    {
        get => _main.AutoAliasTables;
        set => _main.AutoAliasTables = value;
    }

    /// <summary>Unhooks from the main view-model when the window closes.</summary>
    public void Detach() => _main.PropertyChanged -= OnMainPropertyChanged;

    private void OnMainPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.AutoAliasTables))
        {
            OnPropertyChanged(nameof(AutoAliasTables));
        }
    }

    partial void OnThemeIndexChanged(int value) =>
        App.SetTheme(value switch { 1 => "light", 2 => "dark", _ => "system" });

    partial void OnHotkeySchemeIndexChanged(int value) =>
        App.SetHotkeyScheme(value switch { 1 => "windows", 2 => "mac", _ => "auto" });
}
