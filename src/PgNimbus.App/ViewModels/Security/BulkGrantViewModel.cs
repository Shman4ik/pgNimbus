using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using PgNimbus.Core.Security;

namespace PgNimbus.App.ViewModels.Security;

/// <summary>One entry of the preset combo: the enum value plus the words a person reads.</summary>
public sealed record BulkGrantPresetOption(BulkGrantPreset Preset, string Label, string Description);

/// <summary>
/// Backs the bulk-grant dialog — "give this role access to a whole schema",
/// which is the request behind most hand-written GRANT scripts.
///
/// It composes a <see cref="BulkGrantRequest"/> and re-renders
/// <see cref="Script"/> on every edit. It never executes anything: the dialog's
/// affirmative hands the script to the editor, following the rule that every
/// privilege change in this feature is SQL the user reads first.
/// </summary>
public sealed partial class BulkGrantViewModel : ObservableObject
{
    [ObservableProperty]
    private string? _schema;

    [ObservableProperty]
    private string? _grantee;

    [ObservableProperty]
    private BulkGrantPresetOption? _preset;

    [ObservableProperty]
    private bool _includeFutureObjects = true;

    [ObservableProperty]
    private string? _futureObjectsOwner;

    [ObservableProperty]
    private string _script = "";

    public BulkGrantViewModel(
        IEnumerable<string> schemas,
        IEnumerable<string> roles,
        string? initialSchema,
        string? currentRole)
    {
        Schemas = [.. schemas];
        Grantees = [.. roles];

        // The creator list is the same set of roles: default privileges are keyed
        // to whoever CREATES an object, which in practice is the role migrations
        // connect as — most often the one connected right now.
        CreatorRoles = [.. roles];

        Presets =
        [
            new(BulkGrantPreset.ReadOnly, "Read only",
                "SELECT on every table and view, plus USAGE and SELECT on sequences."),
            new(BulkGrantPreset.ReadWrite, "Read and write",
                "SELECT, INSERT, UPDATE and DELETE on tables, plus USAGE, SELECT and UPDATE on sequences."),
            new(BulkGrantPreset.Full, "Everything in the schema",
                "ALL PRIVILEGES on tables, sequences and functions, plus CREATE on the schema itself."),
            new(BulkGrantPreset.RevokeAll, "Revoke everything",
                "REVOKE ALL on tables, sequences, functions and the schema — and on default privileges too."),
        ];

        _schema = initialSchema is not null && Schemas.Contains(initialSchema) ? initialSchema : Schemas.FirstOrDefault();
        _preset = Presets[0];
        _futureObjectsOwner = currentRole is not null && CreatorRoles.Contains(currentRole)
            ? currentRole
            : CreatorRoles.FirstOrDefault();

        Rebuild();
    }

    public ObservableCollection<string> Schemas { get; }

    public ObservableCollection<string> Grantees { get; }

    public ObservableCollection<string> CreatorRoles { get; }

    public IReadOnlyList<BulkGrantPresetOption> Presets { get; }

    public string PresetDescription => Preset?.Description ?? "";

    /// <summary>A script with no schema or no grantee names nothing — the button stays off.</summary>
    public bool CanOpen => Schema is { Length: > 0 } && Grantee is { Length: > 0 } && Script.Length > 0;

    /// <summary>The tab title the generated script gets in the editor.</summary>
    public string ScriptTitle => $"grants · {Schema}";

    partial void OnSchemaChanged(string? value) => Rebuild();

    partial void OnGranteeChanged(string? value) => Rebuild();

    partial void OnPresetChanged(BulkGrantPresetOption? value)
    {
        OnPropertyChanged(nameof(PresetDescription));
        Rebuild();
    }

    partial void OnIncludeFutureObjectsChanged(bool value) => Rebuild();

    partial void OnFutureObjectsOwnerChanged(string? value) => Rebuild();

    private void Rebuild()
    {
        if (Schema is not { Length: > 0 } schema || Preset is not { } preset)
        {
            Script = "";
            OnPropertyChanged(nameof(CanOpen));
            return;
        }

        Script = GrantScriptBuilder.BuildBulk(new BulkGrantRequest(
            schema,
            Grantee,
            preset.Preset,
            IncludeFutureObjects,
            IncludeFutureObjects ? FutureObjectsOwner : null));

        OnPropertyChanged(nameof(CanOpen));
        OnPropertyChanged(nameof(ScriptTitle));
    }
}
