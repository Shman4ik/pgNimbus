using System.Collections.ObjectModel;
using System.Reflection;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PgNimbus.Core.Connections;
// Type alias rather than `using Npgsql`: that namespace has its own SslMode,
// which would collide with Core's on every field in this view model.
using NpgsqlDataSource = Npgsql.NpgsqlDataSource;

namespace PgNimbus.App.ViewModels;

public sealed partial class ConnectionDialogViewModel : ObservableObject
{
    private readonly ConnectionProfileStore _store;
    private readonly ICredentialStore _credentialStore;
    private readonly Action<Guid?>? _persistLastProfileId;

    public ObservableCollection<ConnectionProfile> Profiles { get; } = [];

    /// <summary>
    /// Set by the startup flow when the "connect on startup" preference is on:
    /// the view fires <see cref="ConnectCommand"/> as soon as the dialog opens.
    /// Only meaningful with a preselected profile — a fresh install has nothing
    /// to connect to and just shows the form.
    /// </summary>
    public bool AutoConnectOnOpen { get; init; }

    /// <summary>Whether opening the dialog should connect straight away — the view's cue to skip waiting for a click.</summary>
    public bool ShouldAutoConnect => AutoConnectOnOpen && SelectedProfile is not null;

    /// <summary>
    /// Dialog footer: release version (e.g. "0.4.5", stripped of the
    /// "+&lt;git-sha&gt;" build metadata the release pipeline embeds via
    /// -p:InformationalVersion) plus the copyright/license from the csproj,
    /// so the footer doesn't hardcode anything the release pipeline already owns.
    /// </summary>
    public string FooterText { get; } = BuildFooterText();

    private static string BuildFooterText()
    {
        var assembly = Assembly.GetEntryAssembly();
        var version = "v" + (assembly?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            .Split('+')[0] ?? "0.0.0");
        var copyright = assembly?.GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright;
        return string.IsNullOrEmpty(copyright) ? version : $"{version} · {copyright} · MIT";
    }

    /// <summary>Drives the empty-state hint over the Saved Connections list.</summary>
    public bool HasNoProfiles => Profiles.Count == 0;

    public IReadOnlyList<SslMode> SslModes { get; } = Enum.GetValues<SslMode>();

    public IReadOnlyList<SshAuthMethod> SshAuthMethods { get; } = Enum.GetValues<SshAuthMethod>();

    /// <summary>Preset swatches shown in the dialog; a per-connection accent color helps tell environments (prod vs. dev) apart at a glance.</summary>
    public IReadOnlyList<string> AccentColorSwatches { get; } =
        ["#E5484D", "#F76B15", "#FFB224", "#46A758", "#0091FF", "#8E4EC6", "#6B7280"];

    /// <summary>Shown as the host field's placeholder and used when it is left blank.</summary>
    public const string DefaultHost = "localhost";

    /// <summary>Shown as the database field's placeholder and used when it is left blank.</summary>
    public const string DefaultDatabase = "postgres";

    /// <summary>Shown as the username field's placeholder and used when it is left blank.</summary>
    public const string DefaultUsername = "postgres";

    /// <summary>The host to actually connect to: what was typed, or <see cref="DefaultHost"/> when the field is blank.</summary>
    public string EffectiveHost => Blank(Host) ? DefaultHost : Host.Trim();

    /// <summary>The database to actually connect to: what was typed, or <see cref="DefaultDatabase"/> when the field is blank.</summary>
    public string EffectiveDatabase => Blank(Database) ? DefaultDatabase : Database.Trim();

    /// <summary>The username to actually connect as: what was typed, or <see cref="DefaultUsername"/> when the field is blank.</summary>
    public string EffectiveUsername => Blank(Username) ? DefaultUsername : Username.Trim();

    /// <summary>The port to actually connect to: what was typed, or <see cref="ConnectionProfile.DefaultPort"/> when the field is left empty.</summary>
    public int EffectivePort => Port ?? ConnectionProfile.DefaultPort;

    /// <summary>
    /// The name a blank Name field stands for, shown there as a placeholder and
    /// saved as the profile's name. It tracks the host/database fields as they
    /// are typed, so naming a connection is opt-in rather than a chore.
    /// </summary>
    public string NamePlaceholder => $"{EffectiveHost}/{EffectiveDatabase}";

    /// <summary>The profile name to save: what was typed, or <see cref="NamePlaceholder"/> when the field is blank.</summary>
    public string EffectiveName => Blank(Name) ? NamePlaceholder : Name.Trim();

    private static bool Blank(string? value) => string.IsNullOrWhiteSpace(value);

    [ObservableProperty]
    private ConnectionProfile? _selectedProfile;

    // The four text fields below start *empty*, showing their default as a
    // dim placeholder instead of as real text (see the Default* constants and
    // the Effective* properties). Pre-filling them with "localhost"/"postgres"
    // meant every hand-typed connection began with a select-all or a run of
    // backspaces, because the caret lands after text the user never typed.
    // Leaving a field blank still connects to its default - Effective* is what
    // the profile, the test/connect path and the connection-string preview all
    // read, so nothing is lost by not typing.

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NamePlaceholder))]
    private string _name = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NamePlaceholder))]
    private string _host = string.Empty;

    /// <summary>Null means "not typed" - the field shows 5432 as a placeholder and <see cref="EffectivePort"/> supplies it.</summary>
    [ObservableProperty]
    private int? _port;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NamePlaceholder))]
    private string _database = string.Empty;

    [ObservableProperty]
    private string _username = string.Empty;

    [ObservableProperty]
    private SslMode _sslMode = SslMode.Prefer;

    [ObservableProperty]
    private string? _accentColor;

    [ObservableProperty]
    private string _password = string.Empty;

    [ObservableProperty]
    private bool _useSshTunnel;

    [ObservableProperty]
    private string _sshHost = string.Empty;

    [ObservableProperty]
    private int _sshPort = SshTunnelOptions.DefaultPort;

    [ObservableProperty]
    private string _sshUsername = string.Empty;

    [ObservableProperty]
    private SshAuthMethod _sshAuthMethod = SshAuthMethod.Password;

    [ObservableProperty]
    private string _sshPrivateKeyPath = string.Empty;

    [ObservableProperty]
    private string _sshPassword = string.Empty;

    [ObservableProperty]
    private string _importText = string.Empty;

    [ObservableProperty]
    private string? _errorMessage;

    /// <summary>Green success line (currently only "connection test succeeded"); mutually exclusive with <see cref="ErrorMessage"/>.</summary>
    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private bool _isConnecting;

    [ObservableProperty]
    private bool _isTesting;

    partial void OnErrorMessageChanged(string? value)
    {
        if (value is not null)
        {
            StatusMessage = null;
        }
    }

    /// <summary>
    /// Guards against feedback loops between the import box and the form
    /// fields: set while applying a parsed string onto the fields, or while
    /// rebuilding the preview string from the fields, so the other side's
    /// change handler doesn't re-trigger.
    /// </summary>
    private bool _syncingConnectionString;

    /// <summary>
    /// Raised with a <see cref="NpgsqlDataSource"/> that has already opened (and
    /// returned to its pool) one real connection, the profile's accent color,
    /// and, if a tunnel was used, the live SshTunnel to keep alive. The data
    /// source — not a connection string — is what crosses the hand-off, so the
    /// credentials are known good by the time a window is built and the pool
    /// arrives warm. Ownership transfers with it: the handler is responsible for
    /// disposing both the data source and the tunnel.
    /// </summary>
    public event Action<NpgsqlDataSource, string?, SshTunnel?>? Connected;

    /// <param name="lastProfileId">
    /// The profile connected to last session, preselected here so the common
    /// case — reconnect to the same database — needs no clicking at all: the
    /// form is already filled, the password already loaded, and Enter connects.
    /// Ignored when it names a profile that no longer exists.
    /// </param>
    /// <param name="persistLastProfileId">Called with the connected profile's id (null for an unsaved, ad-hoc connection) so the next session can preselect it.</param>
    public ConnectionDialogViewModel(
        ConnectionProfileStore store,
        ICredentialStore credentialStore,
        Guid? lastProfileId = null,
        Action<Guid?>? persistLastProfileId = null)
    {
        _store = store;
        _credentialStore = credentialStore;
        _persistLastProfileId = persistLastProfileId;

        Profiles.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasNoProfiles));

        foreach (var profile in _store.Load())
        {
            Profiles.Add(profile);
        }

        if (lastProfileId is { } id)
        {
            SelectedProfile = Profiles.FirstOrDefault(p => p.Id == id);
        }
    }

    partial void OnSelectedProfileChanged(ConnectionProfile? value)
    {
        if (value is null)
        {
            return;
        }

        Name = value.Name;
        Host = value.Host;
        Port = value.Port;
        Database = value.Database;
        Username = value.Username;
        SslMode = value.SslMode;
        AccentColor = value.AccentColor;
        Password = _credentialStore.LoadPassword(value.Id) ?? string.Empty;

        UseSshTunnel = value.SshTunnel is not null;
        SshHost = value.SshTunnel?.Host ?? string.Empty;
        SshPort = value.SshTunnel?.Port ?? SshTunnelOptions.DefaultPort;
        SshUsername = value.SshTunnel?.Username ?? string.Empty;
        SshAuthMethod = value.SshTunnel?.AuthMethod ?? SshAuthMethod.Password;
        SshPrivateKeyPath = value.SshTunnel?.PrivateKeyPath ?? string.Empty;
        SshPassword = _credentialStore.LoadPassword(DeriveSshCredentialId(value.Id)) ?? string.Empty;
    }

    [RelayCommand]
    private void New()
    {
        SelectedProfile = null;
        Name = string.Empty;
        Host = string.Empty;
        Port = null;
        Database = string.Empty;
        Username = string.Empty;
        SslMode = SslMode.Prefer;
        AccentColor = null;
        Password = string.Empty;
        UseSshTunnel = false;
        SshHost = string.Empty;
        SshPort = SshTunnelOptions.DefaultPort;
        SshUsername = string.Empty;
        SshAuthMethod = SshAuthMethod.Password;
        SshPrivateKeyPath = string.Empty;
        SshPassword = string.Empty;
        ErrorMessage = null;
        StatusMessage = null;
    }

    [RelayCommand]
    private void Save()
    {
        if (!TryBuildProfile(out var profile, out var error))
        {
            ErrorMessage = error;
            return;
        }

        var index = Profiles.ToList().FindIndex(p => p.Id == profile.Id);
        if (index >= 0)
        {
            Profiles[index] = profile;
        }
        else
        {
            Profiles.Add(profile);
        }

        _store.Save(Profiles);

        if (!string.IsNullOrEmpty(Password))
        {
            _credentialStore.SavePassword(profile.Id, Password);
        }

        if (UseSshTunnel && !string.IsNullOrEmpty(SshPassword))
        {
            _credentialStore.SavePassword(DeriveSshCredentialId(profile.Id), SshPassword);
        }

        SelectedProfile = profile;
    }

    /// <summary>
    /// Clones the selected profile under a new id, right below the original,
    /// and selects it — the "same server, other database" case, without
    /// retyping the host, SSL mode, and SSH block. The password is copied too
    /// (a copy you have to re-enter the password for saves nothing), the name
    /// gets a " (copy)" suffix so the two are told apart in the list.
    /// </summary>
    [RelayCommand]
    private void Duplicate()
    {
        if (SelectedProfile is not { } source)
        {
            return;
        }

        var copy = source with { Id = Guid.NewGuid(), Name = $"{source.Name} (copy)" };
        Profiles.Insert(Profiles.IndexOf(source) + 1, copy);
        _store.Save(Profiles);

        if (_credentialStore.LoadPassword(source.Id) is { } password)
        {
            _credentialStore.SavePassword(copy.Id, password);
        }

        if (_credentialStore.LoadPassword(DeriveSshCredentialId(source.Id)) is { } sshPassword)
        {
            _credentialStore.SavePassword(DeriveSshCredentialId(copy.Id), sshPassword);
        }

        SelectedProfile = copy;
    }

    [RelayCommand]
    private void SelectAccentColor(string? color) => AccentColor = color;

    /// <summary>Persists the list's current order after a drag-reorder in the dialog (the store writes profiles in enumeration order).</summary>
    public void PersistProfileOrder() => _store.Save(Profiles);

    /// <summary>
    /// Re-parses whatever connection string is in the import box — postgres://
    /// URI, JDBC URL, Key=Value;, libpq keywords, or a full psql command line —
    /// and applies it to the form. Exists for explicit re-trigger (e.g. to see
    /// the parse error); pasting or typing into the box already does this
    /// automatically via <see cref="OnImportTextChanged"/>.
    /// </summary>
    [RelayCommand]
    private void ImportConnectionString()
    {
        if (!ConnectionStringParser.TryParse(ImportText, out var parsed, out var parseError))
        {
            ErrorMessage = parseError;
            return;
        }

        ApplyParsed(parsed);
        ErrorMessage = null;
    }

    /// <summary>Auto-parses the import box as soon as its content looks like a full connection string — no need to press "Fill".</summary>
    partial void OnImportTextChanged(string value)
    {
        if (_syncingConnectionString || string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (ConnectionStringParser.TryParse(value, out var parsed, out _))
        {
            ApplyParsed(parsed);
        }
    }

    partial void OnHostChanged(string value) => SyncImportTextFromFields();
    partial void OnPortChanged(int? value) => SyncImportTextFromFields();
    partial void OnDatabaseChanged(string value) => SyncImportTextFromFields();
    partial void OnUsernameChanged(string value) => SyncImportTextFromFields();
    partial void OnPasswordChanged(string value) => SyncImportTextFromFields();
    partial void OnSslModeChanged(SslMode value) => SyncImportTextFromFields();

    /// <summary>Only the fields a parsed string actually mentions are overwritten; the rest of the form is left alone.</summary>
    private void ApplyParsed(ParsedConnectionString parsed)
    {
        _syncingConnectionString = true;
        try
        {
            if (parsed.Host is not null)
            {
                Host = parsed.Host;
            }

            if (parsed.Port is { } port)
            {
                Port = port;
            }

            if (parsed.Database is not null)
            {
                Database = parsed.Database;
            }

            if (parsed.Username is not null)
            {
                Username = parsed.Username;
            }

            // The preview renders the password as the fixed mask string (see
            // BuildPreviewConnectionString) - when the user hand-edits some
            // other part of the preview, the re-parse hands that mask back
            // here, and it must not overwrite the real password. Only the
            // exact mask is skipped, so a genuine pasted password that merely
            // contains a bullet still imports.
            if (parsed.Password is not null && parsed.Password != PasswordMask)
            {
                Password = parsed.Password;
            }

            if (parsed.SslMode is { } sslMode)
            {
                SslMode = sslMode;
            }

            // No name is written here: a blank Name field already reads as
            // host/database (NamePlaceholder) and saves under that name, and it
            // keeps tracking the fields if the pasted string is edited after.

            ImportText = BuildPreviewConnectionString();
        }
        finally
        {
            _syncingConnectionString = false;
        }
    }

    /// <summary>True while the form says nothing at all - every field blank, so only the placeholders are on screen.</summary>
    private bool IsFormBlank =>
        Blank(Host) && Blank(Database) && Blank(Username) && Port is null && string.IsNullOrEmpty(Password);

    /// <summary>Mirrors the current form fields into the import box as a postgres:// URI whenever a field is edited by hand.</summary>
    private void SyncImportTextFromFields()
    {
        if (_syncingConnectionString)
        {
            return;
        }

        _syncingConnectionString = true;
        try
        {
            // An untouched form leaves the paste box empty rather than filling it
            // with the defaults' URI: it is the box you paste *into*, and text
            // nobody typed sitting in it is one more thing to clear first.
            ImportText = IsFormBlank ? string.Empty : BuildPreviewConnectionString();
        }
        finally
        {
            _syncingConnectionString = false;
        }
    }

    private const string PasswordMask = "••••••";

    /// <summary>Renders the current fields as a postgres:// URI — the most widely recognized connection string format. The password shows as mask bullets; <see cref="BuildClipboardConnectionString"/> carries the real one.</summary>
    private string BuildPreviewConnectionString() => BuildConnectionStringUri(maskPassword: true);

    /// <summary>The full postgres:// URI with the real password, for the explicit copy-to-clipboard action only — never shown on screen.</summary>
    public string BuildClipboardConnectionString() => BuildConnectionStringUri(maskPassword: false);

    private string BuildConnectionStringUri(bool maskPassword)
    {
        var builder = new StringBuilder("postgres://");

        // Effective*, not the raw fields: a blank field connects to its default,
        // so a preview built from the raw text would describe a different
        // connection than the one Connect makes.
        builder.Append(Uri.EscapeDataString(EffectiveUsername));
        if (!string.IsNullOrEmpty(Password))
        {
            builder.Append(':').Append(maskPassword
                ? PasswordMask
                : Uri.EscapeDataString(Password));
        }

        builder.Append('@').Append(EffectiveHost);

        if (EffectivePort != ConnectionProfile.DefaultPort)
        {
            builder.Append(':').Append(EffectivePort);
        }

        builder.Append('/').Append(Uri.EscapeDataString(EffectiveDatabase));

        if (SslMode != SslMode.Prefer)
        {
            builder.Append("?sslmode=").Append(SslModeToQueryValue(SslMode));
        }

        return builder.ToString();
    }

    private static string SslModeToQueryValue(SslMode mode) => mode switch
    {
        SslMode.Disable => "disable",
        SslMode.Allow => "allow",
        SslMode.Prefer => "prefer",
        SslMode.Require => "require",
        SslMode.VerifyCa => "verify-ca",
        SslMode.VerifyFull => "verify-full",
        _ => "prefer",
    };

    [RelayCommand]
    private void Delete()
    {
        if (SelectedProfile is null)
        {
            return;
        }

        var idToDelete = SelectedProfile.Id;
        Profiles.Remove(SelectedProfile);
        _store.Save(Profiles);
        _credentialStore.DeletePassword(idToDelete);
        _credentialStore.DeletePassword(DeriveSshCredentialId(idToDelete));
        New();
    }

    /// <summary>
    /// Opens (and immediately closes) a real connection with the form's
    /// current values — including the SSH tunnel when enabled — without
    /// handing anything off to the main window, so credentials can be
    /// verified before saving or connecting.
    /// </summary>
    [RelayCommand]
    private async Task TestConnectionAsync()
    {
        if (IsTesting || IsConnecting)
        {
            return;
        }

        if (!TryBuildProfile(out var profile, out var error))
        {
            ErrorMessage = error;
            return;
        }

        ErrorMessage = null;
        StatusMessage = null;
        IsTesting = true;

        SshTunnel? tunnel = null;
        try
        {
            string connectionString;
            if (profile.SshTunnel is { } sshOptions)
            {
                tunnel = await Task.Run(() => SshTunnel.Connect(sshOptions, SshPassword, profile.Host, profile.Port));
                connectionString = profile.BuildConnectionString(
                    string.IsNullOrEmpty(Password) ? null : Password,
                    (tunnel.LocalHost, tunnel.LocalPort));
            }
            else
            {
                connectionString = profile.BuildConnectionString(string.IsNullOrEmpty(Password) ? null : Password);
            }

            var serverVersion = await ConnectionTester.TestAsync(connectionString);
            StatusMessage = $"Connection successful — PostgreSQL {serverVersion}";
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Connection test failed: {ex.Message}";
        }
        finally
        {
            // Unlike Connect, the test never hands the tunnel off — always tear it down.
            tunnel?.Dispose();
            IsTesting = false;
        }
    }

    [RelayCommand]
    private async Task ConnectAsync()
    {
        // Guard re-entry: a double-click on a profile (which fires ConnectCommand)
        // could otherwise start a second connect and spin up a duplicate SSH
        // tunnel while the first is still in flight.
        if (IsConnecting)
        {
            return;
        }

        if (!TryBuildProfile(out var profile, out var error))
        {
            ErrorMessage = error;
            return;
        }

        ErrorMessage = null;
        StatusMessage = $"Connecting to {profile.Name}…";
        IsConnecting = true;

        SshTunnel? tunnel = null;
        NpgsqlDataSource? dataSource = null;
        try
        {
            string connectionString;
            if (profile.SshTunnel is { } sshOptions)
            {
                tunnel = await Task.Run(() => SshTunnel.Connect(sshOptions, SshPassword, profile.Host, profile.Port));
                connectionString = profile.BuildConnectionString(
                    string.IsNullOrEmpty(Password) ? null : Password,
                    (tunnel.LocalHost, tunnel.LocalPort));
            }
            else
            {
                connectionString = profile.BuildConnectionString(string.IsNullOrEmpty(Password) ? null : Password);
            }

            // Open one real connection before handing anything off. Creating an
            // NpgsqlDataSource opens no socket, so without this a bad password or
            // an unreachable host only surfaced once the main window was already
            // up, in a schema-tree error far from the password field that caused
            // it. The connection goes straight back to the pool, so the main
            // window inherits it warm — this costs a round-trip only in the sense
            // that it moves the first one earlier.
            dataSource = NpgsqlDataSource.Create(connectionString);
            await using (await dataSource.OpenConnectionAsync())
            {
            }

            Connected?.Invoke(dataSource, profile.AccentColor, tunnel);
            dataSource = null; // handed off; the main window owns it now
            RememberLastProfile();
        }
        catch (Exception ex)
        {
            // Anything still owned here is ours to release: the pool if the probe
            // failed (or the hand-off threw, or nothing was listening on
            // Connected), and the SSH session and port forward under it — the
            // main window's Closed handler will never see either.
            if (dataSource is not null)
            {
                await dataSource.DisposeAsync();
            }

            tunnel?.Dispose();
            ErrorMessage = $"Connection failed: {ex.Message}";
        }
        finally
        {
            IsConnecting = false;
        }
    }

    /// <summary>
    /// Files the just-connected profile as "last used". Runs after the hand-off
    /// so a settings write that fails can't take the connection with it — and
    /// swallows its own failure for the same reason: losing the preselection is
    /// not worth failing a working connection over.
    /// </summary>
    private void RememberLastProfile()
    {
        try
        {
            _persistLastProfileId?.Invoke(SelectedProfile?.Id);
        }
        catch
        {
        }
    }

    private bool TryBuildProfile(out ConnectionProfile profile, out string? error)
    {
        // Host / database / username are no longer checked for emptiness: blank
        // means "the default", the same thing the placeholder in the field says.
        if (UseSshTunnel && (string.IsNullOrWhiteSpace(SshHost) || string.IsNullOrWhiteSpace(SshUsername)))
        {
            profile = null!;
            error = "SSH host and username are required when the tunnel is enabled.";
            return false;
        }

        var sshTunnel = UseSshTunnel
            ? new SshTunnelOptions(SshHost, SshPort, SshUsername, SshAuthMethod, string.IsNullOrWhiteSpace(SshPrivateKeyPath) ? null : SshPrivateKeyPath)
            : null;

        profile = new ConnectionProfile(
            SelectedProfile?.Id ?? Guid.NewGuid(),
            EffectiveName,
            EffectiveHost,
            EffectivePort,
            EffectiveDatabase,
            EffectiveUsername,
            SslMode,
            AccentColor,
            sshTunnel);
        error = null;
        return true;
    }

    /// <summary>
    /// SSH credentials are stored via the same ICredentialStore as the DB
    /// password, keyed by a distinct id derived from the connection's own id
    /// (a simple byte-wise XOR) so the two secrets never collide on disk.
    /// </summary>
    private static Guid DeriveSshCredentialId(Guid connectionId)
    {
        var bytes = connectionId.ToByteArray();
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] ^= 0x5A;
        }

        return new Guid(bytes);
    }
}
