using PgNimbus.Core.Schema;

namespace PgNimbus.App.ViewModels;

public sealed class ColumnNode : SchemaTreeNode
{
    public ColumnNode(ColumnDetail detail)
    {
        Name = detail.Name;
        DataType = detail.DataType;
        DomainBaseType = detail.DomainBaseType;
        NotNull = detail.NotNull;
        IsPrimaryKey = detail.IsPrimaryKey;
    }

    public string DataType { get; }

    /// <summary>The base type a domain column resolves to (e.g. "citext" for a domain over citext); null when the declared type isn't a domain.</summary>
    public string? DomainBaseType { get; }

    /// <summary>The compact form shown in the tree (see <see cref="PgTypeAbbreviations"/>).</summary>
    public string DataTypeShort => PgTypeAbbreviations.Abbreviate(DataType);

    /// <summary>
    /// The type name the category icon is derived from — the declared type, or,
    /// when that's a domain with no icon of its own, the base type it resolves to
    /// (so a domain over citext still shows the text glyph).
    /// </summary>
    public string? TypeClassifier => PgTypeCategorizer.ClassifierType(DataType, DomainBaseType);

    /// <summary>
    /// The full type name for a hover tooltip — but only when it differs from the
    /// abbreviated form, so unabbreviated types don't get a tooltip that just
    /// repeats what's already on screen. (Null ⇒ no tooltip.)
    /// </summary>
    public string? FullTypeTip => DataTypeShort != DataType ? DataType : null;

    public bool NotNull { get; }

    public bool IsPrimaryKey { get; }
}
