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

    public bool NotNull { get; }

    public bool IsPrimaryKey { get; }

    public string PrimaryKeyGlyph => IsPrimaryKey ? "🔑" : string.Empty;
}
