namespace PgNimbus.App.ViewModels;

/// <summary>The name a naming dialog opens with, shared by saved queries and saved plans.</summary>
public static class SaveQueryNames
{
    /// <summary>
    /// <paramref name="title"/> trimmed, or with a number (" 2", " 3", …) when that name is
    /// taken: the dialog turns a taken name into Replace, and Enter on a suggestion must
    /// never overwrite what is already saved.
    /// </summary>
    public static string Suggest(string title, Func<string, bool> taken)
    {
        var name = title.Trim();
        if (name.Length == 0 || !taken(name))
        {
            return name;
        }

        for (var n = 2; ; n++)
        {
            var candidate = $"{name} {n}";
            if (!taken(candidate))
            {
                return candidate;
            }
        }
    }
}
