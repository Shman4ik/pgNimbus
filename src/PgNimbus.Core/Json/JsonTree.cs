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
/// One step from a JSON container to one of its values: an object member, by
/// <see cref="Key"/> and its position among the object's members, or an array
/// element, by <see cref="Index"/> alone (<see cref="Key"/> null). The member's
/// position is kept because a <c>json</c> (not <c>jsonb</c>) value can repeat a
/// key, and only the position says which of the two was meant.
/// </summary>
public readonly record struct JsonPathSegment(string? Key, int Index)
{
    public static JsonPathSegment Member(string key, int position) => new(key, position);

    public static JsonPathSegment Element(int index) => new(null, index);

    public bool IsElement => Key is null;
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

    /// <summary>The steps from the document root to this node; empty for the root.</summary>
    public IReadOnlyList<JsonPathSegment> Path { get; init; } = [];

    /// <summary>
    /// Whether the tree opens with this node expanded. Decided once for the whole
    /// document by <see cref="JsonTree.Parse"/>: see <see cref="JsonTree.OpenRows"/>.
    /// </summary>
    public bool ExpandedByDefault { get; internal set; }

    private bool? _isExpanded;

    /// <summary>
    /// Whether the node is open in a tree: <see cref="ExpandedByDefault"/> until the
    /// user opens or closes it. Kept on the node because the tree virtualizes, and
    /// the row a node was shown in is reused for another once it scrolls away.
    /// </summary>
    public bool IsExpanded
    {
        get => _isExpanded ?? ExpandedByDefault;
        set => _isExpanded = value;
    }

    /// <summary>An array element, whose name is its index: drawn quieter than a member's key.</summary>
    public bool IsElement => Path.Count > 0 && Path[^1].IsElement;

    public bool IsContainer => Kind is JsonNodeKind.Object or JsonNodeKind.Array;

    public bool IsString => Kind == JsonNodeKind.String;

    public bool IsNumber => Kind == JsonNodeKind.Number;

    /// <summary><c>true</c>, <c>false</c> and <c>null</c>: the words, coloured alike.</summary>
    public bool IsLiteral => Kind is JsonNodeKind.Boolean or JsonNodeKind.Null;
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

    /// <summary>
    /// How many rows the tree may show when it opens. Containers are expanded
    /// breadth first, shallowest first, while the rows they add still fit: a
    /// small document opens whole, a large one opens a level or two deep. It used
    /// to open as one collapsed <c>$</c> row, so every value took a click to
    /// reach even when the whole document would have fit on screen.
    /// </summary>
    public const int OpenRows = 100;

    /// <summary>The parsed tree's root, or null when <paramref name="text"/> isn't well-formed JSON.</summary>
    public static JsonTreeNode? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(text, JsonText.DocumentOptions);
            // The root is named "$" (jsonpath's root) so the breadcrumb reads naturally.
            var root = BuildNode("$", document.RootElement, []);
            ExpandWhatFits(root);
            return root;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            // A lone surrogate escape: a token the reader accepts but can't turn
            // into a .NET string (see JsonText.TryFormat).
            return null;
        }
    }

    /// <summary>
    /// The value at <paramref name="path"/> in <paramref name="text"/>, as someone
    /// copying it wants it: a string's own text (unquoted, unescaped), a number
    /// or word as written, and an object or array as indented JSON. Null when
    /// the text isn't JSON or the path leads nowhere.
    /// </summary>
    public static string? ValueAt(string text, IReadOnlyList<JsonPathSegment> path)
    {
        try
        {
            using var document = JsonDocument.Parse(text, JsonText.DocumentOptions);
            var element = document.RootElement;
            foreach (var segment in path)
            {
                if (!TryStep(element, segment, out element))
                {
                    return null;
                }
            }

            return element.ValueKind switch
            {
                JsonValueKind.String => element.GetString(),
                JsonValueKind.Object or JsonValueKind.Array => JsonText.Format(element, indented: true),
                _ => element.GetRawText(),
            };
        }
        catch (JsonException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static bool TryStep(JsonElement element, JsonPathSegment segment, out JsonElement child)
    {
        child = default;
        if (segment.IsElement)
        {
            if (element.ValueKind != JsonValueKind.Array || segment.Index >= element.GetArrayLength())
            {
                return false;
            }

            child = element[segment.Index];
            return true;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var position = 0;
        foreach (var property in element.EnumerateObject())
        {
            if (position++ == segment.Index)
            {
                child = property.Value;
                return property.Name == segment.Key;
            }
        }

        return false;
    }

    private static JsonTreeNode BuildNode(string name, JsonElement element, JsonPathSegment[] path)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
            {
                var children = new List<JsonTreeNode>();
                var position = 0;
                foreach (var property in element.EnumerateObject())
                {
                    children.Add(BuildNode(OneLine(property.Name), property.Value, [.. path, JsonPathSegment.Member(property.Name, position++)]));
                }

                return new JsonTreeNode(name, JsonNodeKind.Object, SummarizeObject(children.Count), children) { Path = path };
            }

            case JsonValueKind.Array:
            {
                var children = new List<JsonTreeNode>();
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    children.Add(BuildNode($"[{index}]", item, [.. path, JsonPathSegment.Element(index)]));
                    index++;
                }

                return new JsonTreeNode(name, JsonNodeKind.Array, SummarizeArray(children.Count), children) { Path = path };
            }

            case JsonValueKind.String:
                // Written the way the text view writes it: quoted, with a newline
                // as \n, so a multi-line value stays one row.
                return Leaf(name, JsonNodeKind.String, JsonText.Quote(element.GetString() ?? string.Empty), path);

            case JsonValueKind.Number:
                return Leaf(name, JsonNodeKind.Number, element.GetRawText(), path);

            case JsonValueKind.True:
            case JsonValueKind.False:
                return Leaf(name, JsonNodeKind.Boolean, element.GetRawText(), path);

            default: // Null (and the unreachable Undefined)
                return Leaf(name, JsonNodeKind.Null, "null", path);
        }
    }

    private static JsonTreeNode Leaf(string name, JsonNodeKind kind, string preview, JsonPathSegment[] path) =>
        new(name, kind, Truncate(preview), []) { Path = path };

    private static string SummarizeObject(int count) => count == 1 ? "{ 1 field }" : $"{{ {count} fields }}";

    private static string SummarizeArray(int count) => count == 1 ? "[ 1 item ]" : $"[ {count} items ]";

    // Breadth first from the root, which always opens: each container opens if
    // its children still fit in the rows left, and a level is finished before
    // the next is looked at, so what a large document shows is its outline.
    private static void ExpandWhatFits(JsonTreeNode root)
    {
        if (!root.HasChildren)
        {
            return;
        }

        root.ExpandedByDefault = true;
        var rows = 1 + root.Children.Count;
        var level = new List<JsonTreeNode>(root.Children);
        while (level.Count > 0)
        {
            var next = new List<JsonTreeNode>();
            foreach (var node in level)
            {
                if (!node.HasChildren || rows + node.Children.Count > OpenRows)
                {
                    continue;
                }

                node.ExpandedByDefault = true;
                rows += node.Children.Count;
                next.AddRange(node.Children);
            }

            level = next;
        }
    }

    // A key is shown as its text, but a newline or tab in it would break the row.
    private static string OneLine(string name)
    {
        foreach (var c in name)
        {
            if (c < ' ')
            {
                var quoted = JsonText.Quote(name);
                return quoted[1..^1];
            }
        }

        return name;
    }

    private static string Truncate(string value) =>
        value.Length <= MaxPreviewLength ? value : string.Concat(value.AsSpan(0, MaxPreviewLength), "…");
}
