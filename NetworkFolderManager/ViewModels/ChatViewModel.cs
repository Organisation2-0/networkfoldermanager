using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NetworkFolderManager.Models;
using NetworkFolderManager.Services;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Security.Authentication;
using System.IO;

namespace NetworkFolderManager.ViewModels;

/// <summary>
/// Chat mit anderen NetworkFolderManager-Nutzern im lokalen Netzwerk (Text, Bilder, Dateien).
/// </summary>
public partial class ChatViewModel : ObservableObject, IDisposable
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".tif", ".tiff"
    };

    private readonly AppSettings _settings;
    private readonly Func<MachineConfig?> _machineConfig;
    private readonly ConcurrentDictionary<string, string> _pins = new();
    private readonly PeerDiscoveryService _discovery;
    private readonly ChatService _chat;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private System.Threading.Timer? _retryTimer;

    public ChatViewModel(AppSettings settings, Func<MachineConfig?> machineConfig)
    {
        _settings = settings;
        _machineConfig = machineConfig;

        foreach (var peer in ChatStore.LoadAll().OrderByDescending(p => p.LastActivity))
        {
            Peers.Add(peer);
            if (!string.IsNullOrEmpty(peer.CertFingerprint))
                _pins[peer.Id] = peer.CertFingerprint;
        }

        InitReceiveFolder();

        var certificate = CertificateService.GetOrCreate(_settings.PeerId);
        OwnSecurityCode = CertificateService.ShortCode(CertificateService.Fingerprint(certificate));

        _chat = new ChatService(() => new ChatEnvelope
        {
            From = _settings.PeerId,
            FromName = _settings.DisplayName,
            FromMachine = Environment.MachineName,
            FromPort = _chat!.Port
        }, certificate, VerifySender);
        _chat.MessageReceived += OnMessageReceived;
        _chat.SignalReceived += OnSignalReceived;

        _discovery = new PeerDiscoveryService(() =>
        {
            var config = _machineConfig();
            return new PeerAnnouncement
            {
                Id = _settings.PeerId,
                Name = _settings.DisplayName,
                Machine = Environment.MachineName,
                Port = _chat.Port,
                Share = config?.UncPath,
                ShareUser = config?.AccessUserQualified,
                Fp = _chat.OwnFingerprint
            };
        });
        _discovery.PeerSeen += (_, a) => Dispatch("PeerSeen", () => OnPeerSeen(a));
        _discovery.PeerLost += (_, id) => Dispatch("PeerLost", () => OnPeerLost(id));
    }

    #region Properties

    public ObservableCollection<Peer> Peers { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    [NotifyCanExecuteChangedFor(nameof(AttachFileCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteChatCommand))]
    [NotifyPropertyChangedFor(nameof(HasSelectedPeer))]
    private Peer? _selectedPeer;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private string _messageText = string.Empty;

    [ObservableProperty]
    private string _networkStatus = Loc.T("Chat wird gestartet...");

    /// <summary>Gesamtzahl ungelesener Nachrichten (für das Badge am Chat-Tab).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnread))]
    private int _totalUnread;

    /// <summary>Wird vom Fenster gesetzt: true, wenn der Chat-Tab sichtbar und das Fenster aktiv ist.</summary>
    public bool IsChatVisible { get; set; }

    /// <summary>Ordner für empfangene Bilder/Dateien.</summary>
    [ObservableProperty]
    private string _receiveFolder = string.Empty;

    partial void OnReceiveFolderChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;
        ChatService.ReceiveFolder = value;
        _settings.ChatReceiveFolder = value;
        SettingsService.Save(_settings);
        try { Directory.CreateDirectory(value); } catch { /* wird beim Empfang erneut versucht */ }
    }

    /// <summary>
    /// Empfangsordner bestimmen: Auswahl aus dem Installer (falls neu) → Einstellungen → Dokumente\NetworkFolderManager.
    /// </summary>
    private void InitReceiveFolder()
    {
        var fromInstaller = Platform.Current.GetInstallerReceiveFolder();

        if (SettingsService.Profile.Length == 0 && !string.IsNullOrWhiteSpace(fromInstaller) &&
            !string.Equals(fromInstaller, _settings.InstallerReceiveFolderApplied, StringComparison.OrdinalIgnoreCase))
        {
            _settings.ChatReceiveFolder = fromInstaller;
            _settings.InstallerReceiveFolderApplied = fromInstaller;
        }

        var folder = string.IsNullOrWhiteSpace(_settings.ChatReceiveFolder)
            ? ChatService.DefaultReceiveFolder
            : _settings.ChatReceiveFolder;

        ReceiveFolder = folder; // speichert + legt den Ordner an
    }

    [RelayCommand]
    private async Task BrowseReceiveFolderAsync()
    {
        var folder = await Platform.Current.PickFolderAsync(
            Loc.T("Ordner für empfangene Bilder und Dateien wählen"),
            Directory.Exists(ReceiveFolder) ? ReceiveFolder : null);
        if (!string.IsNullOrEmpty(folder))
            ReceiveFolder = folder;
    }

    [RelayCommand]
    private void ResetReceiveFolder() => ReceiveFolder = ChatService.DefaultReceiveFolder;

    /// <summary>Eigene Sicherheitsnummer (Kurzform des Zertifikat-Fingerabdrucks).</summary>
    public string OwnSecurityCode { get; }

    public bool HasSelectedPeer => SelectedPeer != null;
    public bool HasUnread => TotalUnread > 0;
    public int OnlineCount => Peers.Count(p => p.IsOnline);

    /// <summary>Wird ausgelöst, wenn ein Peer mit eigenem Netzwerkordner gefunden wurde.</summary>
    public event EventHandler<Peer>? PeerShareDiscovered;

    /// <summary>Wird ausgelöst, wenn eine Nachricht hinzugefügt wurde (für Auto-Scroll).</summary>
    public event EventHandler? MessageAdded;

    partial void OnSelectedPeerChanged(Peer? value)
    {
        MarkAsRead();
        MessageAdded?.Invoke(this, EventArgs.Empty);
    }

    #endregion

    public void Start()
    {
        try
        {
            _chat.Start();
            _discovery.Start();
            // Wartende Nachrichten regelmäßig erneut zustellen (unabhängig von der Erkennung)
            _retryTimer = new System.Threading.Timer(_ => Dispatch("Retry", RetryPendingAll),
                null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
            NetworkStatus = Loc.T("Suche nach anderen Nutzern im Netzwerk... (Port {0})", _chat.Port);
        }
        catch (Exception ex)
        {
            NetworkStatus = Loc.T("Chat konnte nicht gestartet werden: {0}", ex.Message);
        }
    }

    /// <summary>Eigenen Namen/Ordner sofort an alle melden.</summary>
    public void AnnounceNow() => _discovery.AnnounceNow();

    public void MarkAsRead()
    {
        if (SelectedPeer is { } peer && IsChatVisible && peer.UnreadCount > 0)
        {
            peer.UnreadCount = 0;
            ChatStore.Save(peer);
        }
        UpdateUnread();
    }

    #region Discovery

    /// <summary>
    /// Prüft eingehende Verbindungen: bekannter Kontakt → Fingerabdruck muss passen;
    /// unbekannter Kontakt → Fingerabdruck wird gemerkt (Trust on first use).
    /// Läuft auf einem Hintergrund-Thread.
    /// </summary>
    private bool VerifySender(string peerId, string fingerprint)
    {
        if (string.IsNullOrEmpty(fingerprint))
            return false;
        var pinned = _pins.GetOrAdd(peerId, fingerprint);
        return string.Equals(pinned, fingerprint, StringComparison.OrdinalIgnoreCase);
    }

    private void OnPeerSeen(PeerAnnouncement a)
    {
        if (string.IsNullOrEmpty(a.Fp))
            return; // ältere Version ohne Verschlüsselung → ignorieren

        var peer = Peers.FirstOrDefault(p => p.Id == a.Id);

        // Bekannter Kontakt mit anderem Schlüssel: nichts übernehmen, nur warnen
        if (peer != null && !string.IsNullOrEmpty(peer.CertFingerprint) &&
            !string.Equals(peer.CertFingerprint, a.Fp, StringComparison.OrdinalIgnoreCase))
        {
            if (!peer.KeyChanged)
            {
                peer.KeyChanged = true;
                peer.PendingFingerprint = a.Fp;
                OnPropertyChanged(nameof(SelectedPeer));
            }
            return;
        }

        var cameOnline = peer == null || !peer.IsOnline;
        var shareChanged = false;

        if (peer == null)
        {
            peer = new Peer { Id = a.Id };
            Peers.Add(peer);
        }

        peer.DisplayName = string.IsNullOrWhiteSpace(a.Name) ? a.Machine : a.Name;
        peer.MachineName = a.Machine;
        peer.Address = a.Address;
        peer.Port = a.Port;
        peer.ShareUser = a.ShareUser;
        if (string.IsNullOrEmpty(peer.CertFingerprint))
        {
            peer.CertFingerprint = a.Fp;
            _pins[peer.Id] = a.Fp;
            ChatStore.Save(peer);
        }
        if (peer.SharePath != a.Share)
        {
            peer.SharePath = a.Share;
            shareChanged = true;
        }
        peer.IsOnline = true;
        peer.LastSeen = DateTime.Now;

        if (cameOnline)
        {
            ChatStore.Save(peer);
            SortPeers();
            UpdateNetworkStatus();
        }

        // Wartende Nachrichten zustellen (bei jedem Lebenszeichen erneut versuchen)
        if (_sendLock.CurrentCount > 0 && peer.Messages.Any(m => m.Outgoing && m.Status == MessageStatus.Pending))
            _ = FlushOutboxAsync(peer);

        if ((cameOnline || shareChanged) && !string.IsNullOrEmpty(peer.SharePath))
            PeerShareDiscovered?.Invoke(this, peer);
    }

    private void OnPeerLost(string id)
    {
        var peer = Peers.FirstOrDefault(p => p.Id == id);
        if (peer == null)
            return;
        peer.IsOnline = false;
        ChatStore.Save(peer);
        SortPeers();
        UpdateNetworkStatus();
    }

    private void UpdateNetworkStatus()
    {
        OnPropertyChanged(nameof(OnlineCount));
        var online = OnlineCount;
        NetworkStatus = online == 0
            ? Loc.T("Niemand online. Andere Nutzer erscheinen automatisch, sobald sie das Programm starten.")
            : Loc.T("{0} Nutzer online", online);
    }

    private void SortPeers()
    {
        var sorted = Peers
            .OrderByDescending(p => p.IsOnline)
            .ThenByDescending(p => p.LastActivity)
            .ThenBy(p => p.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        for (var i = 0; i < sorted.Count; i++)
        {
            var current = Peers.IndexOf(sorted[i]);
            if (current != i)
                Peers.Move(current, i);
        }
    }

    #endregion

    #region Senden

    private bool CanSend() => SelectedPeer != null && !string.IsNullOrWhiteSpace(MessageText);

    [RelayCommand(CanExecute = nameof(CanSend))]
    private void Send()
    {
        if (SelectedPeer is not { } peer)
            return;

        var text = MessageText.Trim();
        MessageText = string.Empty;
        Enqueue(peer, new ChatMessage { Outgoing = true, Kind = MessageKind.Text, Text = text });
    }

    private bool HasPeer() => SelectedPeer != null;

    [RelayCommand(CanExecute = nameof(HasPeer))]
    private async Task AttachFileAsync()
    {
        var files = await Platform.Current.PickFilesAsync(Loc.T("Bilder oder Dateien senden"));
        if (files.Count > 0)
            SendFiles(files);
    }

    /// <summary>
    /// Sendet Dateien an den ausgewählten Kontakt (Dialog oder Drag &amp; Drop).
    /// Bilder werden als Bild angezeigt, alles andere als Datei.
    /// </summary>
    public void SendFiles(IEnumerable<string> paths)
    {
        if (SelectedPeer is not { } peer)
            return;

        var caption = MessageText.Trim();
        var first = true;
        foreach (var path in paths.Where(File.Exists))
        {
            var isImage = ImageExtensions.Contains(Path.GetExtension(path));
            Enqueue(peer, new ChatMessage
            {
                Outgoing = true,
                Kind = isImage ? MessageKind.Image : MessageKind.File,
                FileName = Path.GetFileName(path),
                FilePath = path,
                FileSize = new FileInfo(path).Length,
                // Eingegebenen Text als Bildunterschrift an die erste Datei hängen
                Text = first ? caption : string.Empty
            });
            first = false;
        }
        if (!first)
            MessageText = string.Empty;
    }

    /// <summary>
    /// Bild aus der Zwischenablage senden (Strg+V im Eingabefeld).
    /// </summary>
    public async Task<bool> TrySendClipboardImageAsync()
    {
        if (SelectedPeer == null)
            return false;

        try
        {
            var content = await Platform.Current.GetClipboardAsync();
            if (content?.Files is { Count: > 0 } files)
            {
                SendFiles(files);
                return true;
            }

            if (content?.ImagePng is not { Length: > 0 } png)
                return false;

            var dir = Path.Combine(ChatService.ReceiveFolder, "Gesendet");
            Directory.CreateDirectory(dir);
            var path = ChatService.GetUniquePath(Path.Combine(dir, $"Bild_{DateTime.Now:yyyyMMdd_HHmmss}.png"));
            await File.WriteAllBytesAsync(path, png);
            SendFiles(new[] { path });
            return true;
        }
        catch
        {
            return false;
        }
    }

    [RelayCommand]
    private async Task RetryAsync(ChatMessage? message)
    {
        if (message == null || SelectedPeer is not { } peer || message.Status != MessageStatus.Failed)
            return;
        message.Status = MessageStatus.Pending;
        message.Error = null;
        await FlushOutboxAsync(peer);
    }

    [RelayCommand]
    private void OpenAttachment(ChatMessage? message)
    {
        if (message?.FilePath is not { } path)
            return;
        try
        {
            if (File.Exists(path))
                Platform.Current.OpenFile(path);
        }
        catch
        {
            // keine verknüpfte Anwendung
        }
    }

    [RelayCommand]
    private void ShowInFolder(ChatMessage? message)
    {
        if (message?.FilePath is { } path && File.Exists(path))
            Platform.Current.RevealInFileManager(path);
    }

    [RelayCommand]
    private void OpenReceiveFolder()
    {
        Directory.CreateDirectory(ChatService.ReceiveFolder);
        Platform.Current.OpenFolder(ChatService.ReceiveFolder);
    }

    /// <summary>
    /// Neuen Schlüssel eines Kontakts akzeptieren (z.B. nach Neuinstallation – vorher am besten
    /// die Sicherheitsnummer mit der Person abgleichen).
    /// </summary>
    [RelayCommand]
    private void AcceptNewKey()
    {
        if (SelectedPeer is not { KeyChanged: true, PendingFingerprint: { } fp } peer)
            return;

        peer.CertFingerprint = fp;
        _pins[peer.Id] = fp;
        peer.PendingFingerprint = null;
        peer.KeyChanged = false;
        foreach (var m in peer.Messages.Where(m => m.Outgoing && m.Status == MessageStatus.Failed))
            m.Status = MessageStatus.Pending;
        ChatStore.Save(peer);
        _discovery.AnnounceNow();
    }

    [RelayCommand(CanExecute = nameof(HasPeer))]
    private async Task DeleteChatAsync()
    {
        if (SelectedPeer is not { } peer)
            return;

        var confirmed = await Platform.Current.ConfirmAsync(
            Loc.T("Chatverlauf mit {0} löschen? Empfangene Dateien bleiben erhalten.", peer.DisplayName),
            Loc.T("Chat löschen"));
        if (!confirmed)
            return;

        peer.Messages.Clear();
        peer.LastMessagePreview = string.Empty;
        peer.UnreadCount = 0;
        if (peer.IsOnline)
        {
            ChatStore.Save(peer);
        }
        else
        {
            ChatStore.Delete(peer);
            Peers.Remove(peer);
            SelectedPeer = null;
        }
        UpdateUnread();
    }

    private void Enqueue(Peer peer, ChatMessage message)
    {
        message.Status = MessageStatus.Pending;
        AddMessage(peer, message);
        _ = FlushOutboxAsync(peer);
    }

    /// <summary>
    /// Sendet alle wartenden Nachrichten an einen Peer der Reihe nach.
    /// </summary>
    private async Task FlushOutboxAsync(Peer peer)
    {
        if (!peer.IsOnline)
            return;

        await _sendLock.WaitAsync();
        try
        {
            await FlushOutboxCoreAsync(peer);
        }
        catch (Exception ex)
        {
            LogService.Error("Chat/Flush", ex);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task FlushOutboxCoreAsync(Peer peer)
    {
        {
            foreach (var msg in peer.Messages.Where(m => m.Outgoing && m.Status == MessageStatus.Pending).ToList())
            {
                if (!peer.IsOnline)
                    break;

                msg.Status = MessageStatus.Sending;
                msg.Progress = 0;
                var progress = new Progress<double>(p => msg.Progress = p);
                try
                {
                    await _chat.SendAsync(peer, msg, progress);
                    msg.Status = MessageStatus.Delivered;
                }
                catch (FileNotFoundException ex)
                {
                    msg.Status = MessageStatus.Failed;
                    msg.Error = ex.Message;
                }
                catch (AuthenticationException)
                {
                    // Empfänger hat nicht den gemerkten Schlüssel → nicht senden
                    msg.Status = MessageStatus.Failed;
                    msg.Error = Loc.T("Sicherheitsprüfung fehlgeschlagen – der Schlüssel des Empfängers stimmt nicht.");
                    break;
                }
                catch (IOException ex) when (ex.InnerException is not System.Net.Sockets.SocketException)
                {
                    // Empfänger hat abgelehnt → nicht endlos wiederholen
                    msg.Status = MessageStatus.Failed;
                    msg.Error = ex.Message;
                }
                catch (Exception ex)
                {
                    // Nicht erreichbar → später erneut versuchen
                    LogService.Info($"Chat: Senden an {peer.DisplayName} ({peer.Address}:{peer.Port}) fehlgeschlagen: {ex.GetBaseException().Message}");
                    msg.Status = MessageStatus.Pending;
                    msg.Error = ex.Message;
                    break;
                }
                finally
                {
                    ChatStore.Save(peer);
                }
            }
        }
    }

    #endregion

    #region Anruf-Signale

    /// <summary>Anruf-Signal eines Kontakts (auf dem UI-Thread). Parameter: Kontakt, JSON-Inhalt.</summary>
    public event EventHandler<(Peer Peer, string Json)>? CallSignalReceived;

    private void OnSignalReceived(object? sender, SignalReceivedEventArgs e)
    {
        Dispatch("SignalReceived", () =>
        {
            var env = e.Envelope;
            var peer = Peers.FirstOrDefault(p => p.Id == env.From);
            if (peer == null)
            {
                peer = new Peer { Id = env.From, DisplayName = env.FromName, MachineName = env.FromMachine, CertFingerprint = e.SenderFingerprint };
                Peers.Add(peer);
            }
            if (!string.IsNullOrWhiteSpace(env.FromName))
                peer.DisplayName = env.FromName;
            peer.Address = e.SenderAddress;
            if (env.FromPort > 0)
                peer.Port = env.FromPort;
            peer.IsOnline = true;
            peer.LastSeen = DateTime.Now;
            CallSignalReceived?.Invoke(this, (peer, env.Text));
        });
    }

    /// <summary>Sendet ein Anruf-Signal an einen Kontakt (wirft, wenn nicht erreichbar).</summary>
    public Task SendCallSignalAsync(Peer peer, string json, CancellationToken ct = default)
    {
        if (peer.KeyChanged)
            throw new AuthenticationException(Loc.T("Sicherheitsprüfung fehlgeschlagen – der Schlüssel des Empfängers stimmt nicht."));
        return _chat.SendSignalAsync(peer, json, ct);
    }

    /// <summary>Speichert Änderungen am Kontakt (z.B. Klingelton).</summary>
    public void SavePeer(Peer peer) => ChatStore.Save(peer);

    #endregion

    #region Empfangen

    private void OnMessageReceived(object? sender, ChatReceivedEventArgs e)
    {
        Dispatch("MessageReceived", () =>
        {
            var env = e.Envelope;
            var peer = Peers.FirstOrDefault(p => p.Id == env.From);
            if (peer == null)
            {
                peer = new Peer { Id = env.From };
                Peers.Add(peer);
            }

            if (!string.IsNullOrWhiteSpace(env.FromName))
                peer.DisplayName = env.FromName;
            if (!string.IsNullOrWhiteSpace(env.FromMachine))
                peer.MachineName = env.FromMachine;
            peer.Address = e.SenderAddress;
            if (env.FromPort > 0)
                peer.Port = env.FromPort;
            peer.IsOnline = true;
            peer.LastSeen = DateTime.Now;
            if (string.IsNullOrEmpty(peer.CertFingerprint))
                peer.CertFingerprint = e.SenderFingerprint;

            // Doppelte Zustellung (z.B. nach Wiederholung) ignorieren
            if (peer.Messages.Any(m => m.Id == e.Message.Id))
                return;

            AddMessage(peer, e.Message);

            var isRead = IsChatVisible && SelectedPeer == peer;
            if (!isRead)
            {
                peer.UnreadCount++;
                UpdateUnread();
            }

            if (!isRead && _settings.ChatNotifications)
            {
                var preview = e.Message.Kind switch
                {
                    MessageKind.Image => Loc.T("📷 Bild") + (e.Message.HasText ? $": {e.Message.Text}" : ""),
                    MessageKind.File => $"📎 {e.Message.FileName}",
                    _ => e.Message.Text
                };
                Platform.Current.Notify(peer.DisplayName, Truncate(preview, 200), peer.Id);
            }

            ChatStore.Save(peer);
            SortPeers();
        });
    }

    #endregion

    /// <summary>Öffnet den Chat mit einem bestimmten Kontakt (z.B. Klick auf Benachrichtigung).</summary>
    public void SelectPeer(string peerId)
    {
        var peer = Peers.FirstOrDefault(p => p.Id == peerId);
        if (peer != null)
            SelectedPeer = peer;
    }

    private void AddMessage(Peer peer, ChatMessage message)
    {
        peer.Messages.Add(message);
        peer.LastActivity = message.Timestamp;
        peer.LastMessagePreview = message.Kind switch
        {
            MessageKind.Image => "📷 Bild",
            MessageKind.File => $"📎 {message.FileName}",
            _ => Truncate(message.Text.Replace('\n', ' '), 60)
        };
        if (message.Outgoing)
            peer.LastMessagePreview = Loc.T("Du: ") + peer.LastMessagePreview;

        ChatStore.Save(peer);
        if (peer == SelectedPeer)
            MessageAdded?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateUnread() => TotalUnread = Peers.Sum(p => p.UnreadCount);

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    /// <summary>
    /// Führt eine Aktion aus dem Netzwerk-Hintergrund auf dem UI-Thread aus. Fehler werden protokolliert
    /// und nie an die Netzwerkschleifen zurückgegeben.
    /// </summary>
    private static void Dispatch(string context, Action action)
    {
        Platform.Current.Post(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                LogService.Error($"Chat/{context}", ex);
            }
        });
    }

    /// <summary>Alle wartenden Nachrichten an erreichbare Kontakte erneut senden.</summary>
    private void RetryPendingAll()
    {
        if (_sendLock.CurrentCount == 0)
            return;
        foreach (var peer in Peers.Where(p => p.IsOnline && !p.KeyChanged &&
                     p.Messages.Any(m => m.Outgoing && m.Status == MessageStatus.Pending)).ToList())
        {
            _ = FlushOutboxAsync(peer);
        }
    }

    public void Dispose()
    {
        _retryTimer?.Dispose();
        _discovery.Dispose();
        _chat.Dispose();
    }
}
