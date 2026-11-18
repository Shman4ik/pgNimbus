using PgNimbus.Core.Localization;
using PgNimbus.Core.Schema;

namespace PgNimbus.App.ViewModels;

/// <summary>"Functions" group under each schema — lazily lists that schema's functions/procedures/aggregates.</summary>
public sealed class FunctionsGroupNode : SchemaTreeNode
{
    private readonly SchemaService _schemaService;

    public FunctionsGroupNode(SchemaService schemaService, string schema)
    {
        _schemaService = schemaService;
        Schema = schema;
        SetLocalizedName(static () => Strings.Sidebar_Functions);
        MarkExpandable();
    }

    public string Schema { get; }

    protected override async Task<IReadOnlyList<SchemaTreeNode>> FetchChildrenAsync()
    {
        var functions = await _schemaService.GetFunctionsAsync(Schema, CancellationToken.None);
        return functions.Select(f => (SchemaTreeNode)new FunctionNode(Schema, f)).ToList();
    }
}

public sealed class FunctionNode : SchemaTreeNode
{
    public FunctionNode(string schema, FunctionInfo info)
    {
        Schema = schema;
        Name = info.Name;
        Arguments = info.Arguments;
        ReturnType = info.ReturnType;
        Kind = info.Kind;
    }

    public string Schema { get; }

    public string Arguments { get; }

    public string ReturnType { get; }

    /// <summary>pg_proc.prokind: f(unction), p(rocedure), a(ggregate), w(indow).</summary>
    public char Kind { get; }

    /// <summary>Aggregates have no pg_get_functiondef — the Source menu item disables for them.</summary>
    public bool HasSource => Kind != 'a';

    public string Signature => Arguments.Length == 0 ? $"{Name}()" : $"{Name}({Arguments})";

    /// <summary>The dimmed detail after the name: "→ return type", plus a kind tag for non-plain-functions.</summary>
    public string Detail
    {
        get
        {
            var kind = Kind switch
            {
                'p' => Strings.Sidebar_FunctionProcedure,
                'a' => Strings.Sidebar_FunctionAggregate,
                'w' => Strings.Sidebar_FunctionWindow,
                _ => null,
            };
            var arrow = ReturnType.Length == 0 ? null : $"→ {ReturnType}";
            return string.Join("  ", new[] { arrow, kind }.Where(s => s is not null));
        }
    }

    public string Tooltip => $"{Signature}{(ReturnType.Length == 0 ? "" : $" → {ReturnType}")}";

    public override void RefreshLanguage()
    {
        base.RefreshLanguage();
        OnPropertyChanged(nameof(Detail));
    }
}

/// <summary>Root-level "Extensions" group — installed extensions first, then the rest of pg_available_extensions.</summary>
public sealed class ExtensionsGroupNode : SchemaTreeNode
{
    private readonly SchemaService _schemaService;

    public ExtensionsGroupNode(SchemaService schemaService)
    {
        _schemaService = schemaService;
        SetLocalizedName(static () => Strings.Sidebar_Extensions);
        MarkExpandable();
    }

    protected override async Task<IReadOnlyList<SchemaTreeNode>> FetchChildrenAsync()
    {
        var extensions = await _schemaService.GetExtensionsAsync(CancellationToken.None);
        return extensions.Select(e => (SchemaTreeNode)new ExtensionNode(this, e)).ToList();
    }
}

public sealed class ExtensionNode : SchemaTreeNode
{
    public ExtensionNode(ExtensionsGroupNode group, ExtensionInfo info)
    {
        Group = group;
        Name = info.Name;
        IsInstalled = info.IsInstalled;
        _installedVersion = info.IsInstalled ? info.InstalledVersion! : null;
        _defaultVersion = info.DefaultVersion;
        Description = info.Description;
    }

    private readonly string? _installedVersion;
    private readonly string? _defaultVersion;

    /// <summary>The owning group, so install/drop can refresh the whole list in place.</summary>
    public ExtensionsGroupNode Group { get; }

    public bool IsInstalled { get; }

    public string VersionLabel => _installedVersion ?? Strings.Sidebar_ExtensionAvailable(_defaultVersion ?? string.Empty);

    public override void RefreshLanguage()
    {
        base.RefreshLanguage();
        OnPropertyChanged(nameof(VersionLabel));
    }

    public string? Description { get; }
}

/// <summary>Root-level "Roles" group listing non-system roles with their headline attributes.</summary>
public sealed class RolesGroupNode : SchemaTreeNode
{
    private readonly SchemaService _schemaService;

    public RolesGroupNode(SchemaService schemaService)
    {
        _schemaService = schemaService;
        SetLocalizedName(static () => Strings.Sidebar_Roles);
        MarkExpandable();
    }

    protected override async Task<IReadOnlyList<SchemaTreeNode>> FetchChildrenAsync()
    {
        var roles = await _schemaService.GetRolesAsync(CancellationToken.None);
        return roles.Select(r => (SchemaTreeNode)new RoleNode(r)).ToList();
    }
}

public sealed class RoleNode : SchemaTreeNode
{
    public RoleNode(RoleInfo info)
    {
        Name = info.Name;
        IsSuperuser = info.IsSuperuser;
        var attributes = new List<string>();
        if (info.IsSuperuser)
        {
            attributes.Add("superuser");
        }

        if (info.CanLogin)
        {
            attributes.Add("login");
        }

        if (info.CanCreateDb)
        {
            attributes.Add("createdb");
        }

        if (info.CanCreateRole)
        {
            attributes.Add("createrole");
        }

        AttributesLabel = string.Join(" · ", attributes);
    }

    public bool IsSuperuser { get; }

    public string AttributesLabel { get; }
}
