namespace PgNimbus.Core.Json;

/// <summary>
/// One collapsible region of JSON text: an object or array from its opening
/// bracket to just past its closing one, spanning more than one line.
/// <see cref="Count"/> is its members or elements, for the folded label.
/// </summary>
public readonly record struct JsonFold(int Start, int End, bool IsObject, int Count)
{
    /// <summary>What the region reads as while folded, in the tree view's words.</summary>
    public string Title => IsObject
        ? Count == 1 ? "{ 1 field }" : $"{{ {Count} fields }}"
        : Count == 1 ? "[ 1 item ]" : $"[ {Count} items ]";
}

/// <summary>
/// Finds the foldable regions of JSON text for the cell inspector's editors.
/// One linear pass over the characters, bracket matching outside strings; it
/// never parses, so text being edited — unbalanced, half typed — still folds
/// wherever its brackets do match, and never throws.
/// </summary>
public static class JsonFolding
{
    /// <summary>Every multi-line object and array in <paramref name="text"/>, ordered by start.</summary>
    public static IReadOnlyList<JsonFold> Find(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        var folds = new List<JsonFold>();
        var open = new List<OpenBracket>();
        var inString = false;
        var line = 0;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\n')
            {
                line++;
                // JSON strings can't hold a raw newline, so one means a closing
                // quote is missing. Ending the string here keeps one stray quote
                // from switching folding off for the rest of the document.
                inString = false;
                continue;
            }

            if (inString)
            {
                if (c == '\\' && i + 1 < text.Length && text[i + 1] != '\n')
                {
                    i++;
                }
                else if (c == '"')
                {
                    inString = false;
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    inString = true;
                    MarkContent(open);
                    break;

                case '{':
                case '[':
                    MarkContent(open);
                    open.Add(new OpenBracket(i, line, c == '{'));
                    break;

                case '}':
                case ']':
                    var top = open.Count - 1;
                    if (top < 0 || open[top].IsObject != (c == '}'))
                    {
                        // Unbalanced: not a region anyone can mean, so no fold.
                        break;
                    }

                    var region = open[top];
                    open.RemoveAt(top);
                    if (line > region.Line)
                    {
                        var count = region.HasContent ? region.Commas + 1 : 0;
                        folds.Add(new JsonFold(region.Start, i + 1, region.IsObject, count));
                    }

                    break;

                case ',':
                    if (open.Count > 0)
                    {
                        open[^1] = open[^1] with { Commas = open[^1].Commas + 1 };
                    }

                    break;

                default:
                    if (!char.IsWhiteSpace(c))
                    {
                        MarkContent(open);
                    }

                    break;
            }
        }

        // Closed innermost first; the editor wants them by where they start.
        folds.Sort(static (a, b) => a.Start.CompareTo(b.Start));
        return folds;
    }

    private static void MarkContent(List<OpenBracket> open)
    {
        if (open.Count > 0 && !open[^1].HasContent)
        {
            open[^1] = open[^1] with { HasContent = true };
        }
    }

    private readonly record struct OpenBracket(int Start, int Line, bool IsObject)
    {
        public bool HasContent { get; init; }

        public int Commas { get; init; }
    }
}
