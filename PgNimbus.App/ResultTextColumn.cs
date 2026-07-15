using System.Diagnostics.CodeAnalysis;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Interactivity;
using Avalonia.Layout;
using PgNimbus.App.Converters;
using PgNimbus.Core.Schema;

namespace PgNimbus.App;

/// <summary>
/// A results-grid text column that dims cells whose underlying value is SQL
/// NULL, so the "NULL" placeholder text (see <see cref="RowIndexConverter"/>)
/// reads as a marker rather than data. The display element's DataContext is
/// the row array, so a plain style can't see the cell value - the opacity
/// binding has to be attached per generated element.
/// When the result maps to an editable table, the column also knows its
/// Postgres type (via <paramref name="editorMeta"/>) and generates a
/// type-appropriate cell editor: a dropdown of pg_enum labels for enum
/// columns, a checkbox for booleans, a calendar picker for date/timestamp.
/// Everything else keeps the stock TextBox. The editors are initialized from
/// the row value here; MainWindow reads the edited value back at commit time
/// (see its OnCellEditEnding), so none of them need a two-way binding.
/// </summary>
public sealed class ResultTextColumn : DataGridTextColumn
{
    private readonly int _index;
    private readonly ColumnDetail? _editorMeta;

    public ResultTextColumn(int index, ColumnDetail? editorMeta = null)
    {
        _index = index;
        _editorMeta = editorMeta;

        if (editorMeta?.Editor is ColumnValueEditor.Boolean or ColumnValueEditor.Enum
            or ColumnValueEditor.Date or ColumnValueEditor.Timestamp)
        {
            // The base class binds the column's display Binding (the row→text
            // converter) to BindingTarget on whatever editing element we
            // generate. The display text means nothing to these editors —
            // their state is set from the raw row value in
            // GenerateEditingElementDirect — so park that binding on Tag.
            BindingTarget = Control.TagProperty;
        }
    }

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

    protected override Control GenerateEditingElementDirect(DataGridCell cell, object dataItem)
    {
        var value = dataItem is object?[] row && _index < row.Length ? row[_index] : null;

        switch (_editorMeta?.Editor)
        {
            case ColumnValueEditor.Boolean:
                // A NULL cell starts unchecked-indeterminate; committing with
                // no click writes nothing (MainWindow skips a null IsChecked).
                return new CheckBox
                {
                    IsChecked = value as bool?,
                    HorizontalAlignment = HorizontalAlignment.Center,
                };

            case ColumnValueEditor.Enum:
                return new ComboBox
                {
                    ItemsSource = _editorMeta.EnumLabels,
                    SelectedItem = value?.ToString(),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                };

            case ColumnValueEditor.Date:
                return new CalendarDatePicker
                {
                    SelectedDate = ToDateTime(value),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                };

            case ColumnValueEditor.Timestamp:
            {
                var timestamp = ToDateTime(value);
                return new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 4,
                    Focusable = true,
                    Children =
                    {
                        new CalendarDatePicker { SelectedDate = timestamp?.Date },
                        new TextBox
                        {
                            Text = timestamp?.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
                            PlaceholderText = "HH:MM:SS",
                            MinWidth = 84,
                        },
                    },
                };
            }

            default:
                return base.GenerateEditingElementDirect(cell, dataItem);
        }
    }

    protected override object? PrepareCellForEdit(Control editingElement, RoutedEventArgs editingEventArgs)
        // The TextBox path keeps the stock prepare (select-all etc.); the
        // typed editors were fully initialized at generate time, and with
        // one-way column bindings there is no unedited value to hand back.
        => editingElement is TextBox ? base.PrepareCellForEdit(editingElement, editingEventArgs) : null;

    protected override void CancelCellEdit(Control editingElement, object uneditedValue)
    {
        if (editingElement is TextBox)
        {
            base.CancelCellEdit(editingElement, uneditedValue);
        }

        // Typed editors never wrote into the row (their value is read only at
        // commit), so cancelling has nothing to restore.
    }

    private static DateTime? ToDateTime(object? value) => value switch
    {
        DateTime dateTime => dateTime,
        DateOnly dateOnly => dateOnly.ToDateTime(TimeOnly.MinValue),
        DateTimeOffset dateTimeOffset => dateTimeOffset.DateTime,
        _ => null,
    };
}
