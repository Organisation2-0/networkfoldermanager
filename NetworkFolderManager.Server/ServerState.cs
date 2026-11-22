using NetworkFolderManager.Models;

namespace NetworkFolderManager.Server;

/// <summary>
/// Zentrale Server-Logik: Konten, Ordner, Sitzungen, Verschlüsselung.
/// Wird von API, WebDAV und Kommandozeile gemeinsam genutzt.
/// </summary>
public sealed class ServerState
{
    public static string Version =>
        typeof(ServerState).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "1.0.0";

    public ServerPaths Paths { get; }
    public JsonFile<ServerConfig> Config { get; }
    public JsonFile<UsersDb> Users { get; }
    public JsonFile<FoldersDb> Folders { get; }
    public SessionStore Sessions { get; }
    public VaultManager Vaults { get; }
    public AttemptLimiter Limiter { get; } = new();
    public CertificateManager Certificates { get; private set; } = null!;
    public DiscoveryService? Discovery { get; set; }

    private readonly int? _portOverride;

    public ServerState(ServerPaths paths, int? portOverride = null)
    {
        Paths = paths;
        _portOverride = portOverride;
        Config = new JsonFile<ServerConfig>(paths.ConfigFile);
        // Konfiguration beim ersten Start mit Standardwerten anlegen
        Config.Update(_ => { });
        Users = new JsonFile<UsersDb>(paths.UsersFile);
        Folders = new JsonFile<FoldersDb>(paths.FoldersFile);
        Sessions = new SessionStore(paths.SessionsFile);
        Vaults = new VaultManager(id => Path.Combine(StorageRoot, id));
    }

    public void InitCertificates(ILogger? log) => Certificates = new CertificateManager(Paths, log);

    public string ServerId => Config.Read(c => c.Id);
    public string ServerName => Config.Read(c => c.Name);
    public int Port => _portOverride ?? Config.Read(c => c.Port);
    public bool DiscoveryEnabled => Config.Read(c => c.Discovery);
    public bool AllowRegistration => Config.Read(c => c.AllowRegistration);

    public string StorageRoot
    {
        get
        {
            var p = Config.Read(c => c.StoragePath);
            var root = string.IsNullOrWhiteSpace(p) ? Path.Combine(Paths.DataDir, "folders") : p;
            Directory.CreateDirectory(root);
            return root;
        }
    }

    #region Setup-Code (erster Admin)

    public bool HasAdmin => Users.Read(db => db.Users.Any(u => u.IsAdmin));

    /// <summary>Solange es keinen Admin gibt: Einmal-Code, mit dem sich das erste Admin-Konto registrieren kann.</summary>
    public string? EnsureSetupCode()
    {
        if (HasAdmin)
        {
            if (File.Exists(Paths.SetupCodeFile))
                File.Delete(Paths.SetupCodeFile);
            return null;
        }
        if (File.Exists(Paths.SetupCodeFile))
        {
            var existing = File.ReadAllText(Paths.SetupCodeFile).Trim();
            if (existing.Length > 0)
                return existing;
        }
        var code = Tokens.NewSetupCode();
        File.WriteAllText(Paths.SetupCodeFile, code);
        ServerPaths.Restrict(Paths.SetupCodeFile, isDir: false);
        return code;
    }

    private bool CheckSetupCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code) || HasAdmin || !File.Exists(Paths.SetupCodeFile))
            return false;
        var expected = File.ReadAllText(Paths.SetupCodeFile).Trim();
        return string.Equals(Normalize(expected), Normalize(code), StringComparison.Ordinal);

        static string Normalize(string s) => new(s.ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());
    }

    #endregion

    #region Konten

    public UserRecord? FindUser(string? idOrName) =>
        string.IsNullOrEmpty(idOrName)
            ? null
            : Users.Read(db => db.Users.FirstOrDefault(u =>
                u.Id == idOrName || string.Equals(u.Username, idOrName, StringComparison.OrdinalIgnoreCase)));

    public UserDto ToDto(UserRecord u, bool withFolderCount = false) => new()
    {
        Id = u.Id,
        Username = u.Username,
        DisplayName = string.IsNullOrWhiteSpace(u.DisplayName) ? u.Username : u.DisplayName,
        IsAdmin = u.IsAdmin,
        MustChangePassword = u.MustChangePassword,
        CreatedAt = u.CreatedAt,
        LastLogin = u.LastLogin,
        FolderCount = withFolderCount ? Folders.Read(db => db.Folders.Count(f => f.OwnerId == u.Id)) : 0
    };

    /// <summary>Konto anlegen. Wirft <see cref="VaultException"/> bei ungültigen Daten.</summary>
    public UserRecord CreateUser(string username, string password, string? displayName, bool isAdmin, bool mustChange)
    {
        username = (username ?? string.Empty).Trim();
        if (Names.ValidateUsername(username) is { } nameError)
            throw new VaultException(400, "badUsername", nameError);
        if (Passwords.Validate(password) is { } pwError)
            throw new VaultException(400, "weakPassword", pwError);

        var user = new UserRecord
        {
            Username = username,
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? username : displayName.Trim()[..Math.Min(displayName.Trim().Length, 64)],
            PasswordHash = Passwords.Hash(password),
            IsAdmin = isAdmin,
            MustChangePassword = mustChange
        };

        return Users.Update(db =>
        {
            if (db.Users.Any(u => string.Equals(u.Username, username, StringComparison.OrdinalIgnoreCase)))
                throw new VaultException(409, "userExists", "Dieser Benutzername ist bereits vergeben.");
            db.Users.Add(user);
            return user;
        });
    }

    /// <summary>Selbst registrieren (oder mit Setup-Code als erster Admin).</summary>
    public UserRecord Register(RegisterRequest req)
    {
        var asAdmin = CheckSetupCode(req.SetupCode);
        if (!asAdmin && !string.IsNullOrWhiteSpace(req.SetupCode))
            throw new VaultException(403, "badSetupCode", "Der Setup-Code ist falsch.");
        if (!asAdmin && !AllowRegistration)
            throw new VaultException(403, "registrationClosed", "Auf diesem Server können keine neuen Profile angelegt werden. Bitte den Admin fragen.");

        var user = CreateUser(req.Username, req.Password, req.DisplayName, asAdmin, mustChange: false);
        if (asAdmin)
            EnsureSetupCode(); // löscht den Code
        return user;
    }

    public void SetUserPassword(string userId, string newPassword, bool mustChange)
    {
        if (Passwords.Validate(newPassword) is { } error)
            throw new VaultException(400, "weakPassword", error);
        var hash = Passwords.Hash(newPassword);
        Users.Update(db =>
        {
            var u = db.Users.FirstOrDefault(x => x.Id == userId) ?? throw new VaultException(404, "notFound", "Benutzer nicht gefunden.");
            u.PasswordHash = hash;
            u.MustChangePassword = mustChange;
        });
    }

    public void SetAdmin(string userId, bool isAdmin)
    {
        Users.Update(db =>
        {
            var u = db.Users.FirstOrDefault(x => x.Id == userId) ?? throw new VaultException(404, "notFound", "Benutzer nicht gefunden.");
            if (!isAdmin && u.IsAdmin && db.Users.Count(x => x.IsAdmin) == 1)
                throw new VaultException(409, "lastAdmin", "Der letzte Admin kann nicht entfernt werden.");
            u.IsAdmin = isAdmin;
        });
    }

    /// <summary>Konto löschen. Seine Ordner bleiben erhalten (ohne Besitzer) und können neu zugewiesen werden.</summary>
    public void DeleteUser(string userId)
    {
        Users.Update(db =>
        {
            var u = db.Users.FirstOrDefault(x => x.Id == userId) ?? throw new VaultException(404, "notFound", "Benutzer nicht gefunden.");
            if (u.IsAdmin && db.Users.Count(x => x.IsAdmin) == 1)
                throw new VaultException(409, "lastAdmin", "Der letzte Admin kann nicht gelöscht werden.");
            db.Users.Remove(u);
        });
        Sessions.RevokeUser(userId);
    }

    public void TouchLogin(string userId) =>
        Users.Update(db =>
        {
            var u = db.Users.FirstOrDefault(x => x.Id == userId);
            if (u != null)
                u.LastLogin = DateTime.UtcNow;
        });

    #endregion

    #region Ordner

    public FolderRecord? FindFolder(string? idOrName) =>
        string.IsNullOrEmpty(idOrName)
            ? null
            : Folders.Read(db => db.Folders.FirstOrDefault(f =>
                f.Id == idOrName || string.Equals(f.Name, idOrName, StringComparison.OrdinalIgnoreCase)));

    public FolderDto ToDto(FolderRecord f, string? viewerId, bool admin = false)
    {
        var owner = FindUser(f.OwnerId);
        return new FolderDto
        {
            Id = f.Id,
            Name = f.Name,
            OwnerId = f.OwnerId,
            OwnerName = owner == null ? "—" : (string.IsNullOrWhiteSpace(owner.DisplayName) ? owner.Username : owner.DisplayName),
            CreatedAt = f.CreatedAt,
            IsMine = f.OwnerId == viewerId,
            MustChangePassword = f.MustChangePassword,
            StorageBytes = admin ? VaultFolder.DiskUsage(Vaults.FolderDir(f.Id)) : null,
            CreatedBy = admin ? FindUser(f.CreatedBy)?.Username ?? f.CreatedBy : null
        };
    }

    /// <summary>Neuen verschlüsselten Ordner anlegen.</summary>
    /// <param name="byAdminForOther">Vom Admin für jemand anderen: Besitzer muss beim ersten Öffnen das Passwort ändern.</param>
    public FolderRecord CreateFolder(string name, string password, UserRecord owner, UserRecord creator, bool byAdminForOther)
    {
        name = (name ?? string.Empty).Trim();
        if (Names.ValidateFolderName(name) is { } nameError)
            throw new VaultException(400, "badName", nameError);
        if (Passwords.Validate(password) is { } pwError)
            throw new VaultException(400, "weakPassword", pwError);

        var record = new FolderRecord
        {
            Name = name,
            OwnerId = owner.Id,
            CreatedBy = creator.Id,
            MustChangePassword = byAdminForOther
        };

        // Name reservieren, dann (langsam) verschlüsseln und anlegen
        Folders.Update(db =>
        {
            if (db.Folders.Any(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)))
                throw new VaultException(409, "folderExists", "Ein Ordner mit diesem Namen existiert auf dem Server bereits.");
            db.Folders.Add(record);
        });

        try
        {
            Vaults.CreateFolder(record, password);
            Folders.Update(db =>
            {
                var f = db.Folders.First(x => x.Id == record.Id);
                f.Salt = record.Salt;
                f.Iterations = record.Iterations;
                f.WrappedKey = record.WrappedKey;
                f.KeyVersion = record.KeyVersion;
            });
        }
        catch
        {
            Folders.Update(db => db.Folders.RemoveAll(f => f.Id == record.Id));
            throw;
        }
        return record;
    }

    /// <summary>Ordner endgültig löschen (inkl. aller verschlüsselten Dateien).</summary>
    public void DeleteFolder(string folderId)
    {
        var removed = Folders.Update(db => db.Folders.RemoveAll(f => f.Id == folderId));
        if (removed == 0)
            throw new VaultException(404, "notFound", "Ordner nicht gefunden.");
        Vaults.Close(folderId);
        var dir = Vaults.FolderDir(folderId);
        try
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch
        {
            // Reste werden beim nächsten Start ggf. erneut versucht – sie sind verschlüsselt und ohne Schlüssel wertlos
        }
    }

    public void SetFolderOwner(string folderId, string ownerId) =>
        Folders.Update(db =>
        {
            var f = db.Folders.FirstOrDefault(x => x.Id == folderId) ?? throw new VaultException(404, "notFound", "Ordner nicht gefunden.");
            f.OwnerId = ownerId;
        });

    /// <summary>
    /// Ordner mit Passwort entsperren. Gibt den geöffneten Ordner zurück.
    /// Wirft 403 bei falschem Passwort und 409 ("mustChangePassword"), wenn erst ein eigenes Passwort gesetzt werden muss.
    /// </summary>
    public VaultFolder Unlock(FolderRecord f, string password, string limiterKey)
    {
        if (Limiter.IsBlocked(limiterKey, out var wait))
            throw new VaultException(429, "tooManyAttempts", $"Zu viele Fehlversuche. Bitte in {Math.Ceiling(wait.TotalMinutes)} Minute(n) erneut versuchen.");

        var key = VaultManager.UnwrapKey(f, password);
        if (key == null)
        {
            Limiter.Fail(limiterKey);
            throw new VaultException(403, "wrongPassword", "Falsches Ordnerpasswort.");
        }
        Limiter.Success(limiterKey);

        if (f.MustChangePassword)
            throw new VaultException(409, "mustChangePassword", "Dieser Ordner wurde für dich angelegt. Bitte lege beim ersten Öffnen ein eigenes Passwort fest.");
        return Vaults.Open(f, key);
    }

    /// <summary>
    /// Ordnerpasswort ändern (nur Besitzer). Beim ersten Öffnen eines vom Admin angelegten Ordners wird
    /// zusätzlich ein neuer Schlüssel erzeugt, damit der Admin den Inhalt nie entschlüsseln kann.
    /// </summary>
    public FolderRecord ChangeFolderPassword(FolderRecord f, string oldPassword, string newPassword, string limiterKey)
    {
        if (Passwords.Validate(newPassword) is { } pwError)
            throw new VaultException(400, "weakPassword", pwError);
        if (oldPassword == newPassword)
            throw new VaultException(400, "samePassword", "Das neue Passwort muss sich vom bisherigen unterscheiden.");
        if (Limiter.IsBlocked(limiterKey, out var wait))
            throw new VaultException(429, "tooManyAttempts", $"Zu viele Fehlversuche. Bitte in {Math.Ceiling(wait.TotalMinutes)} Minute(n) erneut versuchen.");

        var key = VaultManager.UnwrapKey(f, oldPassword);
        if (key == null)
        {
            Limiter.Fail(limiterKey);
            throw new VaultException(403, "wrongPassword", "Das bisherige Ordnerpasswort ist falsch.");
        }
        Limiter.Success(limiterKey);

        var updated = Folders.Read(db => db.Folders.First(x => x.Id == f.Id));
        var copy = new FolderRecord
        {
            Id = updated.Id, Name = updated.Name, OwnerId = updated.OwnerId, CreatedAt = updated.CreatedAt,
            CreatedBy = updated.CreatedBy, KeyVersion = updated.KeyVersion
        };

        if (updated.MustChangePassword)
        {
            Vaults.RekeyFolder(copy, newPassword);
        }
        else
        {
            Vaults.Close(f.Id);
            VaultManager.SetPassword(copy, key, newPassword);
            copy.KeyVersion++;
        }

        Folders.Update(db =>
        {
            var r = db.Folders.First(x => x.Id == f.Id);
            r.Salt = copy.Salt;
            r.Iterations = copy.Iterations;
            r.WrappedKey = copy.WrappedKey;
            r.KeyVersion = copy.KeyVersion;
            r.MustChangePassword = false;
        });
        return FindFolder(f.Id)!;
    }

    #endregion

    /// <summary>Regelmäßige Aufräumarbeiten (abgelaufene Zugänge, Sitzungen, Zertifikat).</summary>
    public void Maintenance()
    {
        Vaults.Cleanup();
        Limiter.Cleanup();
        Sessions.Cleanup();
        Certificates.Refresh();
    }
}
