using PgNimbus.Core.Schema;

namespace PgNimbus.App.ViewModels;

/// <summary>"Sequences" group under a schema — lazily lists that schema's sequences.</summary>
public sealed class SequencesGroupNode : SchemaTreeNode
{
    private readonly SchemaService _schemaService;

    public SequencesGroupNode(SchemaService schemaService, string schema)
    {
        _schemaService = schemaService;
        Schema = schema;
        Name = "Sequences";
        MarkExpandable();
    }

    public string Schema { get; }

    protected override async Task<IReadOnlyList<SchemaTreeNode>> FetchChildrenAsync()
    {
        var sequences = await _schemaService.GetSequencesAsync(Schema, CancellationToken.None);
        if (sequences.Count == 0)
        {
            return [new EmptyNode { Name = "No sequences" }];
        }

        return sequences.Select(s => (SchemaTreeNode)new SequenceNode(s)).ToList();
    }
}

public sealed class SequenceNode : SchemaTreeNode
{
    public SequenceNode(SequenceInfo info)
    {
        Name = info.Name;
        DataType = info.DataType;
        IncrementBy = info.IncrementBy;
        LastValue = info.LastValue;
        StartValue = info.StartValue;
        MinValue = info.MinValue;
        MaxValue = info.MaxValue;
        Cycle = info.Cycle;
    }

    public string DataType { get; }

    public long IncrementBy { get; }

    public long? LastValue { get; }

    public long StartValue { get; }

    public long MinValue { get; }

    public long MaxValue { get; }

    public bool Cycle { get; }

    /// <summary>The dim detail after the name — the value type and, if the sequence has advanced, its current value.</summary>
    public string Detail => LastValue is { } last ? $"{DataType}  ·  at {last}" : DataType;

    public string Tooltip
    {
        get
        {
            var parts = new List<string>
            {
                $"{Name}  ({DataType})",
                $"increment {IncrementBy}",
                LastValue is { } last ? $"current {last}" : "not yet used",
                $"range {MinValue} … {MaxValue}",
            };
            if (Cycle)
            {
                parts.Add("cycles");
            }

            return string.Join("\n", parts);
        }
    }
}

/// <summary>"Types" group under a schema — user-defined enums, composites, and domains.</summary>
public sealed class TypesGroupNode : SchemaTreeNode
{
    private readonly SchemaService _schemaService;

    public TypesGroupNode(SchemaService schemaService, string schema)
    {
        _schemaService = schemaService;
        Schema = schema;
        Name = "Types";
        MarkExpandable();
    }

    public string Schema { get; }

    protected override async Task<IReadOnlyList<SchemaTreeNode>> FetchChildrenAsync()
    {
        var types = await _schemaService.GetUserTypesAsync(Schema, CancellationToken.None);
        if (types.Count == 0)
        {
            return [new EmptyNode { Name = "No user types" }];
        }

        return types.Select(t => (SchemaTreeNode)new UserTypeNode(t)).ToList();
    }
}

public sealed class UserTypeNode : SchemaTreeNode
{
    public UserTypeNode(UserTypeInfo info)
    {
        Name = info.Name;
        TypType = info.TypType;
        EnumLabels = info.EnumLabels;
        CompositeFields = info.CompositeFields;
        DomainBaseType = info.DomainBaseType;
        DomainNotNull = info.DomainNotNull;

        // Reuse the column type-icon language: enums and composites have their own
        // family icon; a domain reads as its underlying base type's family.
        Category = TypType switch
        {
            'e' => PgTypeCategory.Enum,
            'c' => PgTypeCategory.Composite,
            'd' => PgTypeCategorizer.Categorize(DomainBaseType),
            _ => PgTypeCategory.Other,
        };
    }

    /// <summary>pg_type.typtype: e(num), c(omposite), d(omain).</summary>
    public char TypType { get; }

    public IReadOnlyList<string> EnumLabels { get; }

    public IReadOnlyList<string> CompositeFields { get; }

    public string? DomainBaseType { get; }

    public bool DomainNotNull { get; }

    /// <summary>The type family driving the icon shown next to the name (see <see cref="Category"/>).</summary>
    public PgTypeCategory Category { get; }

    /// <summary>The dim detail after the name — kind plus a compact summary of the type's shape.</summary>
    public string Detail => TypType switch
    {
        'e' => $"enum  ·  {EnumLabels.Count} label{(EnumLabels.Count == 1 ? "" : "s")}",
        'c' => $"composite  ·  {CompositeFields.Count} field{(CompositeFields.Count == 1 ? "" : "s")}",
        'd' => DomainNotNull ? $"domain → {DomainBaseType}  ·  NOT NULL" : $"domain → {DomainBaseType}",
        _ => string.Empty,
    };

    public string Tooltip => TypType switch
    {
        'e' => EnumLabels.Count == 0 ? Name : string.Join("  |  ", EnumLabels),
        'c' => CompositeFields.Count == 0 ? Name : string.Join("\n", CompositeFields),
        'd' => DomainNotNull ? $"domain over {DomainBaseType} (NOT NULL)" : $"domain over {DomainBaseType}",
        _ => Name,
    };
}

/// <summary>"Indexes" sub-group under a table/matview — lazily lists its indexes.</summary>
public sealed class IndexesGroupNode : SchemaTreeNode
{
    private readonly SchemaService _schemaService;

    public IndexesGroupNode(SchemaService schemaService, string schema, string table)
    {
        _schemaService = schemaService;
        Schema = schema;
        Table = table;
        Name = "Indexes";
        MarkExpandable();
    }

    public string Schema { get; }

    public string Table { get; }

    protected override async Task<IReadOnlyList<SchemaTreeNode>> FetchChildrenAsync()
    {
        var indexes = await _schemaService.GetIndexesAsync(Schema, Table, CancellationToken.None);
        if (indexes.Count == 0)
        {
            return [new EmptyNode { Name = "No indexes" }];
        }

        return indexes.Select(i => (SchemaTreeNode)new IndexNode(i)).ToList();
    }
}

public sealed class IndexNode : SchemaTreeNode
{
    public IndexNode(IndexInfo info)
    {
        Name = info.Name;
        IsUnique = info.IsUnique;
        IsPrimary = info.IsPrimary;
        Definition = info.Definition;
    }

    public bool IsUnique { get; }

    public bool IsPrimary { get; }

    public string Definition { get; }

    /// <summary>The dim tag after the name: primary key / unique / (nothing for a plain index).</summary>
    public string Detail => IsPrimary ? "primary key" : IsUnique ? "unique" : string.Empty;

    /// <summary>The full CREATE INDEX definition, shown on hover.</summary>
    public string Tooltip => Definition;
}

/// <summary>"Triggers" sub-group under a table/view — lazily lists its user triggers.</summary>
public sealed class TriggersGroupNode : SchemaTreeNode
{
    private readonly SchemaService _schemaService;

    public TriggersGroupNode(SchemaService schemaService, string schema, string table)
    {
        _schemaService = schemaService;
        Schema = schema;
        Table = table;
        Name = "Triggers";
        MarkExpandable();
    }

    public string Schema { get; }

    public string Table { get; }

    protected override async Task<IReadOnlyList<SchemaTreeNode>> FetchChildrenAsync()
    {
        var triggers = await _schemaService.GetTriggersAsync(Schema, Table, CancellationToken.None);
        if (triggers.Count == 0)
        {
            return [new EmptyNode { Name = "No triggers" }];
        }

        return triggers.Select(t => (SchemaTreeNode)new TriggerNode(t)).ToList();
    }
}

public sealed class TriggerNode : SchemaTreeNode
{
    public TriggerNode(TriggerInfo info)
    {
        Name = info.Name;
        Enabled = info.Enabled;
        Definition = info.Definition;
    }

    public bool Enabled { get; }

    public string Definition { get; }

    /// <summary>A dim "disabled" tag for a trigger that won't fire; empty when enabled.</summary>
    public string Detail => Enabled ? string.Empty : "disabled";

    /// <summary>The full CREATE TRIGGER definition, shown on hover.</summary>
    public string Tooltip => Definition;
}
