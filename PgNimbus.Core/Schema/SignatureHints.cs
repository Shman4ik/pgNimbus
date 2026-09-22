using PgNimbus.Core.Text;

namespace PgNimbus.Core.Schema;

/// <summary>
/// One overload as the argument hint shows it: its parameters, which one the
/// caret is in (-1 when the call has run past this overload's last one), and
/// what it returns.
/// </summary>
public sealed record SignatureHint(string Schema, string Name, IReadOnlyList<SqlParameter> Parameters, int ActiveParameter, string ReturnType);

/// <summary>
/// Picks the overloads to show for a call and the parameter to highlight in
/// each. Pure, so the argument hint's behaviour is unit-tested here rather
/// than through a popup.
/// </summary>
public static class SignatureHints
{
    /// <summary>
    /// The overloads of the called function that can still take the argument
    /// the caret is in — those with enough parameters, or a VARIADIC last one —
    /// each with that parameter marked; a named argument (<c>arg =&gt; …</c>)
    /// marks the parameter of that name instead. When no overload fits, every
    /// one is returned unmarked rather than hiding the hint: the server may
    /// still accept the call through a default or a cast this can't see.
    /// </summary>
    public static IReadOnlyList<SignatureHint> For(SqlCallSite site, IEnumerable<(string Schema, FunctionInfo Function)> overloads)
    {
        var all = overloads
            .Where(o => o.Function.Kind != 'p')
            .Select(o => (o.Schema, o.Function, Parameters: SqlParameters.Parse(o.Function.Arguments)))
            .ToList();

        var fitting = new List<SignatureHint>();
        foreach (var (schema, function, parameters) in all)
        {
            var active = ActiveParameter(site, parameters);
            if (active >= 0)
            {
                fitting.Add(new SignatureHint(schema, function.Name, parameters, active, function.ReturnType));
            }
        }

        return fitting.Count > 0
            ? fitting
            : [.. all.Select(o => new SignatureHint(o.Schema, o.Function.Name, o.Parameters, -1, o.Function.ReturnType))];
    }

    private static int ActiveParameter(SqlCallSite site, IReadOnlyList<SqlParameter> parameters)
    {
        if (site.ArgumentName is { } name)
        {
            for (var i = 0; i < parameters.Count; i++)
            {
                if (parameters[i].Name == name)
                {
                    return i;
                }
            }

            return -1;
        }

        if (site.ArgumentIndex < parameters.Count)
        {
            return site.ArgumentIndex;
        }

        return parameters.Count > 0 && parameters[^1].IsVariadic ? parameters.Count - 1 : -1;
    }
}
