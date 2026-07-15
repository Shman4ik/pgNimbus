using System.Collections.ObjectModel;
using System.Reflection;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PgNimbus.Core.Connections;

namespace PgNimbus.App.ViewModels;

public sealed partial class ConnectionDialogViewModel : ObservableObject
{
    private readonly ConnectionProfileStore _store;
    private readonly ICredentialStore _credentialStore;

    public ObservableCollection<ConnectionProfile> Profiles { get; } = [];

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

    [ObservableProperty]
    private ConnectionProfile? _selectedProfile;

    [ObservableProperty]
    private string _name = "New Connection";

    [ObservableProperty]
    private string _host = "localhost";

    [ObservableProperty]
    private int _port = ConnectionProfile.DefaultPort;

    [ObservableProperty]
    private string _database = "postgres";

    [ObservableProperty]
    private string _username = "postgres";

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

    /// <summary>Raised with the built connection string, the profile's accent color, and, if a tunnel was used, the live SshTunnel to keep alive.</summary>
    public event Action<string, string?, SshTunnel?>? Connected;

    public ConnectionDialogViewModel(ConnectionProfileStore store, ICredentialStore credentialStore)
    {
        _store = store;
        _credentialStore = credentialStore;

        Profiles.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasNoProfiles));

        foreach (var profile in _store.Load())
        {
            Profiles.Add(profile);
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
        Name = "New Connection";
        Host = "localhost";
        Port = ConnectionProfile.DefaultPort;
        Database = "postgres";
        Username = "postgres";
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
    partial void OnPortChanged(int value) => SyncImportTextFromFields();
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

            // Give a fresh profile a recognizable name; never rename a saved one.
            if (SelectedProfile is null && parsed.Host is not null)
            {
                Name = parsed.Database is null ? parsed.Host : $"{parsed.Host}/{parsed.Database}";
            }

            ImportText = BuildPreviewConnectionString();
        }
        finally
        {
            _syncingConnectionString = false;
        }
    }

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
            ImportText = BuildPreviewConnectionString();
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

        if (!string.IsNullOrEmpty(Username))
        {
            builder.Append(Uri.EscapeDataString(Username));
            if (!string.IsNullOrEmpty(Password))
            {
                builder.Append(':').Append(maskPassword
                    ? PasswordMask
                    : Uri.EscapeDataString(Password));
            }

            builder.Append('@');
        }

        builder.Append(string.IsNullOrEmpty(Host) ? "localhost" : Host);

        if (Port != ConnectionProfile.DefaultPort)
        {
            builder.Append(':').Append(Port);
        }

        builder.Append('/');
        if (!string.IsNullOrEmpty(Database))
        {
            builder.Append(Uri.EscapeDataString(Database));
        }

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
        StatusMessage = null;
        IsConnecting = true;

        SshTunnel? tunnel = null;
        try
        {
            if (profile.SshTunnel is { } sshOptions)
            {
                tunnel = await Task.Run(() => SshTunnel.Connect(sshOptions, SshPassword, profile.Host, profile.Port));
                var connectionString = profile.BuildConnectionString(
                    string.IsNullOrEmpty(Password) ? null : Password,
                    (tunnel.LocalHost, tunnel.LocalPort));
                Connected?.Invoke(connectionString, profile.AccentColor, tunnel);
            }
            else
            {
                var connectionString = profile.BuildConnectionString(string.IsNullOrEmpty(Password) ? null : Password);
                Connected?.Invoke(connectionString, profile.AccentColor, null);
            }
        }
        catch (Exception ex)
        {
            // If the tunnel came up but the hand-off threw (or nothing was
            // listening on Connected), it owns a live SSH session and port
            // forward the main window's Closed handler will never dispose —
            // release it here so it doesn't leak.
            tunnel?.Dispose();
            ErrorMessage = $"Connection failed: {ex.Message}";
        }
        finally
        {
            IsConnecting = false;
        }
    }

    private bool TryBuildProfile(out ConnectionProfile profile, out string? error)
    {
        if (string.IsNullOrWhiteSpace(Host) || string.IsNullOrWhiteSpace(Database) || string.IsNullOrWhiteSpace(Username))
        {
            profile = null!;
            error = "Host, database, and username are required.";
            return false;
        }

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
            string.IsNullOrWhiteSpace(Name) ? Host : Name,
            Host,
            Port,
            Database,
            Username,
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
