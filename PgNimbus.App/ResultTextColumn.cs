using System.Diagnostics.CodeAnalysis;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using PgNimbus.App.Converters;

namespace PgNimbus.App;

/// <summary>
/// A results-grid text column that dims cells whose underlying value is SQL
/// NULL, so the "NULL" placeholder text (see <see cref="RowIndexConverter"/>)
/// reads as a marker rather than data. The display element's DataContext is
/// the row array, so a plain style can't see the cell value - the opacity
/// binding has to be attached per generated element.
/// </summary>
public sealed class ResultTextColumn : DataGridTextColumn
{
    private readonly int _index;

    public ResultTextColumn(int index) => _index = index;

    // The Binding has an empty Path - it passes the row array straight to the
    // converter and never resolves a member by name, so the reflection/dynamic
    // code the analyzers warn about is never exercised. Safe under trimming/AOT.
    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "Pathless binding uses a converter only; no reflection member access.")]
    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "Pathless binding uses a converter only; no dynamic code.")]
    protected override Control GenerateElement(DataGridCell cell, object dataItem)
    {
        var element = base.GenerateElement(cell, dataItem);
        element.Bind(Visual.OpacityProperty, new Binding
        {
            Converter = new NullCellOpacityConverter(_index),
        });

        return element;
    }
}
