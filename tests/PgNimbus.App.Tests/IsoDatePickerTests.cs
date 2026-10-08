using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using PgNimbus.App.Converters;
using PgNimbus.App.ViewModels;
using PgNimbus.App.Views;
using PgNimbus.Core.Schema;

namespace PgNimbus.App.Tests;

/// <summary>
/// The date pickers (row details, Add row, the filter editor, the grid's inline
/// editors, the role dialog) show and read ISO dates, as the grid writes them,
/// whatever the culture (#353). They used the culture's short pattern:
/// <c>MM/dd/yyyy</c> in the shipped app, <c>08.10.2026</c> in a Czech test host.
/// </summary>
public class IsoDatePickerTests
{
    private static readonly string[] Cultures = ["cs-CZ", "de-DE", "en-US", "ar-SA"];

    [Test]
    public async Task Row_details_show_a_date_as_the_grid_writes_it()
    {
        await Ui.Run(async () =>
        {
            var shown = new List<string>();
            var previous = CultureInfo.CurrentCulture;
            try
            {
                foreach (var culture in Cultures)
                {
                    CultureInfo.CurrentCulture = new CultureInfo(culture);

                    var date = Field("placed_on", "date", ColumnValueEditor.Date);
                    date.Seed(new DateOnly(2026, 10, 8));
                    var stamp = Field("placed_at", "timestamp without time zone", ColumnValueEditor.Timestamp);
                    stamp.Seed(new DateTime(2026, 10, 8, 14, 5, 0));

                    var (dateWindow, datePicker) = Host(date, "DateInput");
                    var (stampWindow, stampPicker) = Host(stamp, "StampDateInput");
                    shown.Add($"{culture} date picker {datePicker.Text}");
                    shown.Add($"{culture} timestamp picker {stampPicker.Text}");
                    shown.Add($"{culture} value {date.Value}");
                    dateWindow.Close();
                    stampWindow.Close();
                }
            }
            finally
            {
                CultureInfo.CurrentCulture = previous;
            }

            var expected = Cultures.SelectMany(c => new[]
            {
                $"{c} date picker 2026-10-08", $"{c} timestamp picker 2026-10-08", $"{c} value 2026-10-08",
            });
            await Assert.That(shown).IsEquivalentTo(expected);
        });
    }

    [Test]
    public async Task A_typed_ISO_date_is_read_and_anything_else_is_refused()
    {
        await Ui.Run(async () =>
        {
            var results = new List<(string Culture, DateTime? Typed, string? TypedText, DateTime? Refused, string? RefusedText)>();
            var previous = CultureInfo.CurrentCulture;
            try
            {
                foreach (var culture in Cultures)
                {
                    CultureInfo.CurrentCulture = new CultureInfo(culture);

                    var field = Field("placed_on", "date", ColumnValueEditor.Date);
                    var (window, picker) = Host(field, "DateInput");

                    TypeInto(window, picker, "2026-1-8");
                    var typed = (field.DateValue, picker.Text);

                    // The culture's own short form, and the old US one: neither
                    // is read, and the date the picker had comes back.
                    TypeInto(window, picker, "08.10.2026");
                    TypeInto(window, picker, "10/08/2026");
                    results.Add((culture, typed.DateValue, typed.Text, field.DateValue, picker.Text));
                    window.Close();
                }
            }
            finally
            {
                CultureInfo.CurrentCulture = previous;
            }

            foreach (var (culture, typed, typedText, refused, refusedText) in results)
            {
                await Assert.That(typed).IsEqualTo(new DateTime(2026, 1, 8)).Because(culture);
                await Assert.That(typedText).IsEqualTo("2026-01-08").Because(culture);
                await Assert.That(refused).IsEqualTo(new DateTime(2026, 1, 8)).Because(culture);
                await Assert.That(refusedText).IsEqualTo("2026-01-08").Because(culture);
            }
        });
    }

    [Test]
    public async Task An_empty_picker_without_a_placeholder_names_the_ISO_form()
    {
        await Ui.Run(async () =>
        {
            // The grid's inline date editor is a bare picker like this one.
            var picker = new CalendarDatePicker();
            var window = new Window { Content = picker, Width = 300, Height = 120 };
            Ui.Show(window);

            var box = picker.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "PART_TextBox");
            await Assert.That(box.PlaceholderText).IsEqualTo($"<{IsoDate.Format}>");
            window.Close();
        });
    }

    private static NewRowField Field(string name, string dataType, ColumnValueEditor editor) =>
        NewRowField.For(new ColumnDetail(name, dataType, NotNull: false, IsPrimaryKey: false) { Editor = editor });

    private static (Window Window, CalendarDatePicker Picker) Host(NewRowField field, string pickerName)
    {
        var view = new ColumnValueEditorView { DataContext = field };
        var window = new Window { Content = view, Width = 360, Height = 160 };
        Ui.Show(window);
        return (window, view.GetVisualDescendants().OfType<CalendarDatePicker>().Single(p => p.Name == pickerName));
    }

    private static void TypeInto(Window window, CalendarDatePicker picker, string text)
    {
        var box = picker.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "PART_TextBox");
        box.Focus();
        box.SelectAll();
        Ui.Type(window, text);
        Ui.Press(window, Key.Enter);
    }
}
