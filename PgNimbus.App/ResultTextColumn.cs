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
