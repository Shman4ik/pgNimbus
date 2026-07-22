using System.Globalization;
using Avalonia.Data.Converters;
using PgNimbus.Core.Schema;

namespace PgNimbus.App.Converters;

/// <summary>
/// Extracts one cell from an <c>object?[]</c> result row. The results grid
/// binds each column to the row itself (empty path) with one of these instead
/// of a reflection path like "[3]": indexer-path bindings need dynamic code,
/// which breaks under NativeAOT/trimming (IL2026/IL3050), while an empty-path
/// binding plus converter is reflection-free.
/// SQL NULL converts to a "NULL" placeholder (dimmed via
/// <see cref="NullCellOpacityConverter"/>) so it's distinguishable from an
/// empty string; MainWindow's cell-edit preparation clears the placeholder
/// out of the editor so it can't be committed back as a literal string.
/// </summary>
public sealed class RowIndexConverter : IValueConverter
{
    public const string NullPlaceholder = "NULL";

    private readonly int _index;

    public RowIndexConverter(int index) => _index = index;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is object?[] row && _index < row.Length
            ? row[_index] switch
            {
                null => NullPlaceholder,
                // bytea arrives as byte[]; its default ToString is the useless
                // "System.Byte[]". Show a capped \x-hex preview (the same shape
                // the cell inspector uses, which carries the full value) rather
                // than materializing megabytes of hex for a large blob inline.
                byte[] bytes => FormatByteaPreview(bytes),
                // bit/varbit arrive as a BitArray, whose default ToString is
                // "System.Collections.BitArray". Render the bit string ("10110001",
                // most-significant bit first, matching Postgres) so it reads and,
                // for an editable table, round-trips through CAST(text AS bit(n)).
                System.Collections.BitArray bits => FormatBits(bits),
                // Array columns render in Postgres's literal syntax ("{a,b}")
                // instead of the CLR default ("System.String[]") — readable,
                // and editable in place since the cell editor pre-fills from
                // this text and the edit pipeline casts it back server-side.
                Array array => PgValueSyntax.FormatArray(array),
                // hstore arrives as a Dictionary<string,string>, whose default
                // ToString is the CLR type name. Render the Postgres literal
                // ("k"=>"v") so it reads in any result set — browse mode already
                // re-requests it as text, but a hand-written SELECT gets the raw
                // dictionary, and without this it showed the type name.
                System.Collections.IDictionary map => PgValueSyntax.FormatHstore(map),
                var cell => cell,
            }
            : null;

    /// <summary>Bytes shown before a bytea preview is truncated — enough to read a magic number, not a whole blob.</summary>
    private const int ByteaPreviewBytes = 24;

    private static string FormatByteaPreview(byte[] bytes) =>
        bytes.Length <= ByteaPreviewBytes
            ? "\\x" + System.Convert.ToHexString(bytes)
            : "\\x" + System.Convert.ToHexString(bytes.AsSpan(0, ByteaPreviewBytes)) + "…";

    private static string FormatBits(System.Collections.BitArray bits)
    {
        var chars = new char[bits.Count];
        for (var i = 0; i < bits.Count; i++)
        {
            chars[i] = bits[i] ? '1' : '0';
        }

        return new string(chars);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Renders a boolean cell as a ✓/✗ glyph instead of the literal "true"/"false"
/// text, so a boolean column reads at a glance. SQL NULL keeps the same "NULL"
/// placeholder every other column uses (dimmed via <see cref="NullCellOpacityConverter"/>).
/// </summary>
public sealed class BoolCellGlyphConverter : IValueConverter
{
    public const string True = "✓";  // ✓
    public const string False = "✗";  // ✗

    private readonly int _index;

    public BoolCellGlyphConverter(int index) => _index = index;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is object?[] row && _index < row.Length
            ? row[_index] switch
            {
                null => RowIndexConverter.NullPlaceholder,
                bool b => b ? True : False,
                // A boolean column should only ever hold bool/null, but never
                // throw on a surprise value — show its text form.
                var other => other.ToString(),
            }
            : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Dims cells whose underlying value is SQL NULL, so the "NULL" placeholder reads as a marker, not data.</summary>
public sealed class NullCellOpacityConverter : IValueConverter
{
    private readonly int _index;

    public NullCellOpacityConverter(int index) => _index = index;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is object?[] row && _index < row.Length && row[_index] is null ? 0.4 : 1.0;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
