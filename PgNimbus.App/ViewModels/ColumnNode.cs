using PgNimbus.Core.Schema;

namespace PgNimbus.App.ViewModels;

public sealed class ColumnNode : SchemaTreeNode
{
    public ColumnNode(ColumnDetail detail)
    {
        Name = detail.Name;
        DataType = detail.DataType;
        NotNull = detail.NotNull;
        IsPrimaryKey = detail.IsPrimaryKey;
    }

    public string DataType { get; }

    /// <summary>The compact form shown in the tree (see <see cref="PgTypeAbbreviations"/>).</summary>
    public string DataTypeShort => PgTypeAbbreviations.Abbreviate(DataType);

    /// <summary>
    /// The full type name for a hover tooltip — but only when it differs from the
    /// abbreviated form, so unabbreviated types don't get a tooltip that just
    /// repeats what's already on screen. (Null ⇒ no tooltip.)
    /// </summary>
    public string? FullTypeTip => DataTypeShort != DataType ? DataType : null;

    public bool NotNull { get; }

    public bool IsPrimaryKey { get; }
}
