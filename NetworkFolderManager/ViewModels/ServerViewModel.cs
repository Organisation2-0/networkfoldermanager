using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NetworkFolderManager.Dialogs;
using NetworkFolderManager.Models;
using NetworkFolderManager.Services;
using System.Collections.ObjectModel;
using System.Windows;
using Application = System.Windows.Application;

namespace NetworkFolderManager.ViewModels;

/// <summary>Ein NFM-Server in der Liste (gespeichert und/oder im Netzwerk gefunden).</summary>
public partial class ServerEntry : ObservableObject
{
    public string Id { get; init; } = string.Empty;
    public SavedServer? Saved { get; set; }
    public VaultClient? Client { get; set; }

    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _host = string.Empty;
    [ObservableProperty] private int _port = VaultProtocol.DefaultPort;
    [ObservableProperty] private string _fingerprint = string.Empty;
    [ObservableProperty] private bool _isOnline;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLoggedIn), nameof(IsAdmin), nameof(UserText))]
    private UserDto? _user;
    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private bool _fingerprintChanged;

    public bool IsLoggedIn => User != null;
    public bool IsAdmin => User?.IsAdmin == true;
    public string Address => $"{Host}:{Port}";
    public string WebUrl => $"https://{(Host.Contains(':') ? $"[{Host}]" : Host)}:{Port}/";
    public string FingerprintShort => SavedServer.FormatFingerprint(Fingerprint);
    public string UserText => User == null ? Loc.T("nicht angemeldet") : Loc.T("angemeldet als {0}", User.DisplayName) + (User.IsAdmin ? " · Admin" : "");

    partial void OnHostChanged(string value)
    {
        OnPropertyChanged(nameof(Address));
        OnPropertyChanged(nameof(WebUrl));
    }

    partial void OnPortChanged(int value)
    {
        OnPropertyChanged(nameof(Address));
        OnPropertyChanged(nameof(WebUrl));
    }
    partial void OnFingerprintChanged(string value) => OnPropertyChanged(nameof(FingerprintShort));
}

public class ServerFolderItem
{
    public required FolderDto Dto { get; init; }
    public required ServerEntry Server { get; init; }
    public string Name => Dto.Name;
    public bool IsMine => Dto.IsMine;
    public bool MustChangePassword => Dto.MustChangePassword && Dto.IsMine;
    public string Subtitle => Dto.IsMine
        ? Loc.T("Mein Ordner · erstellt {0:d}", Dto.CreatedAt.ToLocalTime())
        : Loc.T("Ordner von {0}", Dto.OwnerName);
}

public class AdminFolderItem
{
    public required FolderDto Dto { get; init; }
    public string Name => Dto.Name;
    public string OwnerName => Dto.OwnerName;
    public string CreatedText => Dto.CreatedAt.ToLocalTime().ToString("g") + (string.IsNullOrEmpty(Dto.CreatedBy) ? "" : " · " + Dto.CreatedBy);
    public string SizeText => VaultFormat.Size(Dto.StorageBytes ?? 0);
    public string StatusText => Dto.MustChangePassword ? Loc.T("wartet auf erstes Öffnen") : Loc.T("aktiv");
    public bool Waiting => Dto.MustChangePassword;
}

public class AdminUserItem
{
    public required UserDto Dto { get; init; }
    public string DisplayName => Dto.DisplayName;
    public string Username => "@" + Dto.Username;
    public string RoleText => (Dto.IsAdmin ? "Admin" : Loc.T("Mitglied")) + (Dto.MustChangePassword ? " · " + Loc.T("Startpasswort") : "");
    public string FoldersText => Loc.T("{0} Ordner", Dto.FolderCount);
    public string LastLoginText => Dto.LastLogin is { } t ? Loc.T("zuletzt {0:g}", t.ToLocalTime()) : Loc.T("noch nie angemeldet");
    public bool IsAdmin => Dto.IsAdmin;
}

public static class VaultFormat
{
    public static string Size(long bytes)
    {
        if (bytes < 1024)
            return bytes + " B";
        string[] units = { "KB", "MB", "GB", "TB" };
        double v = bytes;
        var i = -1;
        do
        {
            v /= 1024;
            i++;
        } while (v >= 1024 && i < units.Length - 1);
        return v.ToString(v < 10 ? "0.#" : "0") + " " + units[i];
    }
}

/// <summary>
/// Tab „Server“: NFM-Server finden, anmelden (Profile liegen auf dem Server → auch von anderen Geräten nutzbar),
/// verschlüsselte Ordner anlegen/öffnen – sowie der Admin-Bereich.
/// </summary>
public partial class ServerViewModel : ObservableObject, IDisposable
{
    private readonly AppSettings _settings;
    private readonly Dictionary<string, Views.VaultFolderWindow> _openWindows = new();
    private System.Windows.Threading.DispatcherTimer? _searchTimer;

    public ObservableCollection<ServerEntry> Servers { get; } = new();
    public ObservableCollection<ServerFolderItem> Folders { get; } = new();
    public ObservableCollection<ServerEntry> AdminServers { get; } = new();
    public ObservableCollection<AdminFolderItem> AdminFolders { get; } = new();
    public ObservableCollection<AdminUserItem> AdminUsers { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoginCommand), nameof(RegisterCommand), nameof(LogoutCommand), nameof(CreateFolderCommand),
        nameof(RemoveServerCommand), nameof(ChangeAccountPasswordCommand), nameof(RefreshFoldersCommand))]
    [NotifyPropertyChangedFor(nameof(HasSelectedServer))]
    private ServerEntry? _selectedServer;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenFolderCommand), nameof(DeleteFolderCommand))]
    private ServerFolderItem? _selectedFolder;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SearchCommand))]
    private bool _isSearching;

    [ObservableProperty] private string _manualAddress = string.Empty;
    [ObservableProperty] private string _status = string.Empty;
    [ObservableProperty] private bool _isBusy;

    public bool HasSelectedServer => SelectedServer != null;
    public bool HasAdminAccess => AdminServers.Count > 0;

    /// <summary>Admin-Zugang hat sich geändert (Tab ein-/ausblenden).</summary>
    public event EventHandler? AdminAccessChanged;

    public ServerViewModel(AppSettings settings)
    {
        _settings = settings;
        foreach (var saved in _settings.Servers)
        {
            var entry = new ServerEntry
            {
                Id = saved.Id, Saved = saved, Name = saved.Name, Host = saved.Host, Port = saved.Port, Fingerprint = saved.Fingerprint
            };
            try
            {
                entry.Client = new VaultClient(saved) { Token = TokenStore.Load(saved.Id) };
            }
            catch (Exception ex)
            {
                entry.StatusText = ex.Message;
            }
            Servers.Add(entry);
        }
    }

    private static Window? Owner => Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? Application.Current.MainWindow;

    /// <summary>Gespeicherte Anmeldungen prüfen und Netzwerk durchsuchen.</summary>
    public async Task StartAsync()
    {
        await Task.WhenAll(Servers.Where(s => s.Client?.Token != null).Select(RestoreLoginAsync));
        UpdateAdminServers();
        await SearchAsync();

        _searchTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMinutes(2) };
        _searchTimer.Tick += async (_, _) =>
        {
            if (!IsSearching)
                await SearchCoreAsync(quiet: true);
        };
        _searchTimer.Start();
    }

    #region Sprache (im Profil gespeichert, geräteübergreifend)

    private bool _languageEventRaised;

    /// <summary>
    /// Die Sprache aus dem Profil weicht von der laufenden Programmsprache ab (Parameter: "de"/"en").
    /// Sie ist bereits gespeichert und wirkt nach einem Neustart.
    /// </summary>
    public event EventHandler<string>? ProfileLanguageApplied;

    /// <summary>Gewählte Sprache in allen angemeldeten Profilen speichern.</summary>
    public async Task SyncLanguageAsync(string language)
    {
        foreach (var s in Servers.Where(s => s.IsLoggedIn && s.User?.Language != language).ToList())
        {
            try
            {
                s.User = await s.Client!.SetLanguageAsync(language);
            }
            catch (Exception ex)
            {
                LogService.Info($"Sprache für {s.Name} nicht gespeichert: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Nach der Anmeldung: Hat das Profil eine Sprache, wird sie übernommen (auch für alle anderen Server);
    /// sonst wird die aktuelle Programmsprache ins Profil geschrieben.
    /// </summary>
    private async Task ApplyProfileLanguageAsync(ServerEntry entry)
    {
        if (entry.User is not { MustChangePassword: false } user)
            return;

        if (user.Language is not ("de" or "en"))
        {
            await SyncLanguageAsync(Loc.SavedLanguage is "de" or "en" ? Loc.SavedLanguage : Loc.Language);
            return;
        }

        if (Loc.SavedLanguage != user.Language)
            Loc.SavedLanguage = user.Language;
        await SyncLanguageAsync(user.Language);

        if (user.Language != Loc.Language && !_languageEventRaised)
        {
            _languageEventRaised = true;
            ProfileLanguageApplied?.Invoke(this, user.Language);
        }
    }

    #endregion

    private async Task RestoreLoginAsync(ServerEntry entry)
    {
        try
        {
            entry.User = await entry.Client!.MeAsync();
            entry.IsOnline = true;
            entry.StatusText = string.Empty;
            await ApplyProfileLanguageAsync(entry);
        }
        catch (VaultApiException ex) when (ex.Status == 401)
        {
            TokenStore.Delete(entry.Id);
            entry.Client!.Token = null;
            entry.User = null;
            entry.IsOnline = true;
        }
        catch (VaultApiException ex) when (ex.Code == "mustChangeAccountPassword")
        {
            entry.IsOnline = true;
        }
        catch (VaultApiException ex)
        {
            entry.IsOnline = false;
            entry.StatusText = ex.Code == "certificate" ? ex.Message : Loc.T("offline");
        }
    }

    #region Server finden / hinzufügen

    private bool CanSearch() => !IsSearching;

    [RelayCommand(CanExecute = nameof(CanSearch))]
    private Task SearchAsync() => SearchCoreAsync(quiet: false);

    private async Task SearchCoreAsync(bool quiet)
    {
        IsSearching = true;
        if (!quiet)
            Status = Loc.T("Suche Server im Netzwerk...");
        try
        {
            var found = await ServerDiscoveryClient.SearchAsync(TimeSpan.FromSeconds(2.5));
            foreach (var a in found)
            {
                var entry = Servers.FirstOrDefault(s => s.Id == a.Id);
                if (entry == null)
                {
                    entry = new ServerEntry { Id = a.Id, Name = a.Name, Host = a.Address, Port = a.Port, Fingerprint = a.Fp };
                    Servers.Add(entry);
                }
                else if (entry.Saved != null && !string.Equals(entry.Saved.Fingerprint, a.Fp, StringComparison.OrdinalIgnoreCase))
                {
                    // Gleiche Server-ID, aber anderes Zertifikat → nicht automatisch übernehmen
                    entry.FingerprintChanged = true;
                    entry.StatusText = Loc.T("Achtung: Zertifikat hat sich geändert!");
                    continue;
                }
                else if (entry.Saved != null && (entry.Saved.Host != a.Address || entry.Saved.Port != a.Port))
                {
                    // Neue IP-Adresse (z.B. DHCP) → übernehmen
                    entry.Saved.Host = a.Address;
                    entry.Saved.Port = a.Port;
                    entry.Saved.Name = a.Name;
                    SaveServers();
                    var token = entry.Client?.Token;
                    entry.Client?.Dispose();
                    entry.Client = new VaultClient(entry.Saved) { Token = token };
                }
                entry.Name = a.Name;
                entry.Host = a.Address;
                entry.Port = a.Port;
                entry.IsOnline = true;
                if (entry.Client?.Token != null && entry.User == null)
                    await RestoreLoginAsync(entry);
            }

            foreach (var s in Servers.Where(s => found.All(f => f.Id != s.Id) && s.User == null))
                s.IsOnline = false;

            if (!quiet)
                Status = found.Count == 0
                    ? Loc.T("Kein Server gefunden. Tipp: Adresse (IP:Port) manuell hinzufügen.")
                    : Loc.T("{0} Server gefunden.", found.Count);
            SelectedServer ??= Servers.FirstOrDefault(s => s.IsLoggedIn) ?? Servers.FirstOrDefault();
            UpdateAdminServers();
        }
        catch (Exception ex)
        {
            if (!quiet)
                Status = Loc.T("Suche fehlgeschlagen: {0}", ex.Message);
        }
        finally
        {
            IsSearching = false;
        }
    }

    /// <summary>Server per Adresse hinzufügen (z.B. "192.168.1.20" oder "nas:45830").</summary>
    [RelayCommand]
    private async Task AddManualAsync()
    {
        var text = ManualAddress.Trim();
        if (text.Length == 0)
            return;
        text = text.Replace("https://", "", StringComparison.OrdinalIgnoreCase).TrimEnd('/');
        var host = text;
        var port = VaultProtocol.DefaultPort;
        var colon = text.LastIndexOf(':');
        if (colon > 0 && !text.EndsWith(']') && int.TryParse(text[(colon + 1)..], out var p) && text.Count(c => c == ':') == 1)
        {
            host = text[..colon];
            port = p;
        }

        IsBusy = true;
        Status = Loc.T("Verbinde mit {0}...", text);
        try
        {
            var (info, ca) = await VaultClient.ProbeAsync(host, port);
            var entry = Servers.FirstOrDefault(s => s.Id == info.Id);
            if (entry?.Saved == null)
            {
                var ok = FormDialog.Confirm(Owner, Loc.T("Server hinzufügen?"),
                    Loc.T("„{0}“ ({1}:{2})\n\nFingerabdruck: {3}\n\nVergleiche ihn mit der Ausgabe von „nfm-server info“ auf dem Server oder mit der Web-Oberfläche.",
                        info.Name, host, port, SavedServer.FormatFingerprint(info.Fingerprint)),
                    Loc.T("Hinzufügen"), danger: false);
                if (!ok)
                    return;
            }
            entry = SaveServer(info, host, port, ca);
            entry.IsOnline = true;
            SelectedServer = entry;
            ManualAddress = string.Empty;
            Status = Loc.T("Server {0} hinzugefügt.", info.Name);
        }
        catch (Exception ex)
        {
            Status = Loc.T("Server nicht erreichbar: {0}", ex.GetBaseException().Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private ServerEntry SaveServer(ServerInfoDto info, string host, int port, byte[] caDer)
    {
        var saved = _settings.Servers.FirstOrDefault(s => s.Id == info.Id);
        if (saved == null)
        {
            saved = new SavedServer { Id = info.Id };
            _settings.Servers.Add(saved);
        }
        saved.Name = info.Name;
        saved.Host = host;
        saved.Port = port;
        saved.Fingerprint = info.Fingerprint;
        saved.CaCertificate = Convert.ToBase64String(caDer);
        SaveServers();

        var entry = Servers.FirstOrDefault(s => s.Id == info.Id);
        if (entry == null)
        {
            entry = new ServerEntry { Id = info.Id };
            Servers.Add(entry);
        }
        var token = entry.Client?.Token;
        entry.Client?.Dispose();
        entry.Saved = saved;
        entry.Client = new VaultClient(saved) { Token = token };
        entry.Name = info.Name;
        entry.Host = host;
        entry.Port = port;
        entry.Fingerprint = info.Fingerprint;
        entry.FingerprintChanged = false;
        entry.StatusText = string.Empty;
        return entry;
    }

    private void SaveServers() => SettingsService.Save(_settings);

    /// <summary>Für einen nur gefundenen Server das Zertifikat holen und mit der Erkennung vergleichen.</summary>
    private async Task<bool> EnsureSavedAsync(ServerEntry entry)
    {
        if (entry.Saved != null && entry.Client != null && !entry.FingerprintChanged)
            return true;
        try
        {
            var (info, ca) = await VaultClient.ProbeAsync(entry.Host, entry.Port);
            if (entry.FingerprintChanged || (entry.Saved == null && !string.Equals(info.Fingerprint, entry.Fingerprint, StringComparison.OrdinalIgnoreCase)))
            {
                var ok = FormDialog.Confirm(Owner, Loc.T("Zertifikat geändert"),
                    Loc.T("Der Server „{0}“ meldet ein anderes Zertifikat als bisher.\n\nNeuer Fingerabdruck: {1}\n\nNur bestätigen, wenn der Server neu eingerichtet wurde – sonst könnte sich jemand als dieser Server ausgeben.",
                        info.Name, SavedServer.FormatFingerprint(info.Fingerprint)),
                    Loc.T("Neuem Zertifikat vertrauen"));
                if (!ok)
                    return false;
                TokenStore.Delete(info.Id);
                if (entry.Client != null)
                    entry.Client.Token = null;
                entry.User = null;
            }
            SaveServer(info, entry.Host, entry.Port, ca);
            return true;
        }
        catch (Exception ex)
        {
            Status = Loc.T("Server nicht erreichbar: {0}", ex.GetBaseException().Message);
            return false;
        }
    }

    private bool HasServer() => SelectedServer != null;

    /// <summary>Web-Oberfläche des Servers im Browser öffnen (wie auf dem Mac).</summary>
    [RelayCommand]
    private void OpenWeb()
    {
        if (SelectedServer is { } entry)
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(entry.WebUrl) { UseShellExecute = true });
    }

    [RelayCommand(CanExecute = nameof(HasServer))]
    private void RemoveServer()
    {
        if (SelectedServer is not { } entry)
            return;
        if (!FormDialog.Confirm(Owner, Loc.T("Server entfernen"), Loc.T("„{0}“ aus der Liste entfernen? Du wirst auf diesem PC abgemeldet.", entry.Name), Loc.T("Entfernen")))
            return;
        TokenStore.Delete(entry.Id);
        _settings.Servers.RemoveAll(s => s.Id == entry.Id);
        SaveServers();
        entry.Client?.Dispose();
        Servers.Remove(entry);
        SelectedServer = Servers.FirstOrDefault();
        UpdateAdminServers();
    }

    #endregion

    #region Anmeldung

    partial void OnSelectedServerChanged(ServerEntry? value) => _ = LoadFoldersAsync();

    private bool CanLogin() => SelectedServer is { IsLoggedIn: false };

    [RelayCommand(CanExecute = nameof(CanLogin))]
    private async Task LoginAsync()
    {
        if (SelectedServer is not { } entry || !await EnsureSavedAsync(entry))
            return;

        string? password = null;
        var result = FormDialog.Show(Owner, Loc.T("Anmelden bei {0}", entry.Name),
            Loc.T("Mit deinem Profil auf diesem Server – es funktioniert auf jedem Gerät (Windows-App oder Browser)."),
            new[]
            {
                FormField.Text("user", Loc.T("Benutzername"), entry.Saved?.Username ?? ""),
                FormField.Password("pw", Loc.T("Passwort"))
            },
            Loc.T("Anmelden"),
            async v =>
            {
                entry.User = await entry.Client!.LoginAsync(v["user"], v["pw"]);
                password = v["pw"];
            });
        if (result == null)
            return;

        await AfterLoginAsync(entry, password!);
    }

    private async Task AfterLoginAsync(ServerEntry entry, string currentPassword)
    {
        entry.IsOnline = true;
        entry.Saved!.Username = entry.User!.Username;
        SaveServers();

        if (entry.User.MustChangePassword)
        {
            var changed = FormDialog.Show(Owner, Loc.T("Eigenes Passwort festlegen"),
                Loc.T("Dein Profil wurde vom Admin angelegt oder zurückgesetzt. Bitte wähle jetzt ein eigenes Passwort."),
                new[] { FormField.Password("pw", Loc.T("Neues Passwort"), Loc.T("Mindestens 8 Zeichen")), FormField.Password("pw2", Loc.T("Passwort wiederholen")) },
                Loc.T("Speichern"),
                async v =>
                {
                    FormDialog.CheckNewPassword(v["pw"], v["pw2"]);
                    entry.User = await entry.Client!.ChangePasswordAsync(currentPassword, v["pw"]);
                });
            if (changed == null)
            {
                await entry.Client!.LogoutAsync();
                entry.User = null;
                return;
            }
        }

        try
        {
            TokenStore.Save(entry.Id, entry.User!.Username, entry.Client!.Token!);
        }
        catch (Exception ex)
        {
            LogService.Error("TokenStore", ex);
        }
        Status = Loc.T("Angemeldet bei {0} als {1}.", entry.Name, entry.User!.DisplayName);
        await ApplyProfileLanguageAsync(entry);
        UpdateAdminServers();
        RefreshCommands();
        await LoadFoldersAsync();
    }

    [RelayCommand(CanExecute = nameof(CanLogin))]
    private async Task RegisterAsync()
    {
        if (SelectedServer is not { } entry || !await EnsureSavedAsync(entry))
            return;

        ServerInfoDto info;
        try
        {
            info = await entry.Client!.InfoAsync();
        }
        catch (Exception ex)
        {
            Status = ex.Message;
            return;
        }
        if (!info.AllowRegistration && !info.NeedsSetup)
        {
            FormDialog.Show(Owner, Loc.T("Profil anlegen"), Loc.T("Auf diesem Server können nur Admins neue Profile anlegen. Bitte den Admin fragen."),
                Array.Empty<FormField>(), "OK");
            return;
        }

        var fields = new List<FormField>
        {
            FormField.Text("user", Loc.T("Benutzername"), "", Loc.T("3–32 Zeichen: Buchstaben, Ziffern, . _ -")),
            FormField.Text("name", Loc.T("Anzeigename (optional)"), _settings.DisplayName),
            FormField.Password("pw", Loc.T("Passwort"), Loc.T("Mindestens 8 Zeichen")),
            FormField.Password("pw2", Loc.T("Passwort wiederholen"))
        };
        if (info.NeedsSetup)
            fields.Add(FormField.Text("code", Loc.T("Setup-Code (macht dich zum Admin)"), "", Loc.T("Steht im Server-Protokoll bzw. in der Ausgabe von „nfm-server info“.")));

        string? password = null;
        var result = FormDialog.Show(Owner, Loc.T("Profil anlegen auf {0}", entry.Name),
            Loc.T("Das Profil wird auf dem Server gespeichert – du kannst dich damit auf jedem Gerät anmelden."),
            fields, Loc.T("Profil anlegen"),
            async v =>
            {
                FormDialog.CheckNewPassword(v["pw"], v["pw2"]);
                entry.User = await entry.Client!.RegisterAsync(v["user"], v["pw"], v["name"],
                    v.TryGetValue("code", out var code) && code.Length > 0 ? code : null);
                password = v["pw"];
            },
            notice: info.NeedsSetup ? Loc.T("Dieser Server hat noch keinen Admin. Mit dem Setup-Code wird dieses Profil zum Admin.") : null);
        if (result != null)
            await AfterLoginAsync(entry, password!);
    }

    private bool IsLoggedIn() => SelectedServer is { IsLoggedIn: true };

    [RelayCommand(CanExecute = nameof(IsLoggedIn))]
    private async Task LogoutAsync()
    {
        if (SelectedServer is not { } entry)
            return;
        try
        {
            await entry.Client!.LogoutAsync();
        }
        catch
        {
            // Server offline – lokal trotzdem abmelden
        }
        TokenStore.Delete(entry.Id);
        entry.User = null;
        Folders.Clear();
        UpdateAdminServers();
        RefreshCommands();
        Status = Loc.T("Abgemeldet von {0}.", entry.Name);
    }

    [RelayCommand(CanExecute = nameof(IsLoggedIn))]
    private void ChangeAccountPassword()
    {
        if (SelectedServer is not { } entry)
            return;
        var result = FormDialog.Show(Owner, Loc.T("Passwort ändern"), Loc.T("Passwort deines Profils auf {0}.", entry.Name),
            new[]
            {
                FormField.Password("old", Loc.T("Bisheriges Passwort")),
                FormField.Password("pw", Loc.T("Neues Passwort"), Loc.T("Mindestens 8 Zeichen")),
                FormField.Password("pw2", Loc.T("Passwort wiederholen"))
            },
            Loc.T("Speichern"),
            async v =>
            {
                FormDialog.CheckNewPassword(v["pw"], v["pw2"]);
                entry.User = await entry.Client!.ChangePasswordAsync(v["old"], v["pw"]);
            });
        if (result != null)
            Status = Loc.T("Passwort geändert.");
    }

    private void RefreshCommands()
    {
        LoginCommand.NotifyCanExecuteChanged();
        RegisterCommand.NotifyCanExecuteChanged();
        LogoutCommand.NotifyCanExecuteChanged();
        CreateFolderCommand.NotifyCanExecuteChanged();
        ChangeAccountPasswordCommand.NotifyCanExecuteChanged();
        RefreshFoldersCommand.NotifyCanExecuteChanged();
    }

    #endregion

    #region Ordner

    [RelayCommand(CanExecute = nameof(IsLoggedIn))]
    private Task RefreshFoldersAsync() => LoadFoldersAsync();

    private async Task LoadFoldersAsync()
    {
        Folders.Clear();
        RefreshCommands();
        if (SelectedServer is not { IsLoggedIn: true } entry)
            return;
        try
        {
            var list = await entry.Client!.FoldersAsync();
            if (SelectedServer != entry)
                return;
            foreach (var f in list)
                Folders.Add(new ServerFolderItem { Dto = f, Server = entry });
            entry.IsOnline = true;
        }
        catch (VaultApiException ex)
        {
            HandleApiError(entry, ex);
        }
    }

    private void HandleApiError(ServerEntry entry, VaultApiException ex)
    {
        if (ex.Status == 401)
        {
            TokenStore.Delete(entry.Id);
            entry.Client!.Token = null;
            entry.User = null;
            UpdateAdminServers();
            RefreshCommands();
            Status = Loc.T("Anmeldung bei {0} abgelaufen – bitte erneut anmelden.", entry.Name);
            return;
        }
        if (ex.Status == 0)
            entry.IsOnline = false;
        Status = ex.Message;
    }

    [RelayCommand(CanExecute = nameof(IsLoggedIn))]
    private async Task CreateFolderAsync()
    {
        if (SelectedServer is not { } entry)
            return;
        FolderDto? created = null;
        string? password = null;
        var result = FormDialog.Show(Owner, Loc.T("Neuer Ordner auf {0}", entry.Name),
            Loc.T("Der Ordner wird mit diesem Passwort verschlüsselt. Jeder, der es kennt, kann ihn öffnen – der Admin nicht."),
            new[]
            {
                FormField.Text("name", Loc.T("Name")),
                FormField.Password("pw", Loc.T("Ordnerpasswort"), Loc.T("Mindestens 8 Zeichen. Vergessen = Inhalt nicht wiederherstellbar!")),
                FormField.Password("pw2", Loc.T("Passwort wiederholen"))
            },
            Loc.T("Erstellen"),
            async v =>
            {
                FormDialog.CheckNewPassword(v["pw"], v["pw2"]);
                created = await entry.Client!.CreateFolderAsync(v["name"], v["pw"]);
                password = v["pw"];
            });
        if (result == null || created == null)
            return;

        await LoadFoldersAsync();
        try
        {
            var unlock = await entry.Client!.UnlockAsync(created.Id, password!);
            ShowFolderWindow(entry, created, unlock, password!);
        }
        catch (VaultApiException ex)
        {
            Status = ex.Message;
        }
    }

    private bool HasFolder() => SelectedFolder != null;

    [RelayCommand(CanExecute = nameof(HasFolder))]
    private async Task OpenFolderAsync()
    {
        if (SelectedFolder is not { } item)
            return;
        await OpenFolderAsync(item);
    }

    public async Task OpenFolderAsync(ServerFolderItem item)
    {
        if (_openWindows.TryGetValue(item.Dto.Id, out var open))
        {
            open.Activate();
            return;
        }

        var entry = item.Server;
        var f = item.Dto;
        UnlockResponse? unlock = null;
        string? password = null;
        var mustChange = false;
        var first = f.MustChangePassword && f.IsMine;

        var result = FormDialog.Show(Owner, f.Name,
            f.IsMine ? Loc.T("Ordnerpasswort eingeben.") : Loc.T("Ordner von {0} – Ordnerpasswort eingeben.", f.OwnerName),
            new[] { FormField.Password("pw", first ? Loc.T("Startpasswort (vom Admin)") : Loc.T("Ordnerpasswort")) },
            first ? Loc.T("Weiter") : Loc.T("Öffnen"),
            async v =>
            {
                try
                {
                    unlock = await entry.Client!.UnlockAsync(f.Id, v["pw"]);
                }
                catch (VaultApiException ex) when (ex.Code == "mustChangePassword")
                {
                    mustChange = true;
                }
                password = v["pw"];
            },
            notice: first ? Loc.T("Dieser Ordner wurde vom Admin für dich angelegt. Gib das Startpasswort ein und lege danach ein eigenes fest – dabei wird ein neuer Schlüssel erzeugt, der Admin kann deine Dateien also nicht sehen.") : null);
        if (result == null)
            return;

        if (mustChange)
        {
            var changed = FormDialog.Show(Owner, Loc.T("Eigenes Ordnerpasswort festlegen"),
                Loc.T("Wähle ein Passwort, das nur du kennst. Ab jetzt ist „{0}“ damit verschlüsselt.", f.Name),
                new[] { FormField.Password("pw", Loc.T("Neues Passwort"), Loc.T("Mindestens 8 Zeichen")), FormField.Password("pw2", Loc.T("Passwort wiederholen")) },
                Loc.T("Speichern"),
                async v =>
                {
                    FormDialog.CheckNewPassword(v["pw"], v["pw2"]);
                    unlock = await entry.Client!.ChangeFolderPasswordAsync(f.Id, password!, v["pw"]);
                    password = v["pw"];
                });
            if (changed == null)
                return;
            f.MustChangePassword = false;
            await LoadFoldersAsync();
        }

        if (unlock != null)
            ShowFolderWindow(entry, f, unlock, password!);
    }

    private void ShowFolderWindow(ServerEntry entry, FolderDto folder, UnlockResponse unlock, string password)
    {
        var vm = new VaultFolderViewModel(entry, folder, unlock, password);
        var window = new Views.VaultFolderWindow(vm);
        _openWindows[folder.Id] = window;
        window.Closed += (_, _) =>
        {
            _openWindows.Remove(folder.Id);
            vm.Dispose();
            _ = LoadFoldersAsync();
        };
        window.Show();
        window.Activate();
    }

    private bool CanDeleteFolder() => SelectedFolder is { IsMine: true };

    [RelayCommand(CanExecute = nameof(HasFolder))]
    private async Task DeleteFolderAsync()
    {
        if (SelectedFolder is not { } item)
            return;
        if (!item.IsMine)
        {
            Status = Loc.T("Nur der Besitzer (oder der Admin) kann einen Ordner löschen.");
            return;
        }
        var result = FormDialog.Show(Owner, Loc.T("Ordner löschen"),
            Loc.T("„{0}“ und alle Dateien darin werden endgültig gelöscht. Zur Bestätigung das Ordnerpasswort eingeben.", item.Name),
            new[] { FormField.Password("pw", Loc.T("Ordnerpasswort")) },
            Loc.T("Endgültig löschen"),
            async v => await item.Server.Client!.DeleteFolderAsync(item.Dto.Id, v["pw"]),
            danger: true);
        if (result == null)
            return;
        if (_openWindows.TryGetValue(item.Dto.Id, out var w))
            w.Close();
        Status = Loc.T("Ordner {0} gelöscht.", item.Name);
        await LoadFoldersAsync();
    }

    #endregion

    #region Admin

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAdminServer))]
    private ServerEntry? _adminServer;

    [ObservableProperty] private AdminFolderItem? _selectedAdminFolder;
    [ObservableProperty] private AdminUserItem? _selectedAdminUser;
    [ObservableProperty] private string _adminServerName = string.Empty;
    [ObservableProperty] private bool _adminAllowRegistration;
    [ObservableProperty] private string _adminStatus = string.Empty;

    public bool HasAdminServer => AdminServer != null;

    private void UpdateAdminServers()
    {
        var had = AdminServers.Count > 0;
        var admins = Servers.Where(s => s.IsAdmin).ToList();
        foreach (var s in AdminServers.Except(admins).ToList())
            AdminServers.Remove(s);
        foreach (var s in admins.Except(AdminServers).ToList())
            AdminServers.Add(s);
        if (AdminServer == null || !AdminServers.Contains(AdminServer))
            AdminServer = AdminServers.FirstOrDefault();
        OnPropertyChanged(nameof(HasAdminAccess));
        if (had != AdminServers.Count > 0)
            AdminAccessChanged?.Invoke(this, EventArgs.Empty);
    }

    partial void OnAdminServerChanged(ServerEntry? value) => _ = AdminRefreshAsync();

    [RelayCommand]
    private async Task AdminRefreshAsync()
    {
        AdminFolders.Clear();
        AdminUsers.Clear();
        if (AdminServer is not { IsAdmin: true } entry)
            return;
        try
        {
            var folders = entry.Client!.AdminFoldersAsync();
            var users = entry.Client.AdminUsersAsync();
            var settings = entry.Client.AdminSettingsAsync();
            foreach (var f in await folders)
                AdminFolders.Add(new AdminFolderItem { Dto = f });
            foreach (var u in await users)
                AdminUsers.Add(new AdminUserItem { Dto = u });
            var s = await settings;
            AdminServerName = s.Name;
            AdminAllowRegistration = s.AllowRegistration;
            _loadingAutoUpdate = true;
            AdminAutoUpdate = s.AutoUpdate ?? true;
            _loadingAutoUpdate = false;
            try
            {
                ShowUpdate(await entry.Client.AdminUpdateStatusAsync());
            }
            catch (VaultApiException)
            {
                ShowUpdate(null); // älterer Server ohne Update-Funktion
            }
            AdminStatus = Loc.T("{0} Ordner · {1} Benutzer", AdminFolders.Count, AdminUsers.Count);
        }
        catch (VaultApiException ex)
        {
            AdminStatus = ex.Message;
            if (ex.Status is 401 or 403)
            {
                HandleApiError(entry, ex);
                if (entry.IsLoggedIn)
                    await RestoreLoginAsync(entry);
                UpdateAdminServers();
            }
        }
    }

    private IReadOnlyList<(string, string)> UserChoices() =>
        AdminUsers.Select(u => (u.Dto.Username, $"{u.Dto.DisplayName} (@{u.Dto.Username})")).ToList();

    [RelayCommand]
    private async Task AdminCreateFolderAsync()
    {
        if (AdminServer is not { } entry)
            return;
        var startPw = FormDialog.RandomPassword();
        var result = FormDialog.Show(Owner, Loc.T("Ordner für Benutzer anlegen"),
            Loc.T("Gib dem Benutzer das Startpasswort. Beim ersten Öffnen muss er ein eigenes Passwort festlegen – dabei wird ein neuer Schlüssel erzeugt. Du kannst den Inhalt danach nicht sehen."),
            new[]
            {
                FormField.Choice("owner", Loc.T("Für Benutzer"), UserChoices(), AdminUsers.FirstOrDefault(u => u.Dto.Id != entry.User!.Id)?.Dto.Username ?? ""),
                FormField.Text("name", Loc.T("Ordnername")),
                FormField.Text("pw", Loc.T("Startpasswort"), startPw, Loc.T("Zufällig erzeugt – notieren und dem Benutzer geben."))
            },
            Loc.T("Anlegen"),
            async v =>
            {
                FormDialog.CheckNewPassword(v["pw"], v["pw"]);
                await entry.Client!.AdminCreateFolderAsync(v["name"], v["pw"], v["owner"]);
            });
        if (result == null)
            return;
        System.Windows.Clipboard.SetText(result["pw"]);
        AdminStatus = Loc.T("Ordner „{0}“ angelegt. Startpasswort (in die Zwischenablage kopiert): {1}", result["name"], result["pw"]);
        await AdminRefreshAsync();
        await LoadFoldersAsync();
    }

    [RelayCommand]
    private async Task AdminDeleteFolderAsync(AdminFolderItem? item)
    {
        item ??= SelectedAdminFolder;
        if (AdminServer is not { } entry || item == null)
            return;
        if (!FormDialog.Confirm(Owner, Loc.T("Ordner löschen"),
                Loc.T("„{0}“ von {1} und alle Dateien darin werden endgültig gelöscht.", item.Name, item.OwnerName), Loc.T("Endgültig löschen")))
            return;
        try
        {
            await entry.Client!.AdminDeleteFolderAsync(item.Dto.Id);
            AdminStatus = Loc.T("Ordner {0} gelöscht.", item.Name);
        }
        catch (VaultApiException ex)
        {
            AdminStatus = ex.Message;
        }
        await AdminRefreshAsync();
        await LoadFoldersAsync();
    }

    [RelayCommand]
    private async Task AdminChangeOwnerAsync(AdminFolderItem? item)
    {
        item ??= SelectedAdminFolder;
        if (AdminServer is not { } entry || item == null)
            return;
        var result = FormDialog.Show(Owner, Loc.T("Besitzer ändern"), item.Name,
            new[] { FormField.Choice("owner", Loc.T("Neuer Besitzer"), UserChoices(), AdminUsers.FirstOrDefault(u => u.Dto.Id == item.Dto.OwnerId)?.Dto.Username ?? "") },
            Loc.T("Speichern"),
            async v => await entry.Client!.AdminSetOwnerAsync(item.Dto.Id, v["owner"]));
        if (result != null)
            await AdminRefreshAsync();
    }

    [RelayCommand]
    private async Task AdminCreateUserAsync()
    {
        if (AdminServer is not { } entry)
            return;
        var result = FormDialog.Show(Owner, Loc.T("Benutzer anlegen"),
            Loc.T("Der Benutzer muss das Startpasswort bei der ersten Anmeldung ändern."),
            new[]
            {
                FormField.Text("user", Loc.T("Benutzername"), "", Loc.T("3–32 Zeichen: Buchstaben, Ziffern, . _ -")),
                FormField.Text("name", Loc.T("Anzeigename (optional)")),
                FormField.Text("pw", Loc.T("Startpasswort"), FormDialog.RandomPassword()),
                FormField.Check("admin", Loc.T("Admin-Rechte"))
            },
            Loc.T("Anlegen"),
            async v =>
            {
                FormDialog.CheckNewPassword(v["pw"], v["pw"]);
                await entry.Client!.AdminCreateUserAsync(v["user"], v["pw"], v["name"].Length > 0 ? v["name"] : null, v["admin"] == "true");
            });
        if (result == null)
            return;
        System.Windows.Clipboard.SetText(result["pw"]);
        AdminStatus = Loc.T("Benutzer @{0} angelegt. Startpasswort (in die Zwischenablage kopiert): {1}", result["user"], result["pw"]);
        await AdminRefreshAsync();
    }

    [RelayCommand]
    private async Task AdminResetPasswordAsync(AdminUserItem? item)
    {
        item ??= SelectedAdminUser;
        if (AdminServer is not { } entry || item == null)
            return;
        var result = FormDialog.Show(Owner, Loc.T("Passwort zurücksetzen"),
            Loc.T("{0} wird überall abgemeldet und muss bei der nächsten Anmeldung ein neues Passwort wählen. Ordnerpasswörter sind davon nicht betroffen.", item.Username),
            new[] { FormField.Text("pw", Loc.T("Startpasswort"), FormDialog.RandomPassword()) },
            Loc.T("Zurücksetzen"),
            async v =>
            {
                FormDialog.CheckNewPassword(v["pw"], v["pw"]);
                await entry.Client!.AdminResetPasswordAsync(item.Dto.Id, v["pw"]);
            });
        if (result == null)
            return;
        System.Windows.Clipboard.SetText(result["pw"]);
        AdminStatus = Loc.T("Neues Startpasswort für {0} (in die Zwischenablage kopiert): {1}", item.Username, result["pw"]);
        await AdminRefreshAsync();
    }

    [RelayCommand]
    private async Task AdminToggleAdminAsync(AdminUserItem? item)
    {
        item ??= SelectedAdminUser;
        if (AdminServer is not { } entry || item == null)
            return;
        if (item.Dto.Id == entry.User?.Id)
        {
            AdminStatus = Loc.T("Du kannst dir die Admin-Rechte nicht selbst entziehen.");
            return;
        }
        try
        {
            await entry.Client!.AdminSetAdminAsync(item.Dto.Id, !item.IsAdmin);
        }
        catch (VaultApiException ex)
        {
            AdminStatus = ex.Message;
        }
        await AdminRefreshAsync();
    }

    [RelayCommand]
    private async Task AdminDeleteUserAsync(AdminUserItem? item)
    {
        item ??= SelectedAdminUser;
        if (AdminServer is not { } entry || item == null)
            return;
        if (item.Dto.Id == entry.User?.Id)
        {
            AdminStatus = Loc.T("Du kannst dein eigenes Profil hier nicht löschen.");
            return;
        }
        if (!FormDialog.Confirm(Owner, Loc.T("Benutzer löschen"),
                Loc.T("Profil {0} löschen? Seine Ordner bleiben erhalten und können neu zugewiesen werden.", item.Username), Loc.T("Löschen")))
            return;
        try
        {
            await entry.Client!.AdminDeleteUserAsync(item.Dto.Id);
        }
        catch (VaultApiException ex)
        {
            AdminStatus = ex.Message;
        }
        await AdminRefreshAsync();
    }

    [RelayCommand]
    private async Task AdminSaveSettingsAsync()
    {
        if (AdminServer is not { } entry)
            return;
        try
        {
            var s = await entry.Client!.AdminSaveSettingsAsync(new ServerSettingsDto
            {
                Name = AdminServerName.Trim(), AllowRegistration = AdminAllowRegistration, AutoUpdate = AdminAutoUpdate
            });
            entry.Name = s.Name;
            if (entry.Saved != null)
            {
                entry.Saved.Name = s.Name;
                SaveServers();
            }
            AdminStatus = Loc.T("Einstellungen gespeichert.");
        }
        catch (VaultApiException ex)
        {
            AdminStatus = ex.Message;
        }
    }

    #endregion

    #region Server-Updates (Admin)

    private bool _loadingAutoUpdate;

    [ObservableProperty] private bool _adminAutoUpdate = true;
    [ObservableProperty] private string _adminUpdateText = string.Empty;
    [ObservableProperty] private bool _adminUpdateAvailable;
    [ObservableProperty] private bool _adminUpdateSupported;
    [ObservableProperty] private bool _adminUpdateBusy;

    partial void OnAdminAutoUpdateChanged(bool value)
    {
        if (!_loadingAutoUpdate && AdminServer != null)
            _ = AdminSaveSettingsAsync();
    }

    private void ShowUpdate(ServerUpdateDto? u)
    {
        if (u == null)
        {
            AdminUpdateSupported = false;
            AdminUpdateAvailable = false;
            AdminUpdateText = Loc.T("Dieser Server unterstützt noch keine automatischen Updates – bitte einmal manuell aktualisieren.");
            return;
        }
        AdminUpdateSupported = u.Supported;
        AdminUpdateAvailable = u.UpdateAvailable && u.Supported && u.State != "restarting";
        var versions = Loc.T("Installiert: {0} · Neueste: {1}", u.CurrentVersion, u.LatestVersion ?? "–");
        var state = u.State switch
        {
            "checking" => Loc.T("Suche nach Updates..."),
            "downloading" => Loc.T("Update wird heruntergeladen..."),
            "restarting" => Loc.T("Update installiert – der Server startet neu..."),
            "installed" => Loc.T("Update installiert – bitte nfm-server neu starten."),
            "noPermission" => Loc.T("Keine Schreibrechte – neue Version einmal manuell installieren (Installationsskript); danach laufen Updates automatisch."),
            "unsupported" => Loc.T("Dieser Server-Build kann sich nicht selbst aktualisieren."),
            "error" => Loc.T("Fehler: {0}", u.Message ?? ""),
            _ when u.UpdateAvailable => Loc.T("Version {0} ist verfügbar.", u.LatestVersion ?? ""),
            _ when u.LastCheck != null => Loc.T("Der Server ist aktuell."),
            _ => ""
        };
        AdminUpdateText = state.Length == 0 ? versions : versions + " · " + state;
    }

    [RelayCommand]
    private async Task AdminCheckUpdateAsync()
    {
        if (AdminServer is not { } entry)
            return;
        AdminUpdateBusy = true;
        AdminUpdateText = Loc.T("Suche nach Updates...");
        try
        {
            ShowUpdate(await entry.Client!.AdminCheckUpdateAsync());
        }
        catch (VaultApiException ex)
        {
            AdminUpdateText = ex.Message;
        }
        finally
        {
            AdminUpdateBusy = false;
        }
    }

    [RelayCommand]
    private async Task AdminInstallUpdateAsync()
    {
        if (AdminServer is not { } entry)
            return;
        AdminUpdateBusy = true;
        AdminUpdateText = Loc.T("Update wird heruntergeladen...");
        try
        {
            var u = await entry.Client!.AdminInstallUpdateAsync();
            ShowUpdate(u);
            if (u.State == "restarting" && u.LatestVersion != null)
            {
                // Warten, bis der Server mit der neuen Version wieder antwortet
                for (var i = 0; i < 60; i++)
                {
                    await Task.Delay(2000);
                    try
                    {
                        var info = await entry.Client.InfoAsync();
                        if (info.Version == u.LatestVersion)
                        {
                            AdminStatus = Loc.T("Server auf {0} aktualisiert.", info.Version);
                            await AdminRefreshAsync();
                            return;
                        }
                    }
                    catch
                    {
                        // startet noch
                    }
                }
                AdminUpdateText = Loc.T("Der Server ist noch nicht wieder erreichbar – bitte später aktualisieren.");
            }
        }
        catch (VaultApiException ex)
        {
            AdminUpdateText = ex.Message;
        }
        finally
        {
            AdminUpdateBusy = false;
        }
    }

    #endregion

    public void Dispose()
    {
        _searchTimer?.Stop();
        foreach (var w in _openWindows.Values.ToList())
            w.Close();
        foreach (var s in Servers)
            s.Client?.Dispose();
    }
}
