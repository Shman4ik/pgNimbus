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
            // The full argument list when it was read: it keeps each DEFAULT,
            // which is how the hint shows that an argument can be left out.
            .Select(o => (o.Schema, o.Function, Parameters: SqlParameters.Parse(o.Function.FullArguments ?? o.Function.Arguments)))
            // The overloads over ordinary types before the polymorphic ones
            // (H02): upper(text) is what a person means, upper(anymultirange) the fallback.
            .OrderBy(o => o.Parameters.Count(p => IsPolymorphic(p.Text)))
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

    // A pseudo-type that stands for "any type": anyelement, anyarray,
    // anycompatiblerange, "any" …
    private static bool IsPolymorphic(string parameter)
    {
        var type = parameter.Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";
        return type.StartsWith("any", StringComparison.Ordinal) || type == "\"any\"";
    }

    /// <summary>
    /// The SQL forms of calls whose arguments aren't a comma list, and the
    /// variadic conditionals pg_proc doesn't hold (H01): <c>extract(field FROM
    /// source)</c>, <c>substring(string FROM start FOR count)</c>,
    /// <c>position(substring IN string)</c>, <c>trim(…)</c>,
    /// <c>overlay(…)</c>, <c>coalesce</c>, <c>greatest</c>, <c>least</c>,
    /// <c>nullif</c>. Shown before (or instead of) the catalog's overloads.
    /// </summary>
    public static IReadOnlyList<SignatureHint> SpecialForms(SqlCallSite site)
    {
        if (site.Name.Count != 1)
        {
            return [];
        }

        static SqlParameter P(string text, bool variadic = false) => new(text, null, variadic);
        var forms = site.Name[0] switch
        {
            "extract" => new[] { new[] { P("field FROM source") } },
            "substring" => [[P("string FROM start FOR count")], [P("string SIMILAR pattern ESCAPE escape")]],
            "position" => [[P("substring IN string")]],
            "trim" => [[P("[LEADING | TRAILING | BOTH] [characters] FROM string")]],
            "overlay" => [[P("string PLACING replacement FROM start FOR count")]],
            "coalesce" or "greatest" or "least" => [[P("value"), P("[, …]", variadic: true)]],
            "nullif" => [[P("value1"), P("value2")]],
            _ => [],
        };
        var returns = site.Name[0] switch
        {
            "extract" => "numeric",
            "substring" or "trim" or "overlay" => "text",
            "position" => "integer",
            _ => "",
        };

        return [.. forms.Select(parameters =>
        {
            // A form with no commas is one argument; the others count commas.
            var active = parameters.Length == 1 ? 0
                : site.ArgumentIndex < parameters.Length ? site.ArgumentIndex
                : parameters[^1].IsVariadic ? parameters.Length - 1 : -1;
            return new SignatureHint("pg_catalog", site.Name[0], parameters, active, returns);
        })];
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
