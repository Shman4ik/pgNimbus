using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using PgNimbus.Core.Localization;
using PgNimbus.Core.Security;

namespace PgNimbus.App.ViewModels.Security;

/// <summary>One entry of the preset combo: the enum value plus the words a person reads.</summary>
public sealed record BulkGrantPresetOption(BulkGrantPreset Preset, StringKey LabelKey, StringKey DescriptionKey)
{
    public string Label => Strings.Get(LabelKey);

    public string Description => Strings.Get(DescriptionKey);
}

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
            new(BulkGrantPreset.ReadOnly, StringKey.Security_PresetReadOnly, StringKey.Security_PresetReadOnlyDescription),
            new(BulkGrantPreset.ReadWrite, StringKey.Security_PresetReadWrite, StringKey.Security_PresetReadWriteDescription),
            new(BulkGrantPreset.Full, StringKey.Security_PresetFull, StringKey.Security_PresetFullDescription),
            new(BulkGrantPreset.RevokeAll, StringKey.Security_PresetRevokeAll, StringKey.Security_PresetRevokeAllDescription),
        ];

        _schema = initialSchema is not null && Schemas.Contains(initialSchema) ? initialSchema : Schemas.FirstOrDefault();
        _preset = Presets[0];
        _futureObjectsOwner = currentRole is not null && CreatorRoles.Contains(currentRole)
            ? currentRole
            : CreatorRoles.FirstOrDefault();

        Rebuild();

        Localizer.WhenChanged(this, static vm =>
        {
            vm.OnPropertyChanged(nameof(PresetDescription));
            vm.OnPropertyChanged(nameof(ScriptTitle));
        });
    }

    public ObservableCollection<string> Schemas { get; }

    public ObservableCollection<string> Grantees { get; }

    public ObservableCollection<string> CreatorRoles { get; }

    public IReadOnlyList<BulkGrantPresetOption> Presets { get; }

    public string PresetDescription => Preset?.Description ?? "";

    /// <summary>A script with no schema or no grantee names nothing — the button stays off.</summary>
    public bool CanOpen => Schema is { Length: > 0 } && Grantee is { Length: > 0 } && Script.Length > 0;

    /// <summary>The tab title the generated script gets in the editor.</summary>
    public string ScriptTitle => Strings.Security_GrantsTabTitle(Schema);

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
