using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PgNimbus.Core.Connections;

namespace PgNimbus.App.ViewModels;

public sealed partial class ConnectionDialogViewModel : ObservableObject
{
    private readonly ConnectionProfileStore _store;
    private readonly ICredentialStore _credentialStore;

    public ObservableCollection<ConnectionProfile> Profiles { get; } = [];

    public IReadOnlyList<SslMode> SslModes { get; } = Enum.GetValues<SslMode>();

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
    private string _password = string.Empty;

    [ObservableProperty]
    private string? _errorMessage;

    /// <summary>Raised with the built connection string when the user clicks Connect.</summary>
    public event Action<string>? Connected;

    public ConnectionDialogViewModel(ConnectionProfileStore store, ICredentialStore credentialStore)
    {
        _store = store;
        _credentialStore = credentialStore;

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
        Password = _credentialStore.LoadPassword(value.Id) ?? string.Empty;
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
        Password = string.Empty;
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

        SelectedProfile = profile;
    }

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
        New();
    }

    [RelayCommand]
    private void Connect()
    {
        if (!TryBuildProfile(out var profile, out var error))
        {
            ErrorMessage = error;
            return;
        }

        ErrorMessage = null;
        var connectionString = profile.BuildConnectionString(string.IsNullOrEmpty(Password) ? null : Password);
        Connected?.Invoke(connectionString);
    }

    private bool TryBuildProfile(out ConnectionProfile profile, out string? error)
    {
        if (string.IsNullOrWhiteSpace(Host) || string.IsNullOrWhiteSpace(Database) || string.IsNullOrWhiteSpace(Username))
        {
            profile = null!;
            error = "Host, database, and username are required.";
            return false;
        }

        profile = new ConnectionProfile(
            SelectedProfile?.Id ?? Guid.NewGuid(),
            string.IsNullOrWhiteSpace(Name) ? Host : Name,
            Host,
            Port,
            Database,
            Username,
            SslMode);
        error = null;
        return true;
    }
}
