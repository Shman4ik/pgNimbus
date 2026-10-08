using System.Globalization;
using PgNimbus.App.Converters;
using PgNimbus.App.ViewModels;

namespace PgNimbus.App.Tests;

/// <summary>
/// The grid uses its display text to seed the inline editor, which parses
/// numbers invariantly. A decimal comma must never turn 55.75 into 5575.
/// </summary>
public class NumericCellTextTests
{
    [Test]
    public async Task Numbers_display_and_read_back_independently_of_the_region()
    {
        (object Value, string Text)[] cases =
        [
            ((byte)255, "255"),
            ((sbyte)-128, "-128"),
            (short.MinValue, "-32768"),
            (ushort.MaxValue, "65535"),
            (int.MinValue, "-2147483648"),
            (uint.MaxValue, "4294967295"),
            (long.MinValue, "-9223372036854775808"),
            (ulong.MaxValue, "18446744073709551615"),
            (55.75m, "55.75"),
            (-55.7500m, "-55.7500"),
            (decimal.MaxValue, "79228162514264337593543950335"),
            (55.75f, "55.75"),
            (-55.75d, "-55.75"),
            (1E-200d, "1E-200"),
            (double.MaxValue, "1.7976931348623157E+308"),
        ];

        var previous = CultureInfo.CurrentCulture;
        try
        {
            foreach (var culture in new[] { "cs-CZ", "de-DE", "en-US", "ar-SA" })
            {
                CultureInfo.CurrentCulture = new CultureInfo(culture);
                foreach (var (value, expected) in cases)
                {
                    var text = Convert.ToString(CellText.Preview(value), CultureInfo.CurrentCulture);
                    await Assert.That(text).IsEqualTo(expected);
                    await Assert.That(CellText.Full(value)).IsEqualTo(expected);
                    await Assert.That(CellText.IsShortened(value)).IsFalse();
                    var parsed = QueryViewModel.ParseEditedText(text!, value.GetType(), null);
                    await Assert.That(parsed).IsEqualTo(value);
                }
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Test]
    public async Task Floating_point_special_values_keep_invariant_spellings()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            // Postgres's own spellings for float4/float8, which it also accepts on input.
            (object Value, string Text)[] cases =
            [
                (float.NaN, "NaN"),
                (double.NaN, "NaN"),
                (float.PositiveInfinity, "Infinity"),
                (double.PositiveInfinity, "Infinity"),
                (float.NegativeInfinity, "-Infinity"),
                (double.NegativeInfinity, "-Infinity"),
            ];
            foreach (var (value, expected) in cases)
            {
                await Assert.That(Convert.ToString(CellText.Preview(value), CultureInfo.CurrentCulture)).IsEqualTo(expected);
                await Assert.That(CellText.Full(value)).IsEqualTo(expected);
                var parsed = QueryViewModel.ParseEditedText(expected, value.GetType(), null);
                await Assert.That(parsed).IsEqualTo(value);
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Test]
    public async Task Non_numeric_values_keep_their_existing_display_contract()
    {
        await Assert.That(CellText.Preview(null)).IsEqualTo(CellText.NullPlaceholder);
        await Assert.That(CellText.Preview(true) is true).IsTrue();
        await Assert.That(CellText.Preview("55,75")).IsEqualTo("55,75");
    }
}
