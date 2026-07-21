using System.Text;
using System.Text.Json;

namespace PgNimbus.Core.Json;

/// <summary>The JSON value kind a <see cref="JsonTreeNode"/> holds — drives the per-kind glyph the tree view shows.</summary>
public enum JsonNodeKind
{
    Object,
    Array,
    String,
    Number,
    Boolean,
    Null,
}

/// <summary>
/// One node in the read-only tree the cell inspector renders for a JSON value:
/// an object member (keyed by its property name), an array element (named
/// <c>[i]</c>), or the document root. Containers carry their <see cref="Children"/>;
/// scalars carry a display-ready <see cref="ValuePreview"/>. Pure data — the App
/// binds a <c>TreeView</c> to it, but nothing here touches UI (Core stays
/// Avalonia-free).
/// </summary>
public sealed record JsonTreeNode(
    string Name,
    JsonNodeKind Kind,
    string ValuePreview,
    IReadOnlyList<JsonTreeNode> Children)
{
    /// <summary>True for objects and arrays — the nodes a tree view can expand.</summary>
    public bool HasChildren => Children.Count > 0;
}

/// <summary>
/// Builds a <see cref="JsonTreeNode"/> tree from a JSON string for the inspector's
/// document view. Structure-only and forgiving: returns null when the text isn't
/// JSON (the inspector then just shows the raw text), never throws.
/// </summary>
public static class JsonTree
{
    // Leaf string previews are clamped so one giant value can't blow out a row;
    // the inspector's text view still shows the untruncated value.
    private const int MaxPreviewLength = 200;

    /// <summary>The parsed tree's root, or null when <paramref name="text"/> isn't well-formed JSON.</summary>
    public static JsonTreeNode? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            // The root is named "$" (jsonpath's root) so the breadcrumb reads naturally.
            return BuildNode("$", document.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonTreeNode BuildNode(string name, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
            {
                var children = new List<JsonTreeNode>();
                foreach (var property in element.EnumerateObject())
                {
                    children.Add(BuildNode(property.Name, property.Value));
                }

                return new JsonTreeNode(name, JsonNodeKind.Object, SummarizeObject(children.Count), children);
            }

            case JsonValueKind.Array:
            {
                var children = new List<JsonTreeNode>();
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    children.Add(BuildNode($"[{index++}]", item));
                }

                return new JsonTreeNode(name, JsonNodeKind.Array, SummarizeArray(children.Count), children);
            }

            case JsonValueKind.String:
                return Leaf(name, JsonNodeKind.String, Quote(element.GetString() ?? string.Empty));

            case JsonValueKind.Number:
                return Leaf(name, JsonNodeKind.Number, element.GetRawText());

            case JsonValueKind.True:
            case JsonValueKind.False:
                return Leaf(name, JsonNodeKind.Boolean, element.GetRawText());

            default: // Null (and the unreachable Undefined)
                return Leaf(name, JsonNodeKind.Null, "null");
        }
    }

    private static JsonTreeNode Leaf(string name, JsonNodeKind kind, string preview) =>
        new(name, kind, Truncate(preview), []);

    private static string SummarizeObject(int count) => count == 1 ? "{ 1 field }" : $"{{ {count} fields }}";

    private static string SummarizeArray(int count) => count == 1 ? "[ 1 item ]" : $"[ {count} items ]";

    private static string Quote(string value) => Truncate($"\"{value}\"");

    private static string Truncate(string value)
    {
        if (value.Length <= MaxPreviewLength)
        {
            return value;
        }

        // Collapse embedded newlines/tabs so a multi-line string stays one row.
        var clamped = value[..MaxPreviewLength];
        var sb = new StringBuilder(clamped.Length + 1);
        foreach (var ch in clamped)
        {
            sb.Append(ch is '\n' or '\r' or '\t' ? ' ' : ch);
        }

        return sb.Append('…').ToString();
    }
}
