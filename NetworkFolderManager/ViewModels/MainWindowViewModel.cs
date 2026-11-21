using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NetworkFolderManager.Models;
using NetworkFolderManager.Services;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace NetworkFolderManager.ViewModels;

/// <summary>
/// View-Model für das Hauptfenster.
/// Verwaltet die Liste der Netzwerkordner, Verbindungen, Datei-Uploads und die Ordnerüberwachung.
/// </summary>
public partial class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly FileTransferService _fileTransferService = new();
    private readonly FolderMonitorService _folderMonitor = new();
    private readonly AppSettings _settings;
    private CancellationTokenSource? _uploadCts;
    private long _uploadTotalBytes;
    private readonly Dictionary<string, long> _uploadProgressPerFile = new();

    private MachineConfig? _machineConfig;

    public MainWindowViewModel()
    {
        _settings = SettingsService.Load();
        _machineConfig = ShareSetupService.LoadMachineConfig();

        // Vom Installer (neu) angelegten Netzwerkordner übernehmen und automatisch überwachen
        if (_machineConfig != null && _settings.AppliedShareCreatedAt != _machineConfig.CreatedAt)
        {
            _settings.OwnFolderPath = _machineConfig.SharePath;
            _settings.MonitorOnStartup = true;
            _settings.AppliedShareCreatedAt = _machineConfig.CreatedAt;
            SettingsService.Save(_settings);
        }

        _ownFolderPath = _settings.OwnFolderPath;
        _minimizeToTray = _settings.MinimizeToTray;
        _monitorOnStartup = _settings.MonitorOnStartup;
        _displayName = _settings.DisplayName;
        _chatNotifications = _settings.ChatNotifications;
        _autostart = AutostartService.IsEnabled;
        _autoUpdate = UpdateService.AutoUpdateEnabled;
        UpdateShareInfo();

        Chat = new ChatViewModel(_settings, () => _machineConfig);
        Chat.PeerShareDiscovered += OnPeerShareDiscovered;

        foreach (var path in _settings.ManualFolders)
        {
            var folder = NetworkDiscoveryService.ParseUncPath(path);
            if (folder != null)
            {
                folder.IsManual = true;
                NetworkFolders.Add(folder);
            }
        }

        _fileTransferService.ProgressChanged += OnUploadProgress;
        _folderMonitor.NewFileDetected += OnNewFileDetected;
        _folderMonitor.MonitorError += OnMonitorError;
    }

    #region Properties

    public ObservableCollection<NetworkFolder> NetworkFolders { get; } = new();

    public ChatViewModel Chat { get; }

    /// <summary>Zuletzt empfangene Dateien im eigenen Ordner.</summary>
    public ObservableCollection<NewFileEventArgs> ReceivedFiles { get; } = new();

    [ObservableProperty]
    private string _statusText = Loc.T("Bereit");

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanNetworkCommand))]
    private bool _isScanning;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectToFolderCommand))]
    [NotifyCanExecuteChangedFor(nameof(DisconnectFromFolderCommand))]
    private bool _isConnecting;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectToFolderCommand))]
    [NotifyCanExecuteChangedFor(nameof(DisconnectFromFolderCommand))]
    [NotifyCanExecuteChangedFor(nameof(UploadFileCommand))]
    [NotifyCanExecuteChangedFor(nameof(UploadMultipleFilesCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenInExplorerCommand))]
    [NotifyCanExecuteChangedFor(nameof(RemoveManualFolderCommand))]
    [NotifyCanExecuteChangedFor(nameof(UseAsOwnFolderCommand))]
    [NotifyCanExecuteChangedFor(nameof(ForgetPasswordCommand))]
    private NetworkFolder? _selectedFolder;

    [ObservableProperty]
    private string _manualPath = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartMonitoringCommand))]
    private string _ownFolderPath;

    [ObservableProperty]
    private string _uploadStatus = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UploadFileCommand))]
    [NotifyCanExecuteChangedFor(nameof(UploadMultipleFilesCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelUploadCommand))]
    private bool _isUploading;

    [ObservableProperty]
    private double _uploadProgress;

    [ObservableProperty]
    private string _monitorStatus = Loc.T("Überwachung inaktiv.");

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartMonitoringCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopMonitoringCommand))]
    private bool _isMonitoring;

    [ObservableProperty]
    private bool _minimizeToTray;

    [ObservableProperty]
    private bool _monitorOnStartup;

    [ObservableProperty]
    private string _displayName;

    [ObservableProperty]
    private bool _chatNotifications;

    [ObservableProperty]
    private bool _autostart;

    [ObservableProperty]
    private string _ownShareInfo = string.Empty;

    [ObservableProperty]
    private string _newSharePath = @"C:\Netzwerkordner";

    [ObservableProperty]
    private string _newShareName = "Netzwerkordner";

    public bool HasOwnShare => _machineConfig != null;

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

    #region Updates

    [ObservableProperty]
    private bool _autoUpdate;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckForUpdatesCommand))]
    [NotifyCanExecuteChangedFor(nameof(InstallUpdateCommand))]
    private bool _isUpdating;

    [ObservableProperty]
    private string _updateStatus = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallUpdateCommand))]
    [NotifyPropertyChangedFor(nameof(HasUpdate))]
    private UpdateInfo? _availableUpdate;

    public bool HasUpdate => AvailableUpdate != null;

    public string VersionText => Loc.T("Version {0} · noobnet.eu", UpdateService.CurrentVersion);

    private System.Windows.Threading.DispatcherTimer? _updateTimer;

    partial void OnAutoUpdateChanged(bool value)
    {
        UpdateService.AutoUpdateEnabled = value;
        UpdateStatus = value ? Loc.T("Automatische Updates sind eingeschaltet.") : Loc.T("Automatische Updates sind ausgeschaltet.");
        if (value)
            _ = CheckForUpdatesCoreAsync(automatic: true);
    }

    /// <summary>Startet die regelmäßige Update-Prüfung (kurz nach dem Start, dann alle 6 Stunden).</summary>
    private void StartUpdateTimer()
    {
        _updateTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(45) };
        _updateTimer.Tick += async (_, _) =>
        {
            _updateTimer.Interval = TimeSpan.FromHours(6);
            if (AutoUpdate)
                await CheckForUpdatesCoreAsync(automatic: true);
        };
        _updateTimer.Start();
    }

    private bool CanCheckUpdates() => !IsUpdating;

    [RelayCommand(CanExecute = nameof(CanCheckUpdates))]
    private Task CheckForUpdatesAsync() => CheckForUpdatesCoreAsync(automatic: false);

    private async Task CheckForUpdatesCoreAsync(bool automatic)
    {
        if (IsUpdating)
            return;

        IsUpdating = true;
        UpdateStatus = Loc.T("Suche nach Updates...");
        try
        {
            AvailableUpdate = await UpdateService.CheckAsync();
            if (AvailableUpdate == null)
            {
                UpdateStatus = Loc.T("Sie verwenden die neueste Version ({0}).", UpdateService.CurrentVersion);
                return;
            }

            UpdateStatus = Loc.T("Version {0} ist verfügbar.", AvailableUpdate.Version);
            if (automatic)
            {
                IsUpdating = false;
                await InstallUpdateAsync();
            }
        }
        catch (Exception ex)
        {
            LogService.Info($"Update-Prüfung fehlgeschlagen: {ex.GetBaseException().Message}");
            UpdateStatus = Loc.T("Update-Server nicht erreichbar. Später erneut versuchen.");
        }
        finally
        {
            IsUpdating = false;
        }
    }

    private bool CanInstallUpdate() => AvailableUpdate != null && !IsUpdating;

    /// <summary>Update herunterladen, prüfen und installieren (Programm wird danach neu gestartet).</summary>
    [RelayCommand(CanExecute = nameof(CanInstallUpdate))]
    private async Task InstallUpdateAsync()
    {
        if (AvailableUpdate is not { } update)
            return;

        IsUpdating = true;
        try
        {
            var progress = new Progress<double>(p => UpdateStatus = Loc.T("Update {0} wird heruntergeladen... {1} %", update.Version, p.ToString("0")));
            var setup = await UpdateService.DownloadAsync(update, progress);

            NotificationService.ShowToastNotification(Loc.T("Update wird installiert"),
                Loc.T("NetworkFolderManager {0} wird installiert und danach neu gestartet.", update.Version));
            UpdateStatus = Loc.T("Update {0} wird installiert...", update.Version);

            if (!UpdateService.StartInstaller(setup))
            {
                UpdateStatus = Loc.T("Update abgebrochen (Administratorrechte wurden nicht erteilt).");
                return;
            }

            // Programm beenden, damit der Installer die Dateien ersetzen kann (er startet es danach neu)
            await Task.Delay(1500);
            NotificationService.MarkExiting();
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            LogService.Error("Update/Install", ex);
            UpdateStatus = Loc.T("Update fehlgeschlagen: {0}", ex.GetBaseException().Message);
        }
        finally
        {
            IsUpdating = false;
        }
    }

    [RelayCommand]
    private void OpenReleasesPage() =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(UpdateService.ReleasesPage) { UseShellExecute = true });

    #endregion

    #region Sprache

    [ObservableProperty]
    private string _languageStatus = Loc.T("Aktuelle Sprache: {0}. Eine Änderung wirkt nach einem Neustart des Programms.", Loc.IsEnglish ? "English" : "Deutsch");

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

    partial void OnAutostartChanged(bool value)
    {
        try
        {
            AutostartService.SetEnabled(value);
        }
        catch (Exception ex)
        {
            StatusText = Loc.T("Autostart konnte nicht geändert werden: {0}", ex.Message);
        }
    }

    partial void OnMinimizeToTrayChanged(bool value)
    {
        _settings.MinimizeToTray = value;
        SettingsService.Save(_settings);
    }

    partial void OnMonitorOnStartupChanged(bool value)
    {
        _settings.MonitorOnStartup = value;
        SettingsService.Save(_settings);
    }

    #endregion

    /// <summary>
    /// Wird vom Fenster nach dem Laden aufgerufen.
    /// </summary>
    public async Task InitializeAsync()
    {
        Chat.Start();
        StartUpdateTimer();
        AddOwnShareToList();

        await RefreshConnectionStatesAsync();

        if (MonitorOnStartup && !string.IsNullOrWhiteSpace(OwnFolderPath))
            await StartMonitoringAsync();
    }

    #region Netzwerk

    private bool CanScan() => !IsScanning;

    /// <summary>
    /// Scant das lokale Netzwerk nach freigegebenen Ordnern.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanScan))]
    private async Task ScanNetworkAsync()
    {
        IsScanning = true;
        StatusText = Loc.T("Netzwerk wird durchsucht...");

        try
        {
            var progress = new Progress<string>(msg => StatusText = msg);
            var found = await NetworkDiscoveryService.ScanNetworkAsync(progress, CancellationToken.None);

            // Gefundene Ordner zusammenführen, bestehende (verbundene/manuelle) behalten
            var added = 0;
            foreach (var folder in found)
            {
                if (NetworkFolders.Any(f => PathEquals(f.Path, folder.Path)))
                    continue;
                NetworkFolders.Add(folder);
                added++;
            }

            await RefreshConnectionStatesAsync();
            StatusText = Loc.T("{0} Netzwerkordner gefunden ({1} neu).", found.Count, added) +
                         (found.Count == 0 ? Loc.T(" Tipp: Ordner über \\\\Server\\Freigabe manuell hinzufügen.") : "");
        }
        catch (Exception ex)
        {
            StatusText = Loc.T("Fehler beim Scannen: {0}", ex.Message);
        }
        finally
        {
            IsScanning = false;
        }
    }

    /// <summary>
    /// Fügt einen Ordner per UNC-Pfad manuell hinzu (z.B. wenn der Scan ihn nicht findet).
    /// </summary>
    [RelayCommand]
    private async Task AddManualFolderAsync()
    {
        var folder = NetworkDiscoveryService.ParseUncPath(ManualPath);
        if (folder == null)
        {
            StatusText = Loc.T("Ungültiger Pfad. Format: \\\\Server\\Freigabe");
            return;
        }

        var existing = NetworkFolders.FirstOrDefault(f => PathEquals(f.Path, folder.Path));
        if (existing != null)
        {
            SelectedFolder = existing;
            StatusText = Loc.T("{0} ist bereits in der Liste.", existing.DisplayName);
            return;
        }

        folder.IsManual = true;
        NetworkFolders.Add(folder);
        _settings.ManualFolders.Add(folder.Path);
        SettingsService.Save(_settings);

        SelectedFolder = folder;
        ManualPath = string.Empty;
        folder.IsConnected = await NetworkDiscoveryService.IsPathAccessibleAsync(folder.Path);
        StatusText = Loc.T("{0} hinzugefügt", folder.DisplayName) + (folder.IsConnected ? Loc.T(" (erreichbar).") : Loc.T(". Jetzt verbinden."));
    }

    private bool HasManualSelection() => SelectedFolder?.IsManual == true;

    [RelayCommand(CanExecute = nameof(HasManualSelection))]
    private void RemoveManualFolder()
    {
        if (SelectedFolder is not { IsManual: true } folder)
            return;

        _settings.ManualFolders.RemoveAll(p => PathEquals(p, folder.Path));
        SettingsService.Save(_settings);
        NetworkFolders.Remove(folder);
        StatusText = Loc.T("{0} entfernt.", folder.DisplayName);
    }

    private bool CanConnect() => SelectedFolder != null && !IsConnecting;

    /// <summary>
    /// Fragt Benutzername und Passwort ab und verbindet mit dem ausgewählten Ordner.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectToFolderAsync()
    {
        if (SelectedFolder is not { } folder)
            return;

        // 1. Gespeichertes Passwort vorhanden → ohne Nachfrage verbinden
        if (await TryConnectWithSavedCredentialsAsync(folder))
        {
            StatusText = Loc.T("Verbunden mit {0} (gespeichertes Passwort).", folder.DisplayName);
            return;
        }

        // 2. Sonst Benutzername/Passwort abfragen
        var lastUser = CredentialService.GetUsername(folder.ComputerName);
        if (lastUser == null)
            _settings.LastUsernames.TryGetValue(folder.ComputerName, out lastUser);
        lastUser ??= folder.SuggestedUsername;

        var dialog = new Dialogs.PasswordDialog(folder, lastUser)
        {
            Owner = Application.Current.MainWindow
        };
        if (dialog.ShowDialog() != true)
            return;

        IsConnecting = true;
        StatusText = Loc.T("Verbinde mit {0}...", folder.DisplayName);
        try
        {
            await NetworkDiscoveryService.ConnectToFolderAsync(folder.Path, dialog.Username, dialog.Password, dialog.Remember);

            folder.IsConnected = true;
            folder.Username = dialog.Username;
            _settings.LastUsernames[folder.ComputerName] = dialog.Username;
            SettingsService.Save(_settings);

            if (dialog.SavePassword)
            {
                try
                {
                    CredentialService.Save(folder.ComputerName, dialog.Username, dialog.Password);
                    MarkSavedCredentials(folder.ComputerName, true);
                }
                catch (Exception ex)
                {
                    StatusText = Loc.T("Verbunden, aber Passwort konnte nicht gespeichert werden: {0}", ex.Message);
                    return;
                }
            }
            StatusText = Loc.T("Verbunden mit {0}.", folder.DisplayName) + (dialog.SavePassword ? Loc.T(" Passwort gespeichert.") : "");
        }
        catch (Exception ex)
        {
            StatusText = Loc.T("Verbindung zu {0} fehlgeschlagen.", folder.DisplayName);
            MessageBox.Show(
                Loc.T("Verbindung zu {0} fehlgeschlagen:\n\n{1}", folder.DisplayName, ex.Message),
                Loc.T("Verbindungsfehler"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsConnecting = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task DisconnectFromFolderAsync()
    {
        if (SelectedFolder is not { } folder)
            return;

        try
        {
            await NetworkDiscoveryService.DisconnectFolderAsync(folder.Path);
            folder.IsConnected = await NetworkDiscoveryService.IsPathAccessibleAsync(folder.Path);
            folder.Username = string.Empty;
            StatusText = folder.IsConnected
                ? Loc.T("{0} ist weiterhin ohne Anmeldung erreichbar.", folder.DisplayName)
                : Loc.T("Verbindung zu {0} getrennt.", folder.DisplayName);
        }
        catch (Exception ex)
        {
            StatusText = Loc.T("Fehler beim Trennen: {0}", ex.Message);
        }
    }

    private bool HasSelection() => SelectedFolder != null;

    private bool HasSavedPassword() => SelectedFolder?.HasSavedCredentials == true;

    /// <summary>
    /// Gespeichertes Passwort für den Server des ausgewählten Ordners löschen.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasSavedPassword))]
    private async Task ForgetPasswordAsync()
    {
        if (SelectedFolder is not { } folder)
            return;

        CredentialService.Delete(folder.ComputerName);
        MarkSavedCredentials(folder.ComputerName, false);
        try
        {
            await NetworkDiscoveryService.DisconnectFolderAsync(folder.Path);
        }
        catch
        {
            // war nicht verbunden
        }
        folder.IsConnected = await NetworkDiscoveryService.IsPathAccessibleAsync(folder.Path);
        StatusText = Loc.T("Gespeichertes Passwort für {0} gelöscht.", folder.ComputerName);
    }

    private void MarkSavedCredentials(string server, bool saved)
    {
        foreach (var f in NetworkFolders.Where(f => string.Equals(f.ComputerName, server, StringComparison.OrdinalIgnoreCase)))
            f.HasSavedCredentials = saved;
        ForgetPasswordCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Verbindet mit den in Windows gespeicherten Anmeldedaten (ohne Dialog).
    /// </summary>
    private async Task<bool> TryConnectWithSavedCredentialsAsync(NetworkFolder folder)
    {
        folder.HasSavedCredentials = CredentialService.Exists(folder.ComputerName);
        if (!folder.HasSavedCredentials)
            return false;

        try
        {
            var connect = NetworkDiscoveryService.ConnectToFolderAsync(folder.Path, null, null);
            if (await Task.WhenAny(connect, Task.Delay(8000)) != connect)
                return false;
            await connect;
            folder.IsConnected = true;
            folder.Username = CredentialService.GetUsername(folder.ComputerName) ?? string.Empty;
            return true;
        }
        catch
        {
            return false;
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void OpenInExplorer()
    {
        if (SelectedFolder is not { } folder)
            return;

        try
        {
            System.Diagnostics.Process.Start("explorer.exe", folder.Path);
        }
        catch (Exception ex)
        {
            StatusText = Loc.T("Ordner konnte nicht geöffnet werden: {0}", ex.Message);
        }
    }

    /// <summary>
    /// Prüft für alle Ordner, ob sie bereits erreichbar sind (z.B. gespeicherte Windows-Verbindung).
    /// </summary>
    private async Task RefreshConnectionStatesAsync()
    {
        var checks = NetworkFolders.Select(async f =>
        {
            var timeout = Task.Delay(3000);
            var check = NetworkDiscoveryService.IsPathAccessibleAsync(f.Path);
            return (Folder: f, Ok: await Task.WhenAny(check, timeout) == check && check.Result);
        }).ToList();

        foreach (var (folder, ok) in await Task.WhenAll(checks))
        {
            folder.IsConnected = ok;
            folder.IsOwnFolder = IsOwnPath(folder.Path);
            folder.HasSavedCredentials = CredentialService.Exists(folder.ComputerName);
        }

        // Ordner mit gespeichertem Passwort automatisch verbinden
        await Task.WhenAll(NetworkFolders
            .Where(f => !f.IsConnected && f.HasSavedCredentials)
            .Select(TryConnectWithSavedCredentialsAsync));
    }

    #endregion

    #region Upload

    private bool CanUpload() => SelectedFolder != null && !IsUploading;

    [RelayCommand(CanExecute = nameof(CanUpload))]
    private async Task UploadFileAsync()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = Loc.T("Alle Dateien (*.*)|*.*"),
            Title = Loc.T("Datei zum Hochladen auswählen")
        };

        if (dialog.ShowDialog() == true)
            await UploadFilesAsync(new[] { dialog.FileName });
    }

    [RelayCommand(CanExecute = nameof(CanUpload))]
    private async Task UploadMultipleFilesAsync()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = Loc.T("Alle Dateien (*.*)|*.*"),
            Title = Loc.T("Dateien zum Hochladen auswählen"),
            Multiselect = true
        };

        if (dialog.ShowDialog() == true)
            await UploadFilesAsync(dialog.FileNames);
    }

    private bool CanCancelUpload() => IsUploading;

    [RelayCommand(CanExecute = nameof(CanCancelUpload))]
    private void CancelUpload() => _uploadCts?.Cancel();

    /// <summary>
    /// Lädt Dateien in den ausgewählten Ordner hoch (auch per Drag &amp; Drop aufgerufen).
    /// </summary>
    public async Task UploadFilesAsync(IReadOnlyCollection<string> files)
    {
        if (IsUploading)
        {
            StatusText = Loc.T("Es läuft bereits ein Upload.");
            return;
        }

        if (SelectedFolder is not { } folder)
        {
            StatusText = Loc.T("Bitte wählen Sie links einen Zielordner aus.");
            return;
        }

        var fileList = files.Where(File.Exists).ToList();
        if (fileList.Count == 0)
        {
            StatusText = Loc.T("Keine Dateien zum Hochladen (Ordner werden nicht unterstützt).");
            return;
        }

        if (!await NetworkDiscoveryService.IsPathAccessibleAsync(folder.Path))
        {
            folder.IsConnected = false;
            StatusText = Loc.T("{0} ist nicht erreichbar. Bitte zuerst verbinden.", folder.DisplayName);
            return;
        }
        folder.IsConnected = true;

        IsUploading = true;
        UploadProgress = 0;
        _uploadCts = new CancellationTokenSource();
        lock (_uploadProgressPerFile)
        {
            _uploadProgressPerFile.Clear();
            _uploadTotalBytes = fileList.Sum(f => new FileInfo(f).Length);
        }

        UploadStatus = fileList.Count == 1
            ? Loc.T("Lade {0} hoch...", Path.GetFileName(fileList[0]))
            : Loc.T("Lade {0} Dateien hoch...", fileList.Count);

        try
        {
            var results = await _fileTransferService.UploadFilesAsync(fileList, folder.Path, 3, _uploadCts.Token);
            var ok = results.Count(r => r.Success);
            var failed = results.Where(r => !r.Success).ToList();

            UploadProgress = 100;
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
            IsUploading = false;
            _uploadCts.Dispose();
            _uploadCts = null;
        }
    }

    private void OnUploadProgress(object? sender, TransferProgressEventArgs e)
    {
        double percent;
        lock (_uploadProgressPerFile)
        {
            _uploadProgressPerFile[e.FileName] = e.BytesTransferred;
            percent = _uploadTotalBytes > 0
                ? _uploadProgressPerFile.Values.Sum() * 100.0 / _uploadTotalBytes
                : 100;
        }

        Application.Current?.Dispatcher.BeginInvoke(() => UploadProgress = Math.Min(100, percent));
    }

    #endregion

    #region Überwachung

    [RelayCommand]
    private void BrowseOwnFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = Loc.T("Eigenen Ordner zur Überwachung auswählen")
        };
        if (!string.IsNullOrWhiteSpace(OwnFolderPath) && Directory.Exists(OwnFolderPath))
            dialog.InitialDirectory = OwnFolderPath;

        if (dialog.ShowDialog() == true)
            OwnFolderPath = dialog.FolderName;
    }

    private bool CanUseAsOwn() => SelectedFolder != null;

    [RelayCommand(CanExecute = nameof(CanUseAsOwn))]
    private void UseAsOwnFolder()
    {
        if (SelectedFolder is { } folder)
            OwnFolderPath = folder.Path;
    }

    private bool CanStartMonitoring() => !IsMonitoring && !string.IsNullOrWhiteSpace(OwnFolderPath);

    [RelayCommand(CanExecute = nameof(CanStartMonitoring))]
    private async Task StartMonitoringAsync()
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

        if (!await NetworkDiscoveryService.IsPathAccessibleAsync(fullPath))
        {
            StatusText = Loc.T("Ordner nicht erreichbar: {0}", fullPath);
            MonitorStatus = Loc.T("Ordner nicht erreichbar – ggf. zuerst verbinden.");
            return;
        }

        try
        {
            _folderMonitor.StartMonitoring(fullPath);
        }
        catch (Exception ex)
        {
            StatusText = Loc.T("Überwachung konnte nicht gestartet werden: {0}", ex.Message);
            return;
        }

        OwnFolderPath = fullPath;
        _settings.OwnFolderPath = fullPath;
        SettingsService.Save(_settings);

        foreach (var f in NetworkFolders)
            f.IsOwnFolder = IsOwnPath(f.Path);

        IsMonitoring = true;
        MonitorStatus = Loc.T("Überwachung von {0} aktiv.", fullPath);
        StatusText = Loc.T("Überwachung aktiv.");
    }

    private bool CanStopMonitoring() => IsMonitoring;

    [RelayCommand(CanExecute = nameof(CanStopMonitoring))]
    private void StopMonitoring()
    {
        _folderMonitor.StopMonitoring();
        IsMonitoring = false;
        MonitorStatus = Loc.T("Überwachung gestoppt.");
        StatusText = Loc.T("Überwachung gestoppt.");
    }

    [RelayCommand]
    private void OpenReceivedFile(NewFileEventArgs? file)
    {
        if (file == null)
            return;
        try
        {
            if (File.Exists(file.FullPath))
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{file.FullPath}\"");
            else
                StatusText = Loc.T("{0} existiert nicht mehr.", file.FileName);
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
    }

    private void OnNewFileDetected(object? sender, NewFileEventArgs e)
    {
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            ReceivedFiles.Insert(0, e);
            while (ReceivedFiles.Count > 50)
                ReceivedFiles.RemoveAt(ReceivedFiles.Count - 1);

            NotificationService.ShowToastNotification(
                Loc.T("Neue Datei erhalten"),
                Loc.T("In Ihrem Ordner wurde eine neue Datei abgelegt:\n{0}", e.FileName),
                e.FullPath);

            StatusText = Loc.T("Neue Datei: {0}", e.FileName);
        });
    }

    private void OnMonitorError(object? sender, ErrorEventArgs e)
    {
        Application.Current?.Dispatcher.BeginInvoke(async () =>
        {
            var path = OwnFolderPath;
            StopMonitoring();
            MonitorStatus = Loc.T("Überwachung unterbrochen: {0}. Neuer Versuch in 30 s...", e.GetException().Message);

            // Automatisch erneut verbinden (z.B. nach kurzem Netzwerkausfall)
            await Task.Delay(TimeSpan.FromSeconds(30));
            if (!IsMonitoring && PathEquals(path, OwnFolderPath))
                await StartMonitoringAsync();
        });
    }

    #endregion

    private bool IsOwnPath(string path) =>
        (!string.IsNullOrWhiteSpace(OwnFolderPath) && PathEquals(path, OwnFolderPath)) ||
        (_machineConfig != null && PathEquals(path, _machineConfig.UncPath) &&
         !string.IsNullOrWhiteSpace(OwnFolderPath) && PathEquals(_machineConfig.SharePath, OwnFolderPath));

    #region Eigener Netzwerkordner / Peers

    private void UpdateShareInfo()
    {
        OwnShareInfo = _machineConfig == null
            ? Loc.T("Noch kein eigener Netzwerkordner eingerichtet.")
            : $"{_machineConfig.UncPath}   ({_machineConfig.SharePath})";

        if (_machineConfig != null)
        {
            NewSharePath = _machineConfig.SharePath;
            NewShareName = _machineConfig.ShareName;
        }

        ShareAccessInfo = _machineConfig?.IsProtected == true
            ? Loc.T("Andere verbinden sich mit Benutzername  {0}  und dem festgelegten Passwort.", _machineConfig.AccessUserQualified)
            : string.Empty;
        ShareEncryptionInfo = _machineConfig?.Encrypted == true
            ? Loc.T("Dateiübertragung verschlüsselt (SMB 3)")
            : string.Empty;

        OnPropertyChanged(nameof(HasOwnShare));
        OnPropertyChanged(nameof(IsShareProtected));
        OnPropertyChanged(nameof(IsShareUnprotected));
    }

    [ObservableProperty]
    private string _shareAccessInfo = string.Empty;

    [ObservableProperty]
    private string _shareEncryptionInfo = string.Empty;

    public bool IsShareProtected => _machineConfig?.IsProtected == true;
    public bool IsShareUnprotected => _machineConfig != null && !_machineConfig.IsProtected;

    private void AddOwnShareToList()
    {
        if (_machineConfig == null)
            return;

        var own = NetworkDiscoveryService.ParseUncPath(_machineConfig.UncPath);
        if (own == null || NetworkFolders.Any(f => PathEquals(f.Path, own.Path)))
            return;

        own.Description = Loc.T("Mein Netzwerkordner");
        own.IsOwnFolder = IsOwnPath(own.Path);
        NetworkFolders.Insert(0, own);
    }

    /// <summary>
    /// Legt den eigenen Netzwerkordner an bzw. ändert Passwort/Schutz (fragt per UAC nach Administratorrechten).
    /// Wird vom Fenster aufgerufen, weil Passwortfelder nicht gebunden werden können.
    /// </summary>
    public async Task SetupOwnShareAsync(string password, string confirm)
    {
        if (string.IsNullOrWhiteSpace(NewSharePath) || string.IsNullOrWhiteSpace(NewShareName))
        {
            StatusText = Loc.T("Bitte Pfad und Freigabename angeben.");
            return;
        }

        if (password != confirm)
        {
            MessageBox.Show(Loc.T("Die Passwörter stimmen nicht überein."), Loc.T("Netzwerkordner"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (password.Length == 0)
        {
            var answer = MessageBox.Show(
                Loc.T("Ohne Passwort ist Ihr Netzwerkordner NICHT geschützt!\n\n" +
                "Jeder im Netzwerk mit Zugang zu diesem PC kann dann Dateien lesen, verändern oder löschen – " +
                "und auch schädliche Dateien ablegen. Das ist gefährlich, besonders in fremden oder öffentlichen Netzwerken.\n\n" +
                "Trotzdem ohne Passwort fortfahren?"),
                Loc.T("Warnung: Ordner ohne Passwort"), MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes)
                return;
        }
        else if (password.Length < 6)
        {
            MessageBox.Show(Loc.T("Bitte ein Passwort mit mindestens 6 Zeichen wählen."), Loc.T("Netzwerkordner"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        StatusText = Loc.T("Netzwerkordner wird eingerichtet (Administratorrechte erforderlich)...");
        var ok = await ShareSetupService.RunElevatedSetupAsync(NewSharePath.Trim(), NewShareName.Trim(), password);
        _machineConfig = ShareSetupService.LoadMachineConfig();
        UpdateShareInfo();

        if (!ok || _machineConfig == null)
        {
            StatusText = Loc.T(@"Netzwerkordner konnte nicht eingerichtet werden (abgebrochen oder Fehler – siehe %ProgramData%\NetworkFolderManager\setup.log).");
            return;
        }

        // Neuen Ordner als eigenen Ordner überwachen
        if (IsMonitoring)
            StopMonitoring();
        OwnFolderPath = _machineConfig.SharePath;
        MonitorOnStartup = true;
        _settings.AppliedShareCreatedAt = _machineConfig.CreatedAt;
        await StartMonitoringAsync();
        AddOwnShareToList();
        await RefreshConnectionStatesAsync();

        Chat.AnnounceNow();
        StatusText = Loc.T("Netzwerkordner {0} eingerichtet und wird überwacht.", _machineConfig.UncPath) +
                     (_machineConfig.IsProtected ? Loc.T(" Zugang: {0}", _machineConfig.AccessUserQualified) : Loc.T(" ACHTUNG: ohne Passwort!"));
    }

    [RelayCommand]
    private void OpenOwnShare()
    {
        if (_machineConfig != null && Directory.Exists(_machineConfig.SharePath))
            System.Diagnostics.Process.Start("explorer.exe", _machineConfig.SharePath);
    }

    /// <summary>
    /// Netzwerkordner anderer NetworkFolderManager-Nutzer automatisch in die Liste aufnehmen.
    /// </summary>
    private async void OnPeerShareDiscovered(object? sender, Peer peer)
    {
        try
        {
            await AddPeerShareAsync(peer);
        }
        catch (Exception ex)
        {
            LogService.Error("PeerShare", ex);
        }
    }

    private async Task AddPeerShareAsync(Peer peer)
    {
        if (string.IsNullOrEmpty(peer.SharePath))
            return;

        var folder = NetworkFolders.FirstOrDefault(f => PathEquals(f.Path, peer.SharePath));
        if (folder == null)
        {
            folder = NetworkDiscoveryService.ParseUncPath(peer.SharePath);
            if (folder == null)
                return;
            NetworkFolders.Add(folder);
        }
        folder.Description = Loc.T("Ordner von {0}", peer.DisplayName);
        folder.SuggestedUsername = peer.ShareUser;

        var check = NetworkDiscoveryService.IsPathAccessibleAsync(folder.Path);
        folder.IsConnected = await Task.WhenAny(check, Task.Delay(3000)) == check && check.Result;
    }

    #endregion

    private static bool PathEquals(string a, string b) =>
        string.Equals(a.TrimEnd('\\'), b.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    public void Dispose()
    {
        _uploadCts?.Cancel();
        Chat.Dispose();
        _folderMonitor.Dispose();
        _fileTransferService.Dispose();
    }
}
