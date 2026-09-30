using System;
using System.Linq;
using Avalonia.Data.Converters;
using PgNimbus.Core.Query;

namespace PgNimbus.App.Converters;

/// <summary>
/// Binds a run-history row to <see cref="HistoryLabel"/>: the statement on one
/// line, and the "when · how long · what came back" line under it.
/// </summary>
public static class HistoryText
{
    /// <summary>
    /// The clock the time labels are relative to. The screenshot harness pins
    /// it to its fixture's "now", so "Yesterday" in a baseline doesn't depend
    /// on the day the baseline was rendered.
    /// </summary>
    public static Func<DateTimeOffset> Now { get; set; } = () => DateTimeOffset.Now;

    public static readonly IValueConverter OneLine = new FuncValueConverter<string?, string>(sql =>
        sql is null ? string.Empty : HistoryLabel.OneLine(sql));

    /// <summary>Values: the entry, then the current connection's label.</summary>
    public static readonly IMultiValueConverter Meta = new FuncMultiValueConverter<object?, string>(values =>
    {
        var list = values.ToList();
        return list.Count > 0 && list[0] is QueryHistoryEntry entry
            ? HistoryLabel.Meta(entry, list.Count > 1 ? list[1] as string : null, Now())
            : string.Empty;
    });

    public static readonly IValueConverter Detail = new FuncValueConverter<QueryHistoryEntry?, string>(entry =>
        entry is null ? string.Empty : HistoryLabel.Detail(entry, Now()));
}
