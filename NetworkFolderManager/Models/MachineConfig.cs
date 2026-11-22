using System.Text.Json.Serialization;

namespace NetworkFolderManager.Services;

/// <summary>
/// Maschinenweite Konfiguration, die der Installer (bzw. die App mit Admin-Rechten) anlegt.
/// Gespeichert unter %ProgramData%\NetworkFolderManager\machine.json (ohne Passwort).
/// </summary>
public class MachineConfig
{
    public string SharePath { get; set; } = string.Empty;
    public string ShareName { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    /// <summary>Lokales Windows-Konto, mit dem andere auf den Ordner zugreifen (null = ungeschützt).</summary>
    public string? AccessUser { get; set; }

    /// <summary>Ob die SMB3-Verschlüsselung für die Freigabe aktiv ist.</summary>
    public bool Encrypted { get; set; }

    [JsonIgnore]
    public bool IsProtected => !string.IsNullOrEmpty(AccessUser);

    [JsonIgnore]
    public string UncPath => $@"\\{Environment.MachineName}\{ShareName}";

    /// <summary>Benutzername, den andere beim Verbinden eingeben: COMPUTER\Konto.</summary>
    [JsonIgnore]
    public string? AccessUserQualified => IsProtected ? $@"{Environment.MachineName}\{AccessUser}" : null;
}
