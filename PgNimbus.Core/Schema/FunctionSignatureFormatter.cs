namespace PgNimbus.Core.Schema;

/// <summary>
/// Renders a <see cref="FunctionInfo"/> into the human-readable signature
/// completion shows as a candidate's description — "parameter hints" without
/// a separate floating popup: the arguments and return type ride the
/// existing tooltip panel next to the completion list.
/// </summary>
public static class FunctionSignatureFormatter
{
    /// <summary>
    /// "(customer_id integer, status text) → orders" for a function/window
    /// function/aggregate with a return type; "(id integer)" for a procedure
    /// (no return type — <see cref="FunctionInfo.ReturnType"/> is empty,
    /// since <c>pg_get_function_result</c> returns null for those); "()" for
    /// a no-argument callable.
    /// </summary>
    public static string Format(FunctionInfo info)
    {
        var args = info.Arguments.Length == 0 ? "()" : $"({info.Arguments})";
        return info.ReturnType.Length == 0 ? args : $"{args} → {info.ReturnType}";
    }

    /// <summary>The tooltip's leading label — what kind of callable this is, spelled out rather than the bare pg_proc.prokind letter.</summary>
    public static string KindLabel(FunctionInfo info) => info.Kind switch
    {
        'p' => "procedure",
        'a' => "aggregate",
        'w' => "window function",
        _ => "function",
    };
}
