using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NetworkFolderManager.Models;
using NetworkFolderManager.Mac;
using NetworkFolderManager.Services;
using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace NetworkFolderManager.ViewModels;

/// <summary>
/// Hauptmodell der macOS-App: Ordner (SMB einbinden, hochladen), eigener überwachter Ordner und Einstellungen.
/// Der Chat liegt im gemeinsam genutzten <see cref="ChatViewModel"/>.
/// </summary>
public partial class MainViewModel : ObservableObject, IDisposable
{
    private const string ReleasesPage = "https://github.com/Organisation2-0/NetworkFolderManager/releases";
    private const string LatestReleaseApi = "https://api.github.com/repos/Organisation2-0/NetworkFolderManager/releases/latest";

    private readonly AppSettings _settings;
    private readonly FileTransferService _transfer = new();
    private readonly FolderMonitorService _monitor = new();
    private readonly System.Threading.Timer _mountTimer;

    public MainViewModel()
    {
        _settings = SettingsService.Load();
        _displayName = _settings.DisplayName;
        _chatNotifications = _settings.ChatNotifications;
        _ownFolderPath = _settings.OwnFolderPath;
        _monitorOnStartup = _settings.MonitorOnStartup;
        _autostart = File.Exists(LaunchAgentFile);
        _languageStatus = Loc.T("Aktuelle Sprache: {0}. Eine Änderung wirkt nach einem Neustart des Programms.", Loc.IsEnglish ? "English" : "Deutsch");

        Chat = new ChatViewModel(_settings, () => null);
        Chat.PeerShareDiscovered += (_, peer) => AddPeerShare(peer);

        foreach (var url in _settings.ManualFolders)
        {
            if (TryParseSmb(url, out var host, out var share))
                AddFolder(host, share, null, Loc.T("Manuell hinzugefügt"), manual: true);
        }

        _monitor.NewFileDetected += OnNewFile;
        _monitor.MonitorError += (_, e) => Platform_Post(() =>
            MonitorStatus = Loc.T("Überwachung unterbrochen: {0}. Neuer Versuch in 30 s...", e.GetException().Message));
        _mountTimer = new System.Threading.Timer(_ => Platform_Post(RefreshMountStates), null,
            TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10));
    }

    private static void Platform_Post(Action a) => Services.Platform.Current.Post(a);

    public ChatViewModel Chat { get; }

    public string VersionText => $"Version {CurrentVersion} · noobnet.eu";

    private static Version CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version is { } v ? new Version(v.Major, v.Minor, Math.Max(0, v.Build)) : new Version(1, 0, 0);

    public async Task InitializeAsync()
    {
        Chat.Start();
        RefreshMountStates();
        if (MonitorOnStartup && !string.IsNullOrWhiteSpace(OwnFolderPath))
            StartMonitoring();
        await CheckForUpdatesAsync();
    }

    #region Ordner

    public ObservableCollection<NetworkFolder> NetworkFolders { get; } = new();

    [ObservableProperty]
    private string _statusText = Loc.T("Bereit");

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectFolderCommand))]
    [NotifyCanExecuteChangedFor(nameof(DisconnectFolderCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenFolderCommand))]
    [NotifyCanExecuteChangedFor(nameof(RemoveFolderCommand))]
    [NotifyCanExecuteChangedFor(nameof(UploadFilesCommand))]
    private NetworkFolder? _selectedFolder;

    [ObservableProperty]
    private string _manualPath = string.Empty;

    [ObservableProperty]
    private string _uploadStatus = string.Empty;

    [ObservableProperty]
    private double _uploadProgress;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UploadFilesCommand))]
    private bool _isBusy;

    /// <summary>Akzeptiert \\host\freigabe, //host/freigabe und smb://host/freigabe.</summary>
    public static bool TryParseSmb(string input, out string host, out string share)
    {
        host = share = string.Empty;
        var s = input.Trim().Replace('\\', '/');
        if (s.StartsWith("smb://", StringComparison.OrdinalIgnoreCase))
            s = s[6..];
        s = s.TrimStart('/').TrimEnd('/');

        var at = s.IndexOf('@');
        if (at >= 0 && at < s.IndexOf('/'))
            s = s[(at + 1)..];

        var parts = s.Split('/', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            return false;
        host = parts[0];
        share = parts[1].Split('/')[0];
        return host.Length > 0 && share.Length > 0;
    }

    private NetworkFolder AddFolder(string host, string share, string? user, string description, bool manual)
    {
        var existing = NetworkFolders.FirstOrDefault(f =>
            f.ComputerName.Equals(host, StringComparison.OrdinalIgnoreCase) &&
            f.ShareName.Equals(share, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            if (!string.IsNullOrEmpty(user))
                existing.SuggestedUsername = user;
            return existing;
        }

        var folder = new NetworkFolder
        {
            ComputerName = host,
            ShareName = share,
            Path = $"smb://{host}/{share}",
            DisplayName = $"{host}/{share}",
            Description = description,
            SuggestedUsername = user,
            IsManual = manual
        };
        NetworkFolders.Add(folder);
        return folder;
    }

    private void AddPeerShare(Peer peer)
    {
        if (peer.SharePath == null || !TryParseSmb(peer.SharePath, out var host, out var share))
            return;

        // Über die IP-Adresse verbinden (NetBIOS-Namen von Windows-PCs lösen auf dem Mac oft nicht auf)
        var address = string.IsNullOrEmpty(peer.Address) ? host : peer.Address;
        var folder = AddFolder(address, share, peer.ShareUser, Loc.T("Ordner von {0}", peer.DisplayName), manual: false);
        folder.DisplayName = $"{peer.DisplayName} · {share}";
        RefreshMountStates();
    }

    [RelayCommand]
    private void AddManualFolder()
    {
        if (!TryParseSmb(ManualPath, out var host, out var share))
        {
            StatusText = Loc.T("Ungültiger Pfad. Format: smb://Server/Freigabe");
            return;
        }

        var folder = AddFolder(host, share, null, Loc.T("Manuell hinzugefügt"), manual: true);
        folder.IsManual = true;
        SelectedFolder = folder;
        if (!_settings.ManualFolders.Contains(folder.Path, StringComparer.OrdinalIgnoreCase))
        {
            _settings.ManualFolders.Add(folder.Path);
            SettingsService.Save(_settings);
        }
        ManualPath = string.Empty;
        StatusText = Loc.T("{0} hinzugefügt", folder.DisplayName) + Loc.T(". Jetzt verbinden.");
    }

    private bool HasFolder() => SelectedFolder != null;

    [RelayCommand(CanExecute = nameof(HasFolder))]
    private void RemoveFolder()
    {
        if (SelectedFolder is not { } folder)
            return;
        NetworkFolders.Remove(folder);
        _settings.ManualFolders.RemoveAll(p => p.Equals(folder.Path, StringComparison.OrdinalIgnoreCase));
        SettingsService.Save(_settings);
        StatusText = Loc.T("{0} entfernt.", folder.DisplayName);
    }

    /// <summary>Wo macOS die Freigabe einhängt (/Volumes/Name).</summary>
    private static string MountPoint(NetworkFolder folder) => $"/Volumes/{folder.ShareName}";

    private void RefreshMountStates()
    {
        foreach (var folder in NetworkFolders)
            folder.IsConnected = Directory.Exists(MountPoint(folder));
        OpenFolderCommand.NotifyCanExecuteChanged();
        DisconnectFolderCommand.NotifyCanExecuteChanged();
        UploadFilesCommand.NotifyCanExecuteChanged();
    }

    private static string BuildSmbUrl(NetworkFolder folder)
    {
        var user = folder.SuggestedUsername;
        var auth = string.Empty;
        if (!string.IsNullOrWhiteSpace(user))
        {
            // COMPUTER\Konto → COMPUTER;Konto
            var parts = user.Split('\\', 2);
            auth = (parts.Length == 2
                ? Uri.EscapeDataString(parts[0]) + ";" + Uri.EscapeDataString(parts[1])
                : Uri.EscapeDataString(user)) + "@";
        }
        return $"smb://{auth}{folder.ComputerName}/{Uri.EscapeDataString(folder.ShareName)}";
    }

    /// <summary>Bindet die Freigabe über macOS ein (macOS fragt selbst nach Benutzername/Passwort und kann es im Schlüsselbund merken).</summary>
    [RelayCommand(CanExecute = nameof(HasFolder))]
    private async Task ConnectFolderAsync()
    {
        if (SelectedFolder is not { } folder)
            return;

        if (Directory.Exists(MountPoint(folder)))
        {
            folder.IsConnected = true;
            StatusText = Loc.T("Verbunden mit {0}.", folder.DisplayName);
            return;
        }

        StatusText = Loc.T("Verbinde mit {0}...", folder.DisplayName);
        var (code, output) = await MacPlatform.RunAsync("osascript", TimeSpan.FromMinutes(3),
            "-e", $"mount volume \"{BuildSmbUrl(folder)}\"");
        RefreshMountStates();
        StatusText = code == 0
            ? Loc.T("Verbunden mit {0}.", folder.DisplayName)
            : Loc.T("Verbindung zu {0} fehlgeschlagen:\n\n{1}", folder.DisplayName, output);
    }

    [RelayCommand(CanExecute = nameof(HasFolder))]
    private async Task DisconnectFolderAsync()
    {
        if (SelectedFolder is not { } folder)
            return;

        var (code, output) = await MacPlatform.RunAsync("diskutil", TimeSpan.FromSeconds(30), "unmount", MountPoint(folder));
        RefreshMountStates();
        StatusText = code == 0
            ? Loc.T("Verbindung zu {0} getrennt.", folder.DisplayName)
            : Loc.T("Fehler beim Trennen: {0}", output);
    }

    [RelayCommand(CanExecute = nameof(HasFolder))]
    private void OpenFolder()
    {
        if (SelectedFolder is not { } folder)
            return;
        if (Directory.Exists(MountPoint(folder)))
            Services.Platform.Current.OpenFolder(MountPoint(folder));
        else
            _ = ConnectFolderAsync();
    }

    private bool CanUpload() => SelectedFolder is { IsConnected: true } && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanUpload))]
    private async Task UploadFilesAsync()
    {
        var files = await Services.Platform.Current.PickFilesAsync(Loc.T("Dateien zum Hochladen auswählen"));
        if (files.Count > 0)
            await UploadAsync(files);
    }

    /// <summary>Lädt Dateien in den ausgewählten, eingehängten Ordner hoch (Dialog oder Drag &amp; Drop).</summary>
    public async Task UploadAsync(IEnumerable<string> paths)
    {
        if (IsBusy)
        {
            StatusText = Loc.T("Es läuft bereits ein Upload.");
            return;
        }
        if (SelectedFolder is not { } folder)
        {
            StatusText = Loc.T("Bitte wählen Sie links einen Zielordner aus.");
            return;
        }
        if (!Directory.Exists(MountPoint(folder)))
        {
            StatusText = Loc.T("{0} ist nicht erreichbar. Bitte zuerst verbinden.", folder.DisplayName);
            return;
        }

        var files = paths.Where(File.Exists).ToList();
        if (files.Count == 0)
        {
            StatusText = Loc.T("Keine Dateien zum Hochladen (Ordner werden nicht unterstützt).");
            return;
        }

        IsBusy = true;
        UploadProgress = 0;
        UploadStatus = files.Count == 1
            ? Loc.T("Lade {0} hoch...", Path.GetFileName(files[0]))
            : Loc.T("Lade {0} Dateien hoch...", files.Count);
        try
        {
            void OnProgress(object? s, TransferProgressEventArgs e) => Platform_Post(() => UploadProgress = e.Percentage);
            _transfer.ProgressChanged += OnProgress;
            List<TransferResult> results;
            try
            {
                results = await _transfer.UploadFilesAsync(files, MountPoint(folder));
            }
            finally
            {
                _transfer.ProgressChanged -= OnProgress;
            }

            var ok = results.Count(r => r.Success);
            var failed = results.Where(r => !r.Success).ToList();
            UploadStatus = failed.Count == 0
                ? Loc.T("{0} Datei(en) erfolgreich nach {1} hochgeladen.", ok, folder.DisplayName)
                : Loc.T("{0}/{1} hochgeladen. Fehler: ", ok, results.Count) +
                  string.Join("; ", failed.Take(3).Select(r => $"{r.FileName}: {r.ErrorMessage}"));
            StatusText = Loc.T("Upload abgeschlossen.");
        }
        catch (Exception ex)
        {
            UploadStatus = Loc.T("Upload fehlgeschlagen: {0}", ex.Message);
        }
        finally
        {
            IsBusy = false;
            UploadProgress = 0;
        }
    }

    #endregion

    #region Eigener Ordner (Überwachung)

    public ObservableCollection<NewFileEventArgs> ReceivedFiles { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartMonitoringCommand))]
    private string _ownFolderPath;

    [ObservableProperty]
    private string _monitorStatus = Loc.T("Überwachung inaktiv.");

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartMonitoringCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopMonitoringCommand))]
    private bool _isMonitoring;

    [ObservableProperty]
    private bool _monitorOnStartup;

    partial void OnMonitorOnStartupChanged(bool value)
    {
        _settings.MonitorOnStartup = value;
        SettingsService.Save(_settings);
    }

    partial void OnOwnFolderPathChanged(string value)
    {
        _settings.OwnFolderPath = value;
        SettingsService.Save(_settings);
    }

    [RelayCommand]
    private async Task BrowseOwnFolderAsync()
    {
        var folder = await Services.Platform.Current.PickFolderAsync(
            Loc.T("Eigenen Ordner zur Überwachung auswählen"),
            Directory.Exists(OwnFolderPath) ? OwnFolderPath : null);
        if (!string.IsNullOrEmpty(folder))
            OwnFolderPath = folder;
    }

    private bool CanStart() => !IsMonitoring && !string.IsNullOrWhiteSpace(OwnFolderPath);

    [RelayCommand(CanExecute = nameof(CanStart))]
    private void StartMonitoring()
    {
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(OwnFolderPath.Trim());
        }
        catch (Exception ex)
        {
            StatusText = Loc.T("Ungültiger Pfad: {0}", ex.Message);
            return;
        }

        if (!Directory.Exists(fullPath))
        {
            StatusText = Loc.T("Ordner nicht erreichbar: {0}", fullPath);
            MonitorStatus = Loc.T("Ordner nicht erreichbar – ggf. zuerst verbinden.");
            return;
        }

        try
        {
            _monitor.StartMonitoring(fullPath);
        }
        catch (Exception ex)
        {
            StatusText = Loc.T("Überwachung konnte nicht gestartet werden: {0}", ex.Message);
            return;
        }

        IsMonitoring = true;
        MonitorStatus = Loc.T("Überwachung von {0} aktiv.", fullPath);
        StatusText = Loc.T("Überwachung aktiv.");
    }

    [RelayCommand(CanExecute = nameof(IsMonitoring))]
    private void StopMonitoring()
    {
        _monitor.StopMonitoring();
        IsMonitoring = false;
        MonitorStatus = Loc.T("Überwachung gestoppt.");
        StatusText = Loc.T("Überwachung gestoppt.");
    }

    private void OnNewFile(object? sender, NewFileEventArgs e) => Platform_Post(() =>
    {
        ReceivedFiles.Insert(0, e);
        while (ReceivedFiles.Count > 50)
            ReceivedFiles.RemoveAt(ReceivedFiles.Count - 1);
        Services.Platform.Current.Notify(Loc.T("Neue Datei erhalten"),
            Loc.T("In Ihrem Ordner wurde eine neue Datei abgelegt:\n{0}", e.FileName));
        StatusText = Loc.T("Neue Datei: {0}", e.FileName);
    });

    [RelayCommand]
    private void ShowReceivedFile(NewFileEventArgs? file)
    {
        if (file != null && File.Exists(file.FullPath))
            Services.Platform.Current.RevealInFileManager(file.FullPath);
        else if (file != null)
            StatusText = Loc.T("{0} existiert nicht mehr.", file.FileName);
    }

    #endregion

    #region Einstellungen

    [ObservableProperty]
    private string _displayName;

    [ObservableProperty]
    private bool _chatNotifications;

    [ObservableProperty]
    private bool _autostart;

    [ObservableProperty]
    private string _languageStatus;

    partial void OnDisplayNameChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;
        _settings.DisplayName = value.Trim();
        SettingsService.Save(_settings);
        Chat?.AnnounceNow();
    }

    partial void OnChatNotificationsChanged(bool value)
    {
        _settings.ChatNotifications = value;
        SettingsService.Save(_settings);
    }

    private static string LaunchAgentFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Library", "LaunchAgents", "eu.noobnet.networkfoldermanager.plist");

    partial void OnAutostartChanged(bool value)
    {
        try
        {
            if (value)
            {
                var exe = Environment.ProcessPath ?? throw new InvalidOperationException("ProcessPath");
                Directory.CreateDirectory(Path.GetDirectoryName(LaunchAgentFile)!);
                File.WriteAllText(LaunchAgentFile,
                    "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
                    "<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n" +
                    "<plist version=\"1.0\"><dict>\n" +
                    "<key>Label</key><string>eu.noobnet.networkfoldermanager</string>\n" +
                    $"<key>ProgramArguments</key><array><string>{System.Security.SecurityElement.Escape(exe)}</string><string>--minimized</string></array>\n" +
                    "<key>RunAtLoad</key><true/>\n" +
                    "</dict></plist>\n");
            }
            else if (File.Exists(LaunchAgentFile))
            {
                File.Delete(LaunchAgentFile);
            }
        }
        catch (Exception ex)
        {
            StatusText = Loc.T("Autostart konnte nicht geändert werden: {0}", ex.Message);
        }
    }

    [RelayCommand]
    private void SetLanguageGerman() => ChangeLanguage("de");

    [RelayCommand]
    private void SetLanguageEnglish() => ChangeLanguage("en");

    private void ChangeLanguage(string language)
    {
        Loc.SavedLanguage = language;
        LanguageStatus = language == Loc.Language
            ? Loc.T("Aktuelle Sprache: {0}. Eine Änderung wirkt nach einem Neustart des Programms.", Loc.IsEnglish ? "English" : "Deutsch")
            : Loc.T("Sprache gespeichert. Bitte das Programm neu starten (Infobereich → Beenden, dann erneut öffnen).");
    }

    #endregion

    #region Updates

    [ObservableProperty]
    private string _updateStatus = string.Empty;

    [ObservableProperty]
    private bool _hasUpdate;

    [RelayCommand]
    private async Task CheckForUpdatesAsync()
    {
        UpdateStatus = Loc.T("Suche nach Updates...");
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("NetworkFolderManager");
            using var doc = JsonDocument.Parse(await http.GetStringAsync(LatestReleaseApi));
            var tag = doc.RootElement.GetProperty("tag_name").GetString() ?? string.Empty;
            if (Version.TryParse(tag.TrimStart('v', 'V'), out var latest) && latest > CurrentVersion)
            {
                HasUpdate = true;
                UpdateStatus = Loc.T("Version {0} ist verfügbar.", latest);
            }
            else
            {
                HasUpdate = false;
                UpdateStatus = Loc.T("Sie verwenden die neueste Version ({0}).", CurrentVersion);
            }
        }
        catch (Exception ex)
        {
            LogService.Info($"Update-Prüfung fehlgeschlagen: {ex.GetBaseException().Message}");
            UpdateStatus = Loc.T("Update-Server nicht erreichbar. Später erneut versuchen.");
        }
    }

    [RelayCommand]
    private void OpenReleasesPage() => MacPlatform.Run("open", ReleasesPage);

    #endregion

    public void Dispose()
    {
        _mountTimer.Dispose();
        _monitor.Dispose();
        Chat.Dispose();
    }
}
