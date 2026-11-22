using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace NetworkFolderManager.Server;

/// <summary>
/// Verwaltet geöffnete (entsperrte) Ordner. Der Ordnerschlüssel liegt nur im Arbeitsspeicher, solange jemand
/// den Ordner geöffnet hat (Token, 30 Minuten gleitend) bzw. als Netzlaufwerk nutzt.
/// </summary>
public sealed class VaultManager
{
    public static readonly TimeSpan TokenLifetime = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan DavLifetime = TimeSpan.FromMinutes(15);

    private readonly Func<string, string> _folderDir;
    private readonly ConcurrentDictionary<string, VaultFolder> _open = new();
    private readonly ConcurrentDictionary<string, FolderAccess> _tokens = new();
    private readonly ConcurrentDictionary<string, DavAccess> _dav = new();

    private sealed class FolderAccess
    {
        public required string FolderId { get; init; }
        public required string UserId { get; init; }
        public required int KeyVersion { get; init; }
        public DateTime Expires { get; set; }
    }

    private sealed class DavAccess
    {
        public required string FolderId { get; init; }
        public required int KeyVersion { get; init; }
        public DateTime Expires { get; set; }
    }

    public VaultManager(Func<string, string> folderDir)
    {
        _folderDir = folderDir;
    }

    public string FolderDir(string folderId) => _folderDir(folderId);

    /// <summary>Entschlüsselt den Ordnerschlüssel mit dem Passwort (langsam, absichtlich). null = falsches Passwort.</summary>
    public static byte[]? UnwrapKey(FolderRecord f, string password)
    {
        try
        {
            var kek = FolderCrypto.DeriveKek(password, Convert.FromBase64String(f.Salt), f.Iterations);
            return FolderCrypto.Unwrap(kek, Convert.FromBase64String(f.WrappedKey));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>Neuen Ordner verschlüsselt anlegen; setzt Salt/WrappedKey im Datensatz.</summary>
    public void CreateFolder(FolderRecord f, string password)
    {
        var key = FolderCrypto.NewKey();
        SetPassword(f, key, password);
        VaultFolder.Create(f.Id, FolderDir(f.Id), key, f.KeyVersion);
    }

    /// <summary>Verpackt den Ordnerschlüssel neu mit einem anderen Passwort.</summary>
    public static void SetPassword(FolderRecord f, byte[] key, string password)
    {
        var salt = FolderCrypto.NewSalt();
        f.Salt = Convert.ToBase64String(salt);
        f.Iterations = FolderCrypto.KdfIterations;
        f.WrappedKey = Convert.ToBase64String(FolderCrypto.Wrap(FolderCrypto.DeriveKek(password, salt, f.Iterations), key));
    }

    /// <summary>
    /// Erstes Öffnen eines vom Admin angelegten Ordners: komplett neuer Schlüssel (der Admin kannte das
    /// Startpasswort – mit dem neuen Schlüssel kann er nichts mehr entschlüsseln). Bisheriger Inhalt wird verworfen
    /// (er kann nicht existieren, weil der Ordner vorher nicht geöffnet werden konnte).
    /// </summary>
    public void RekeyFolder(FolderRecord f, string newPassword)
    {
        Close(f.Id);
        var dir = FolderDir(f.Id);
        if (Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
        f.KeyVersion++;
        CreateFolder(f, newPassword);
    }

    public VaultFolder Open(FolderRecord f, byte[] key) =>
        _open.AddOrUpdate(f.Id,
            _ => VaultFolder.Open(f.Id, FolderDir(f.Id), key, f.KeyVersion),
            (_, existing) => existing.KeyVersion == f.KeyVersion ? existing : VaultFolder.Open(f.Id, FolderDir(f.Id), key, f.KeyVersion));

    public string IssueToken(FolderRecord f, string userId)
    {
        var token = Tokens.New();
        _tokens[Tokens.Hash(token)] = new FolderAccess
        {
            FolderId = f.Id,
            UserId = userId,
            KeyVersion = f.KeyVersion,
            Expires = DateTime.UtcNow + TokenLifetime
        };
        return token;
    }

    /// <summary>Gibt den geöffneten Ordner zu einem Token zurück (verlängert dessen Gültigkeit).</summary>
    public VaultFolder? Resolve(string? token, FolderRecord f, string userId)
    {
        if (string.IsNullOrEmpty(token) || !_tokens.TryGetValue(Tokens.Hash(token), out var access))
            return null;
        if (access.FolderId != f.Id || access.UserId != userId || access.KeyVersion != f.KeyVersion || access.Expires < DateTime.UtcNow)
            return null;
        if (!_open.TryGetValue(f.Id, out var folder) || folder.KeyVersion != f.KeyVersion)
            return null;
        access.Expires = DateTime.UtcNow + TokenLifetime;
        return folder;
    }

    public void RevokeToken(string? token)
    {
        if (!string.IsNullOrEmpty(token))
            _tokens.TryRemove(Tokens.Hash(token), out _);
    }

    /// <summary>
    /// WebDAV sendet bei jeder Anfrage Benutzername/Passwort. Damit nicht jedes Mal der (absichtlich langsame)
    /// Schlüssel abgeleitet werden muss, wird eine erfolgreiche Anmeldung kurz zwischengespeichert.
    /// </summary>
    public VaultFolder? DavFromCache(FolderRecord f, string password)
    {
        var key = DavCacheKey(f, password);
        if (_dav.TryGetValue(key, out var access) && access.KeyVersion == f.KeyVersion && access.Expires > DateTime.UtcNow &&
            _open.TryGetValue(f.Id, out var folder) && folder.KeyVersion == f.KeyVersion)
        {
            access.Expires = DateTime.UtcNow + DavLifetime;
            return folder;
        }
        return null;
    }

    public void RememberDav(FolderRecord f, string password)
    {
        _dav[DavCacheKey(f, password)] = new DavAccess
        {
            FolderId = f.Id,
            KeyVersion = f.KeyVersion,
            Expires = DateTime.UtcNow + DavLifetime
        };
    }

    private static readonly byte[] DavCacheSecret = RandomNumberGenerator.GetBytes(32);

    private static string DavCacheKey(FolderRecord f, string password) =>
        Convert.ToHexString(HMACSHA256.HashData(DavCacheSecret, System.Text.Encoding.UTF8.GetBytes($"{f.Id}\n{f.KeyVersion}\n{password}")));

    /// <summary>Ordner schließen: alle Token/Netzlaufwerk-Zugänge ungültig, Schlüssel aus dem Speicher.</summary>
    public void Close(string folderId)
    {
        foreach (var (k, v) in _tokens)
            if (v.FolderId == folderId)
                _tokens.TryRemove(k, out _);
        foreach (var (k, v) in _dav)
            if (v.FolderId == folderId)
                _dav.TryRemove(k, out _);
        _open.TryRemove(folderId, out _);
    }

    public bool IsOpen(string folderId) => _open.ContainsKey(folderId);

    /// <summary>Abgelaufene Zugänge entfernen und Ordner ohne Zugang schließen.</summary>
    public void Cleanup()
    {
        var now = DateTime.UtcNow;
        foreach (var (k, v) in _tokens)
            if (v.Expires < now)
                _tokens.TryRemove(k, out _);
        foreach (var (k, v) in _dav)
            if (v.Expires < now)
                _dav.TryRemove(k, out _);

        var inUse = _tokens.Values.Select(t => t.FolderId).Concat(_dav.Values.Select(d => d.FolderId)).ToHashSet();
        foreach (var id in _open.Keys)
            if (!inUse.Contains(id))
                _open.TryRemove(id, out _);
    }
}
