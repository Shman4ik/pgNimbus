using System.Collections;

namespace PgNimbus.Core.Query;

/// <summary>Which limit cut a result short.</summary>
public enum ResultCap
{
    /// <summary>Nothing: every row is here.</summary>
    None,

    /// <summary>The statement's own row cap.</summary>
    Rows,

    /// <summary>The statement's own byte budget (<see cref="ResultBudget.EstimateRow"/>).</summary>
    Bytes,

    /// <summary>
    /// A budget shared by several statements (a script's sections) ran out; this
    /// statement kept what fitted and read the rest without keeping it.
    /// </summary>
    Shared,
}

/// <summary>
/// How many rows, and roughly how many bytes of them, a result may hold in memory.
/// Rows were the only bound: <c>SELECT *</c> over 37 KB jsonb cells is several GB
/// at the 100,000-row cap, and one <c>repeat('x', 500000000)</c> is a gigabyte
/// (security audit 2026-09, finding 16). One instance is shared by every statement
/// of a script, so the sections together hold no more than one run can. Not
/// thread-safe: a script's statements run one after another.
/// </summary>
public sealed class ResultBudget(int maxRows, long maxBytes)
{
    public int MaxRows { get; } = maxRows;

    public long MaxBytes { get; } = maxBytes;

    public int Rows { get; private set; }

    public long Bytes { get; private set; }

    /// <summary>Why the last refused row was refused; <see cref="ResultCap.None"/> until one is.</summary>
    public ResultCap Exhausted { get; private set; }

    /// <summary>Charges one row of <paramref name="bytes"/>; false, charging nothing, when it doesn't fit.</summary>
    public bool TryTake(long bytes)
    {
        if (Rows >= MaxRows)
        {
            Exhausted = ResultCap.Rows;
            return false;
        }

        if (Bytes + bytes > MaxBytes)
        {
            Exhausted = ResultCap.Bytes;
            return false;
        }

        Rows++;
        Bytes += bytes;
        return true;
    }

    // Object header + array length, and a reference slot per cell.
    private const long RowOverhead = 32;
    private const long CellSlot = 8;

    // A boxed scalar (int, DateTime, Guid, decimal …): header plus the value.
    private const long BoxedValue = 24;

    // A string or array header, before its contents.
    private const long ObjectHeader = 24;

    /// <summary>
    /// Roughly what one materialized row costs on the managed heap. An estimate, not
    /// a measurement: it has to be cheap enough to run per row as batches arrive, and
    /// it only has to be right about the rows that matter — the ones with large text,
    /// json, bytea and array values, which it counts by length. Strings are UTF-16.
    /// </summary>
    public static long EstimateRow(object?[] row)
    {
        var bytes = RowOverhead + (row.Length * CellSlot);
        foreach (var value in row)
        {
            bytes += EstimateValue(value);
        }

        return bytes;
    }

    private static long EstimateValue(object? value)
    {
        switch (value)
        {
            case null or DBNull:
                return 0;
            case string s:
                return ObjectHeader + (2L * s.Length);
            case byte[] b:
                return ObjectHeader + b.LongLength;
            case BitArray bits:
                return ObjectHeader + ObjectHeader + (bits.Length / 8);
            case Array array:
            {
                // Postgres arrays arrive as (possibly multi-dimensional) CLR arrays of
                // boxed or string elements; never jagged, so one level of walking reads
                // every element.
                var bytes = ObjectHeader + (array.LongLength * CellSlot);
                foreach (var element in array)
                {
                    bytes += element is Array inner ? ObjectHeader + (inner.LongLength * CellSlot) : EstimateValue(element);
                }

                return bytes;
            }

            default:
                return BoxedValue;
        }
    }
}
