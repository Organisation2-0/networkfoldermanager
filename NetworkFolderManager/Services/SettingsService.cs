using System.IO;
using System.Text.Json;

namespace NetworkFolderManager.Services;

/// <summary>
/// Persistente Benutzer-Einstellungen (ohne Passwörter).
/// Gespeichert unter %APPDATA%\NetworkFolderManager\settings.json.
/// </summary>
public class AppSettings
{
    /// <summary>Pfad des eigenen, überwachten Ordners.</summary>
    public string OwnFolderPath { get; set; } = string.Empty;

    /// <summary>Überwachung beim Programmstart automatisch starten.</summary>
    public bool MonitorOnStartup { get; set; }

    /// <summary>Beim Schließen in den Infobereich minimieren statt zu beenden.</summary>
    public bool MinimizeToTray { get; set; } = true;

    /// <summary>Manuell hinzugefügte UNC-Pfade (\\server\freigabe).</summary>
    public List<string> ManualFolders { get; set; } = new();

    /// <summary>Zuletzt verwendete Benutzernamen pro Server.</summary>
    public Dictionary<string, string> LastUsernames { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Eindeutige ID dieser Installation/dieses Benutzers für den Chat.</summary>
    public string PeerId { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Anzeigename im Chat.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Erstellungszeitpunkt des zuletzt übernommenen Netzwerkordners (aus machine.json).</summary>
    public DateTime? AppliedShareCreatedAt { get; set; }

    /// <summary>Ordner für empfangene Chat-Bilder/-Dateien (leer = Dokumente\NetworkFolderManager).</summary>
    public string ChatReceiveFolder { get; set; } = string.Empty;

    /// <summary>Zuletzt übernommene Ordner-Auswahl aus dem Installer.</summary>
    public string? InstallerReceiveFolderApplied { get; set; }

    /// <summary>Benachrichtigungen bei neuen Chat-Nachrichten.</summary>
    public bool ChatNotifications { get; set; } = true;

    /// <summary>Eigener Klingelton für eingehende Anrufe (Audiodatei; leer = eingebauter Klingelton).</summary>
    public string RingtonePath { get; set; } = string.Empty;

    /// <summary>Lautstärke des Klingeltons (0–100).</summary>
    public int RingtoneVolume { get; set; } = 80;

    /// <summary>Bekannte NFM-Server (Anmelde-Token liegen in der Anmeldeinformationsverwaltung, nicht hier).</summary>
    public List<Models.SavedServer> Servers { get; set; } = new();
}

public static class SettingsService
{
    /// <summary>
    /// Optionales Profil (Umgebungsvariable NFM_PROFILE) – erlaubt zum Testen mehrere Instanzen pro Benutzer.
    /// </summary>
    public static readonly string Profile = SanitizeProfile(Environment.GetEnvironmentVariable("NFM_PROFILE"));

    /// <summary>Windows: %APPDATA%\NetworkFolderManager, macOS: ~/Library/Application Support/NetworkFolderManager.</summary>
    public static readonly string SettingsDir = Path.Combine(
        OperatingSystem.IsWindows()
            ? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support"),
        "NetworkFolderManager" + (Profile.Length > 0 ? "-" + Profile : ""));

    private static string SanitizeProfile(string? p) =>
        new((p ?? string.Empty).Where(char.IsLetterOrDigit).Take(20).ToArray());

    private static readonly string SettingsFile = Path.Combine(SettingsDir, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        AppSettings? settings = null;
        try
        {
            if (File.Exists(SettingsFile))
                settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsFile), JsonOptions);
        }
        catch
        {
            // Beschädigte Datei → Standardwerte
        }

        settings ??= new AppSettings();
        settings.LastUsernames = new Dictionary<string, string>(settings.LastUsernames, StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(settings.PeerId))
            settings.PeerId = Guid.NewGuid().ToString("N");
        if (string.IsNullOrWhiteSpace(settings.DisplayName))
            settings.DisplayName = Environment.UserName;

        // ID und Name sofort sichern, damit sie stabil bleiben
        Save(settings);
        return settings;
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(SettingsDir);
            var tmp = SettingsFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(settings, JsonOptions));
            File.Move(tmp, SettingsFile, overwrite: true);
        }
        catch
        {
            // Einstellungen sind nicht kritisch
        }
    }
}
