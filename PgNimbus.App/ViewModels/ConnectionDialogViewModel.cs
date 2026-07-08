using System.Collections.ObjectModel;
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

    [ObservableProperty]
    private bool _isConnecting;

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

            if (parsed.Password is not null)
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

    /// <summary>Renders the current fields as a postgres:// URI — the most widely recognized connection string format.</summary>
    private string BuildPreviewConnectionString()
    {
        var builder = new StringBuilder("postgres://");

        if (!string.IsNullOrEmpty(Username))
        {
            builder.Append(Uri.EscapeDataString(Username));
            if (!string.IsNullOrEmpty(Password))
            {
                builder.Append(':').Append(Uri.EscapeDataString(Password));
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

    [RelayCommand]
    private async Task ConnectAsync()
    {
        if (!TryBuildProfile(out var profile, out var error))
        {
            ErrorMessage = error;
            return;
        }

        ErrorMessage = null;
        IsConnecting = true;

        try
        {
            if (profile.SshTunnel is { } sshOptions)
            {
                var tunnel = await Task.Run(() => SshTunnel.Connect(sshOptions, SshPassword, profile.Host, profile.Port));
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
            ErrorMessage = $"SSH tunnel failed: {ex.Message}";
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
