// Gemeinsame Datenmodelle für NFM-Server (nfm-server) und die Clients (Windows-App).
// Diese Datei wird auch vom Server-Projekt eingebunden – deshalb ohne WPF-Abhängigkeiten.
namespace NetworkFolderManager.Models;

public static class VaultProtocol
{
    /// <summary>UDP-Port für die Server-Erkennung (Query/Announce).</summary>
    public const int DiscoveryPort = 45823;

    /// <summary>Standard-HTTPS-Port des Servers (API, Web-App, WebDAV).</summary>
    public const int DefaultPort = 45830;

    public const string AppTag = "NFM-SRV";

    /// <summary>Header, den alle ändernden API-Aufrufe mitsenden müssen (CSRF-Schutz für die Web-App).</summary>
    public const string ClientHeader = "X-NFM";

    public const string FolderTokenHeader = "X-Folder-Token";
}

/// <summary>Gespeicherter NFM-Server in den Client-Einstellungen (Token liegt getrennt in der Anmeldeinformationsverwaltung).</summary>
public class SavedServer
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = VaultProtocol.DefaultPort;
    /// <summary>SHA-256 des Server-Stammzertifikats (wird bei jeder Verbindung geprüft).</summary>
    public string Fingerprint { get; set; } = string.Empty;
    /// <summary>Stammzertifikat (DER, Base64).</summary>
    public string CaCertificate { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;

    [System.Text.Json.Serialization.JsonIgnore]
    public string BaseUrl => $"https://{(Host.Contains(':') && !Host.StartsWith('[') ? $"[{Host}]" : Host)}:{Port}/";

    [System.Text.Json.Serialization.JsonIgnore]
    public string FingerprintShort => FormatFingerprint(Fingerprint);

    public static string FormatFingerprint(string fp) =>
        string.Join(" ", Enumerable.Range(0, Math.Min(8, fp.Length / 4)).Select(i => fp.Substring(i * 4, 4)));
}

/// <summary>Discovery-Paket (UDP, JSON).</summary>
public class ServerAnnouncement
{
    public string App { get; set; } = VaultProtocol.AppTag;
    public int V { get; set; } = 1;
    /// <summary>"query" (Client sucht Server) oder "announce" (Server meldet sich).</summary>
    public string Type { get; set; } = "announce";
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int Port { get; set; }
    /// <summary>SHA-256-Fingerabdruck des Server-Stammzertifikats (Hex, Großbuchstaben).</summary>
    public string Fp { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    /// <summary>Absender-IP (wird beim Empfang gesetzt).</summary>
    public string Address { get; set; } = string.Empty;
}

public class ServerInfoDto
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string Fingerprint { get; set; } = string.Empty;
    public bool AllowRegistration { get; set; }
    /// <summary>Noch kein Admin vorhanden – Registrierung mit Setup-Code macht zum Admin.</summary>
    public bool NeedsSetup { get; set; }
}

public class KnownServerDto
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public int Port { get; set; }
    public string Fingerprint { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
}

public class UserDto
{
    public string Id { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public bool IsAdmin { get; set; }
    public bool MustChangePassword { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? LastLogin { get; set; }
    public int FolderCount { get; set; }
    /// <summary>Bevorzugte Sprache des Profils ("de"/"en", null = nicht festgelegt) – gilt auf allen Geräten.</summary>
    public string? Language { get; set; }
}

public class LanguageRequest
{
    public string? Language { get; set; }
}

public class AuthResponse
{
    public string Token { get; set; } = string.Empty;
    public UserDto User { get; set; } = new();
}

public class LoginRequest
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string? Device { get; set; }
}

public class RegisterRequest
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public string? SetupCode { get; set; }
    public string? Device { get; set; }
}

public class ChangePasswordRequest
{
    public string OldPassword { get; set; } = string.Empty;
    public string NewPassword { get; set; } = string.Empty;
}

public class FolderDto
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string OwnerId { get; set; } = string.Empty;
    public string OwnerName { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public bool IsMine { get; set; }
    /// <summary>Vom Admin angelegt: Besitzer muss beim ersten Öffnen ein eigenes Passwort setzen.</summary>
    public bool MustChangePassword { get; set; }
    /// <summary>Nur im Admin-Bereich gefüllt (verschlüsselte Größe auf dem Datenträger).</summary>
    public long? StorageBytes { get; set; }
    public string? CreatedBy { get; set; }
}

public class CreateFolderRequest
{
    public string Name { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    /// <summary>Nur Admin: Benutzername des Besitzers.</summary>
    public string? Owner { get; set; }
}

public class PasswordRequest
{
    public string Password { get; set; } = string.Empty;
}

public class UnlockResponse
{
    public string FolderToken { get; set; } = string.Empty;
    public int ExpiresInSeconds { get; set; }
    public string FolderName { get; set; } = string.Empty;
    public string DavUrl { get; set; } = string.Empty;
}

public class ItemDto
{
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public bool IsDir { get; set; }
    public long Size { get; set; }
    public DateTime Modified { get; set; }
    public DateTime Created { get; set; }
    public string ContentType { get; set; } = string.Empty;
    /// <summary>"folder", "image", "video", "audio", "pdf", "text", "file".</summary>
    public string Kind { get; set; } = "file";
}

public class FolderStatsDto
{
    public int Files { get; set; }
    public int Folders { get; set; }
    public int Photos { get; set; }
    public int Videos { get; set; }
    public long Bytes { get; set; }
}

public class PathRequest
{
    public string Path { get; set; } = string.Empty;
}

public class MoveRequest
{
    public string From { get; set; } = string.Empty;
    public string To { get; set; } = string.Empty;
    public bool Overwrite { get; set; }
}

public class ErrorDto
{
    public string Error { get; set; } = string.Empty;
    public string? Code { get; set; }
}

public class AdminUserRequest
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public bool IsAdmin { get; set; }
}

public class SetAdminRequest
{
    public bool IsAdmin { get; set; }
}

public class SetOwnerRequest
{
    public string Username { get; set; } = string.Empty;
}

public class ServerSettingsDto
{
    public string Name { get; set; } = string.Empty;
    public bool AllowRegistration { get; set; }
    /// <summary>Automatische Server-Updates (null = unverändert lassen, für ältere Clients).</summary>
    public bool? AutoUpdate { get; set; }
}

/// <summary>Update-Status des Servers (Admin-Bereich).</summary>
public class ServerUpdateDto
{
    public string CurrentVersion { get; set; } = string.Empty;
    public string? LatestVersion { get; set; }
    public bool UpdateAvailable { get; set; }
    public bool AutoUpdate { get; set; }
    /// <summary>false = Server kann sich nicht selbst aktualisieren (z.B. Entwicklungs-Build).</summary>
    public bool Supported { get; set; }
    public bool Busy { get; set; }
    public DateTime? LastCheck { get; set; }
    /// <summary>"idle", "checking", "downloading", "installed", "restarting", "error", "noPermission", "unsupported".</summary>
    public string State { get; set; } = "idle";
    /// <summary>Englische Detailmeldung (z.B. Fehlertext).</summary>
    public string? Message { get; set; }
}

public static class VaultKinds
{
    private static readonly Dictionary<string, string> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".png"] = "image/png", [".gif"] = "image/gif",
        [".webp"] = "image/webp", [".bmp"] = "image/bmp", [".heic"] = "image/heic", [".heif"] = "image/heif",
        [".tif"] = "image/tiff", [".tiff"] = "image/tiff", [".svg"] = "image/svg+xml", [".ico"] = "image/x-icon",
        [".avif"] = "image/avif",
        [".mp4"] = "video/mp4", [".m4v"] = "video/mp4", [".mov"] = "video/quicktime", [".webm"] = "video/webm",
        [".mkv"] = "video/x-matroska", [".avi"] = "video/x-msvideo",
        [".mp3"] = "audio/mpeg", [".m4a"] = "audio/mp4", [".wav"] = "audio/wav", [".ogg"] = "audio/ogg",
        [".flac"] = "audio/flac", [".aac"] = "audio/aac",
        [".pdf"] = "application/pdf",
        [".txt"] = "text/plain", [".md"] = "text/plain", [".log"] = "text/plain", [".csv"] = "text/plain",
        [".json"] = "application/json", [".xml"] = "application/xml",
        [".html"] = "text/html", [".htm"] = "text/html",
        [".zip"] = "application/zip", [".7z"] = "application/x-7z-compressed", [".rar"] = "application/vnd.rar",
        [".doc"] = "application/msword", [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [".xls"] = "application/vnd.ms-excel", [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        [".ppt"] = "application/vnd.ms-powerpoint", [".pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation",
    };

    public static string ContentType(string name) =>
        Types.TryGetValue(System.IO.Path.GetExtension(name), out var t) ? t : "application/octet-stream";

    public static string Kind(string name, bool isDir)
    {
        if (isDir)
            return "folder";
        var ct = ContentType(name);
        if (ct.StartsWith("image/", StringComparison.Ordinal))
            return "image";
        if (ct.StartsWith("video/", StringComparison.Ordinal))
            return "video";
        if (ct.StartsWith("audio/", StringComparison.Ordinal))
            return "audio";
        if (ct == "application/pdf")
            return "pdf";
        if (ct.StartsWith("text/", StringComparison.Ordinal))
            return "text";
        return "file";
    }
}
