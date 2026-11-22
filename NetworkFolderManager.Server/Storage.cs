using System.Runtime.InteropServices;
using System.Text.Json;

namespace NetworkFolderManager.Server;

/// <summary>Speicherorte des Servers.</summary>
public sealed class ServerPaths
{
    public string DataDir { get; }
    public string ConfigFile => Path.Combine(DataDir, "config.json");
    public string UsersFile => Path.Combine(DataDir, "users.json");
    public string FoldersFile => Path.Combine(DataDir, "folders.json");
    public string SessionsFile => Path.Combine(DataDir, "sessions.json");
    public string CaFile => Path.Combine(DataDir, "tls", "ca.pfx");
    public string LeafFile => Path.Combine(DataDir, "tls", "server.pfx");
    public string SetupCodeFile => Path.Combine(DataDir, "setup-code.txt");

    public ServerPaths(string? dataDir)
    {
        DataDir = Path.GetFullPath(string.IsNullOrWhiteSpace(dataDir) ? DefaultDataDir() : dataDir);
        Directory.CreateDirectory(DataDir);
        Restrict(DataDir, isDir: true);
    }

    /// <summary>
    /// Standard-Datenordner: NFM_SERVER_DATA, sonst
    /// Windows: %ProgramData%\NFM-Server · Linux (root): /var/lib/nfm-server · Linux (Benutzer): ~/.local/share/nfm-server ·
    /// macOS: ~/Library/Application Support/NFM-Server
    /// </summary>
    public static string DefaultDataDir()
    {
        var env = Environment.GetEnvironmentVariable("NFM_SERVER_DATA");
        if (!string.IsNullOrWhiteSpace(env))
            return env;
        if (OperatingSystem.IsWindows())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "NFM-Server");
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsMacOS())
            return Path.Combine(home, "Library", "Application Support", "NFM-Server");
        if (IsRoot())
            return "/var/lib/nfm-server";
        return Path.Combine(home, ".local", "share", "nfm-server");
    }

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEuid();

    private static bool IsRoot()
    {
        try
        {
            return !OperatingSystem.IsWindows() && GetEuid() == 0;
        }
        catch
        {
            return Environment.UserName == "root";
        }
    }

    /// <summary>Unter Linux/macOS nur für den Besitzer lesbar machen (0700/0600).</summary>
    public static void Restrict(string path, bool isDir)
    {
        if (OperatingSystem.IsWindows())
            return;
        try
        {
            File.SetUnixFileMode(path, isDir
                ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                : UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch
        {
            // z.B. Dateisystem ohne Unix-Rechte
        }
    }
}

public sealed class ServerConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = Environment.MachineName;
    public int Port { get; set; } = Models.VaultProtocol.DefaultPort;
    /// <summary>Ablage der verschlüsselten Ordner (leer = &lt;Datenordner&gt;/folders).</summary>
    public string StoragePath { get; set; } = string.Empty;
    /// <summary>Darf sich jeder selbst ein Profil anlegen?</summary>
    public bool AllowRegistration { get; set; } = true;
    /// <summary>Server-Erkennung per UDP-Broadcast.</summary>
    public bool Discovery { get; set; } = true;
    /// <summary>Install new versions from GitHub releases automatically.</summary>
    public bool AutoUpdate { get; set; } = true;
}

public sealed class UserRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Username { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public bool IsAdmin { get; set; }
    public bool MustChangePassword { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastLogin { get; set; }
    /// <summary>Preferred UI language of the clients ("de"/"en"), synced across devices.</summary>
    public string? Language { get; set; }
}

public sealed class UsersDb
{
    public List<UserRecord> Users { get; set; } = new();
}

public sealed class FolderRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public string OwnerId { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string CreatedBy { get; set; } = string.Empty;
    /// <summary>Vom Admin angelegt → Besitzer muss beim ersten Öffnen ein eigenes Passwort setzen (neuer Schlüssel).</summary>
    public bool MustChangePassword { get; set; }
    public string Salt { get; set; } = string.Empty;
    public int Iterations { get; set; }
    /// <summary>Ordnerschlüssel, verschlüsselt mit dem aus dem Ordnerpasswort abgeleiteten Schlüssel.</summary>
    public string WrappedKey { get; set; } = string.Empty;
    /// <summary>Wird bei jeder Passwortänderung erhöht (macht alte Zugänge ungültig).</summary>
    public int KeyVersion { get; set; } = 1;
}

public sealed class FoldersDb
{
    public List<FolderRecord> Folders { get; set; } = new();
}

/// <summary>
/// JSON-Datei mit Sperre und atomarem Schreiben. Wurde die Datei von außen geändert
/// (z.B. durch "nfm-server admin add" während der Server läuft), wird sie neu eingelesen.
/// </summary>
public sealed class JsonFile<T> where T : class, new()
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private readonly string _path;
    private readonly object _lock = new();
    private T _value;
    private DateTime _stamp;

    public JsonFile(string path)
    {
        _path = path;
        _value = Load();
    }

    public TResult Read<TResult>(Func<T, TResult> read)
    {
        lock (_lock)
        {
            ReloadIfChanged();
            return read(_value);
        }
    }

    public TResult Update<TResult>(Func<T, TResult> update)
    {
        lock (_lock)
        {
            ReloadIfChanged();
            var result = update(_value);
            Save();
            return result;
        }
    }

    public void Update(Action<T> update) => Update(v => { update(v); return true; });

    private void ReloadIfChanged()
    {
        var stamp = File.Exists(_path) ? File.GetLastWriteTimeUtc(_path) : DateTime.MinValue;
        if (stamp != _stamp)
            _value = Load();
    }

    private T Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                _stamp = File.GetLastWriteTimeUtc(_path);
                return JsonSerializer.Deserialize<T>(File.ReadAllText(_path), Options) ?? new T();
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"File {_path} is corrupted: {ex.Message}", ex);
        }
        _stamp = DateTime.MinValue;
        return new T();
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_value, Options));
        ServerPaths.Restrict(tmp, isDir: false);
        File.Move(tmp, _path, overwrite: true);
        _stamp = File.GetLastWriteTimeUtc(_path);
    }
}
