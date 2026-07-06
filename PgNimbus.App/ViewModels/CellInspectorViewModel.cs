using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace PgNimbus.App.ViewModels;

/// <summary>
/// Backs the results-grid cell inspector overlay: a read-only, copyable detail
/// view of one cell's full value, opened when a <c>text</c>/<c>jsonb</c> value
/// is too long to read inline. <c>jsonb</c>/<c>json</c> values are
/// pretty-printed; everything else is shown as its plain string form.
/// </summary>
public sealed partial class CellInspectorViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _isOpen;

    [ObservableProperty]
    private string _columnName = string.Empty;

    [ObservableProperty]
    private string _displayText = string.Empty;

    /// <summary>True when <see cref="DisplayText"/> is pretty-printed JSON - drives monospace display in the view.</summary>
    [ObservableProperty]
    private bool _isJson;

    public void Open(string columnName, object? value)
    {
        ColumnName = columnName;
        (DisplayText, IsJson) = Format(value);
        IsOpen = true;
    }

    [RelayCommand]
    private void Close() => IsOpen = false;

    private static (string Text, bool IsJson) Format(object? value)
    {
        var text = value switch
        {
            null => "NULL",
            byte[] bytes => $"\\x{Convert.ToHexString(bytes)}",
            _ => value.ToString() ?? string.Empty,
        };

        return TryPrettyPrintJson(text, out var pretty) ? (pretty, true) : (text, false);
    }

    // Npgsql returns json/jsonb columns as their raw text by default, so the
    // only signal that a value is JSON (rather than, say, a plain string that
    // happens to start with '{') is that it actually parses as one.
    private static bool TryPrettyPrintJson(string text, out string pretty)
    {
        pretty = text;

        var trimmed = text.AsSpan().Trim();
        if (trimmed.Length == 0 || (trimmed[0] != '{' && trimmed[0] != '['))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            pretty = JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true });
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
