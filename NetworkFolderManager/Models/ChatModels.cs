using NetworkFolderManager.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

namespace NetworkFolderManager.Models;

public enum MessageKind
{
    Text,
    Image,
    File
}

public enum MessageStatus
{
    /// <summary>Wartet auf Zustellung (Empfänger offline).</summary>
    Pending,
    /// <summary>Wird gerade übertragen.</summary>
    Sending,
    /// <summary>Vom Empfänger bestätigt.</summary>
    Delivered,
    /// <summary>Zustellung endgültig fehlgeschlagen.</summary>
    Failed,
    /// <summary>Eingehende Nachricht.</summary>
    Received
}

/// <summary>
/// Eine Chat-Nachricht (Text, Bild oder Datei).
/// </summary>
public partial class ChatMessage : ObservableObject
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public bool Outgoing { get; set; }
    public MessageKind Kind { get; set; }
    public string Text { get; set; } = string.Empty;
    public string? FileName { get; set; }
    public long FileSize { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.Now;

    /// <summary>Lokaler Pfad: Quelle (ausgehend) bzw. gespeicherte Datei (eingehend).</summary>
    [ObservableProperty]
    private string? _filePath;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusIcon))]
    private MessageStatus _status;

    [ObservableProperty]
    [property: JsonIgnore]
    private double _progress;

    [ObservableProperty]
    [property: JsonIgnore]
    private string? _error;

    [JsonIgnore]
    public bool IsImage => Kind == MessageKind.Image;

    [JsonIgnore]
    public bool IsFile => Kind == MessageKind.File;

    [JsonIgnore]
    public bool HasText => !string.IsNullOrEmpty(Text);

    [JsonIgnore]
    public string StatusIcon => Status switch
    {
        MessageStatus.Pending => "🕓",
        MessageStatus.Sending => "↑",
        MessageStatus.Delivered => "✓✓",
        MessageStatus.Failed => "⚠",
        _ => string.Empty
    };

    [JsonIgnore]
    public string FileSizeText => FormatSize(FileSize);

    public static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024.0:0.#} MB",
        _ => $"{bytes / 1024.0 / 1024.0 / 1024.0:0.##} GB"
    };
}

/// <summary>
/// Ein anderer NetworkFolderManager-Nutzer im Netzwerk.
/// </summary>
public partial class Peer : ObservableObject
{
    public string Id { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Initial))]
    private string _displayName = string.Empty;

    [ObservableProperty]
    private string _machineName = string.Empty;

    [ObservableProperty]
    private string _address = string.Empty;

    [ObservableProperty]
    private int _port;

    /// <summary>UNC-Pfad des eigenen Netzwerkordners dieses Nutzers (falls vorhanden).</summary>
    [ObservableProperty]
    private string? _sharePath;

    /// <summary>Benutzername für den Netzwerkordner dieses Nutzers (COMPUTER\Konto), falls passwortgeschützt.</summary>
    [ObservableProperty]
    private string? _shareUser;

    /// <summary>Eigener Klingelton für Anrufe dieses Kontakts (Audiodatei; leer = Standard-Klingelton).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCustomRingtone))]
    private string? _ringtonePath;

    [JsonIgnore]
    public bool HasCustomRingtone => !string.IsNullOrEmpty(RingtonePath);

    /// <summary>Gemerkter SHA-256-Fingerabdruck des TLS-Zertifikats (Trust on first use).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SecurityCode))]
    private string _certFingerprint = string.Empty;

    /// <summary>Neuer, abweichender Fingerabdruck (z.B. nach Neuinstallation – oder ein Angriff).</summary>
    [ObservableProperty]
    [property: JsonIgnore]
    private string? _pendingFingerprint;

    [ObservableProperty]
    [property: JsonIgnore]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private bool _keyChanged;

    [JsonIgnore]
    public string SecurityCode => Services.CertificateService.ShortCode(CertFingerprint);

    [ObservableProperty]
    [property: JsonIgnore]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private bool _isOnline;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private DateTime _lastSeen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnread))]
    private int _unreadCount;

    [ObservableProperty]
    private string _lastMessagePreview = string.Empty;

    [ObservableProperty]
    private DateTime _lastActivity;

    [JsonIgnore]
    public ObservableCollection<ChatMessage> Messages { get; } = new();

    [JsonIgnore]
    public bool HasUnread => UnreadCount > 0;

    [JsonIgnore]
    public string Initial => string.IsNullOrWhiteSpace(DisplayName) ? "?" : DisplayName.Trim()[..1].ToUpperInvariant();

    public override string ToString() => DisplayName;

    [JsonIgnore]
    public string StatusText => KeyChanged
        ? Loc.T("⚠ Sicherheitsschlüssel hat sich geändert")
        : IsOnline
        ? Loc.T("online · {0}", MachineName)
        : LastSeen == default ? Loc.T("offline") : Loc.T("zuletzt online {0}", LastSeen.ToString("dd.MM. HH:mm"));
}
