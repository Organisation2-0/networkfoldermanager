namespace NetworkFolderManager.Services;

/// <summary>Inhalt der Zwischenablage, soweit für den Chat relevant.</summary>
/// <param name="Files">Kopierte Dateien (Pfade) – oder null</param>
/// <param name="ImagePng">Kopiertes Bild als PNG – oder null</param>
public record ClipboardContent(IReadOnlyList<string>? Files, byte[]? ImagePng);

/// <summary>
/// Plattformabhängige Dienste (Dialoge, Dateimanager, Benachrichtigungen, UI-Thread).
/// Der gemeinsame Code (Chat, Erkennung) greift nur über diese Schnittstelle darauf zu;
/// die Windows-App (WPF) und die macOS-App (Avalonia) liefern je eine Implementierung.
/// </summary>
public interface IPlatform
{
    /// <summary>Führt eine Aktion auf dem UI-Thread aus.</summary>
    void Post(Action action);

    /// <summary>Vom Windows-Installer gewählter Ordner für Chat-Dateien (sonst null).</summary>
    string? GetInstallerReceiveFolder();

    Task<string?> PickFolderAsync(string title, string? initialDirectory);

    /// <summary>Dateien zum Senden auswählen (leere Liste = abgebrochen).</summary>
    Task<IReadOnlyList<string>> PickFilesAsync(string title);

    Task<bool> ConfirmAsync(string message, string title);

    Task<ClipboardContent?> GetClipboardAsync();

    /// <summary>Datei mit der verknüpften Anwendung öffnen.</summary>
    void OpenFile(string path);

    /// <summary>Datei im Explorer/Finder markiert anzeigen.</summary>
    void RevealInFileManager(string path);

    /// <summary>Ordner im Explorer/Finder öffnen.</summary>
    void OpenFolder(string path);

    /// <summary>Benachrichtigung anzeigen; ein Klick darauf öffnet (falls angegeben) den Chat mit dem Kontakt.</summary>
    void Notify(string title, string text, string? chatPeerId = null);
}

/// <summary>Zugriff auf die aktuelle Plattform-Implementierung (wird beim Programmstart gesetzt).</summary>
public static class Platform
{
    public static IPlatform Current { get; set; } = null!;
}
