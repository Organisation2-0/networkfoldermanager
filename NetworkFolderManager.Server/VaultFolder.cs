using System.Text;
using System.Text.Json;

namespace NetworkFolderManager.Server;

public sealed class VaultException : Exception
{
    public int Status { get; }
    public string Code { get; }

    public VaultException(int status, string code, string message) : base(message)
    {
        Status = status;
        Code = code;
    }

    public static VaultException NotFound(string path) => new(404, "notFound", $"„{path}“ wurde nicht gefunden.");
    public static VaultException Exists(string path) => new(409, "exists", $"„{path}“ existiert bereits.");
    public static VaultException NoParent(string path) => new(409, "noParent", $"Übergeordneter Ordner von „{path}“ fehlt.");
    public static VaultException BadPath() => new(400, "badPath", "Ungültiger Pfad oder Name.");
}

public sealed class IndexEntry
{
    public string Path { get; set; } = "/";
    public bool Dir { get; set; }
    public string? Blob { get; set; }
    public long Size { get; set; }
    public DateTime Modified { get; set; } = DateTime.UtcNow;
    public DateTime Created { get; set; } = DateTime.UtcNow;

    public string Name => Path == "/" ? "" : Path[(Path.LastIndexOf('/') + 1)..];

    public IndexEntry Clone() => (IndexEntry)MemberwiseClone();
}

/// <summary>
/// Ein geöffneter (entschlüsselter) Server-Ordner: virtuelles Dateisystem über verschlüsselten Blobs.
/// Das Inhaltsverzeichnis (Pfade, Größen, Zeiten) liegt ebenfalls verschlüsselt in "index.enc",
/// die Dateien unter "blobs/xx/&lt;zufällige ID&gt;" – Dateinamen sind auf dem Datenträger also nicht sichtbar.
/// </summary>
public sealed class VaultFolder
{
    private static readonly byte[] IndexAad = "NFM-INDEX-V1"u8.ToArray();
    private static readonly JsonSerializerOptions JsonOptions = new();

    private readonly string _dir;
    private readonly byte[] _key;
    private readonly object _sync = new();
    private readonly Dictionary<string, IndexEntry> _entries = new(StringComparer.OrdinalIgnoreCase);

    public string FolderId { get; }
    public int KeyVersion { get; }

    private VaultFolder(string folderId, string dir, byte[] key, int keyVersion)
    {
        FolderId = folderId;
        _dir = dir;
        _key = key;
        KeyVersion = keyVersion;
    }

    private string IndexFile => Path.Combine(_dir, "index.enc");

    private string BlobPath(string blob) => Path.Combine(_dir, "blobs", blob[..2], blob);

    /// <summary>Legt einen neuen, leeren Ordner an.</summary>
    public static VaultFolder Create(string folderId, string dir, byte[] key, int keyVersion)
    {
        Directory.CreateDirectory(Path.Combine(dir, "blobs"));
        ServerPaths.Restrict(dir, isDir: true);
        var folder = new VaultFolder(folderId, dir, key, keyVersion);
        folder._entries["/"] = new IndexEntry { Path = "/", Dir = true };
        folder.SaveIndex();
        return folder;
    }

    public static VaultFolder Open(string folderId, string dir, byte[] key, int keyVersion)
    {
        var folder = new VaultFolder(folderId, dir, key, keyVersion);
        if (!File.Exists(folder.IndexFile))
        {
            Directory.CreateDirectory(Path.Combine(dir, "blobs"));
            folder._entries["/"] = new IndexEntry { Path = "/", Dir = true };
            folder.SaveIndex();
            return folder;
        }

        var plain = FolderCrypto.Open(key, File.ReadAllBytes(folder.IndexFile), IndexAad)
                    ?? throw new InvalidDataException("Inhaltsverzeichnis des Ordners konnte nicht entschlüsselt werden.");
        var entries = JsonSerializer.Deserialize<List<IndexEntry>>(plain, JsonOptions) ?? new();
        foreach (var e in entries)
            folder._entries[e.Path] = e;
        folder._entries.TryAdd("/", new IndexEntry { Path = "/", Dir = true });
        return folder;
    }

    private void SaveIndex()
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(_entries.Values.ToList(), JsonOptions);
        var data = FolderCrypto.Seal(_key, json, IndexAad);
        var tmp = IndexFile + ".tmp";
        File.WriteAllBytes(tmp, data);
        File.Move(tmp, IndexFile, overwrite: true);
    }

    #region Pfade

    /// <summary>Normalisiert einen Pfad zu "/a/b" ("/" = Wurzel). Wirft bei "..", leeren oder ungültigen Namen.</summary>
    public static string Normalize(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return "/";
        var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var sb = new StringBuilder();
        foreach (var part in parts)
        {
            if (part == ".")
                continue;
            if (!Names.IsValidItemName(part))
                throw VaultException.BadPath();
            sb.Append('/').Append(part);
        }
        return sb.Length == 0 ? "/" : sb.ToString();
    }

    public static string ParentOf(string path)
    {
        if (path == "/")
            return "/";
        var i = path.LastIndexOf('/');
        return i <= 0 ? "/" : path[..i];
    }

    private static string Combine(string dir, string name) => dir == "/" ? "/" + name : dir + "/" + name;

    private static bool IsUnder(string path, string dir) =>
        dir == "/" || path.Equals(dir, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(dir + "/", StringComparison.OrdinalIgnoreCase);

    #endregion

    #region Lesen

    public IndexEntry? Get(string path)
    {
        lock (_sync)
            return _entries.TryGetValue(path, out var e) ? e.Clone() : null;
    }

    public List<IndexEntry> List(string dir)
    {
        lock (_sync)
        {
            if (!_entries.TryGetValue(dir, out var d) || !d.Dir)
                throw VaultException.NotFound(dir);
            return _entries.Values
                .Where(e => e.Path != "/" && ParentOf(e.Path).Equals(dir, StringComparison.OrdinalIgnoreCase))
                .Select(e => e.Clone())
                .ToList();
        }
    }

    public List<IndexEntry> AllFiles()
    {
        lock (_sync)
            return _entries.Values.Where(e => !e.Dir).Select(e => e.Clone()).ToList();
    }

    public (int Files, int Dirs, long Bytes) Stats()
    {
        lock (_sync)
            return (_entries.Values.Count(e => !e.Dir), _entries.Values.Count(e => e.Dir) - 1, _entries.Values.Where(e => !e.Dir).Sum(e => e.Size));
    }

    public Stream OpenRead(IndexEntry entry)
    {
        if (entry.Dir || entry.Blob == null)
            throw VaultException.NotFound(entry.Path);
        return new BlobReadStream(BlobPath(entry.Blob), _key, entry.Size);
    }

    #endregion

    #region Schreiben

    /// <summary>
    /// Speichert eine Datei. <paramref name="autoRename"/>: existiert sie schon, wird "Name (1).ext" verwendet;
    /// sonst wird sie bei <paramref name="overwrite"/> ersetzt bzw. ein Konflikt gemeldet.
    /// </summary>
    public async Task<(IndexEntry Entry, bool Created)> PutAsync(string path, Stream content, bool overwrite, bool autoRename,
        DateTime? modified, CancellationToken ct)
    {
        if (path == "/")
            throw VaultException.BadPath();
        CheckParent(path);

        var blob = Guid.NewGuid().ToString("N");
        var blobPath = BlobPath(blob);
        long size;
        try
        {
            size = await BlobFormat.WriteAsync(content, blobPath, _key, ct);
        }
        catch
        {
            TryDelete(blobPath);
            throw;
        }

        string? oldBlob = null;
        IndexEntry entry;
        bool created;
        lock (_sync)
        {
            CheckParent(path);
            if (_entries.TryGetValue(path, out var existing))
            {
                if (autoRename)
                {
                    path = FreeName(path);
                    existing = null;
                }
                else if (existing.Dir || !overwrite)
                {
                    TryDelete(blobPath);
                    throw VaultException.Exists(path);
                }
            }

            var now = DateTime.UtcNow;
            created = existing == null;
            oldBlob = existing?.Blob;
            entry = new IndexEntry
            {
                Path = existing?.Path ?? path,
                Blob = blob,
                Size = size,
                Created = existing?.Created ?? now,
                Modified = modified ?? now
            };
            _entries[entry.Path] = entry;
            SaveIndex();
        }

        if (oldBlob != null)
            TryDelete(BlobPath(oldBlob));
        return (entry.Clone(), created);
    }

    private string FreeName(string path)
    {
        var dir = ParentOf(path);
        var name = path[(path.LastIndexOf('/') + 1)..];
        var ext = Path.GetExtension(name);
        var stem = name[..^ext.Length];
        for (var i = 1; ; i++)
        {
            var candidate = Combine(dir, $"{stem} ({i}){ext}");
            if (!_entries.ContainsKey(candidate))
                return candidate;
        }
    }

    private void CheckParent(string path)
    {
        lock (_sync)
        {
            if (!_entries.TryGetValue(ParentOf(path), out var parent) || !parent.Dir)
                throw VaultException.NoParent(path);
        }
    }

    public IndexEntry CreateDirectory(string path)
    {
        if (path == "/")
            throw VaultException.Exists(path);
        lock (_sync)
        {
            CheckParent(path);
            if (_entries.ContainsKey(path))
                throw VaultException.Exists(path);
            var entry = new IndexEntry { Path = path, Dir = true };
            _entries[path] = entry;
            SaveIndex();
            return entry.Clone();
        }
    }

    /// <summary>Setzt die Änderungszeit (z.B. per WebDAV PROPPATCH).</summary>
    public void Touch(string path, DateTime modified)
    {
        lock (_sync)
        {
            if (!_entries.TryGetValue(path, out var e))
                return;
            e.Modified = modified;
            SaveIndex();
        }
    }

    public void Delete(string path)
    {
        if (path == "/")
            throw VaultException.BadPath();
        List<string> blobs;
        lock (_sync)
        {
            if (!_entries.ContainsKey(path))
                throw VaultException.NotFound(path);
            var victims = _entries.Values.Where(e => IsUnder(e.Path, path)).ToList();
            foreach (var v in victims)
                _entries.Remove(v.Path);
            blobs = victims.Where(v => v.Blob != null).Select(v => v.Blob!).ToList();
            SaveIndex();
        }
        foreach (var b in blobs)
            TryDelete(BlobPath(b));
    }

    /// <summary>Verschiebt/benennt um. Rückgabe: true = Ziel war neu.</summary>
    public bool Move(string from, string to, bool overwrite) => Transfer(from, to, overwrite, copy: false);

    public bool Copy(string from, string to, bool overwrite) => Transfer(from, to, overwrite, copy: true);

    private bool Transfer(string from, string to, bool overwrite, bool copy)
    {
        if (from == "/" || to == "/")
            throw VaultException.BadPath();
        if (IsUnder(to, from) && !from.Equals(to, StringComparison.Ordinal))
        {
            // in sich selbst verschieben – nur erlaubt, wenn es reine Umbenennung in Groß-/Kleinschreibung ist
            if (!from.Equals(to, StringComparison.OrdinalIgnoreCase))
                throw new VaultException(400, "badPath", "Ein Ordner kann nicht in sich selbst verschoben werden.");
        }

        var orphanBlobs = new List<string>();
        var copies = new List<(string Source, string Target)>();
        bool created;
        lock (_sync)
        {
            if (!_entries.TryGetValue(from, out var source))
                throw VaultException.NotFound(from);
            CheckParent(to);

            var caseRename = from.Equals(to, StringComparison.OrdinalIgnoreCase);
            created = caseRename || !_entries.ContainsKey(to);
            if (!created)
            {
                if (!overwrite)
                    throw VaultException.Exists(to);
                foreach (var v in _entries.Values.Where(e => IsUnder(e.Path, to)).ToList())
                {
                    _entries.Remove(v.Path);
                    if (v.Blob != null)
                        orphanBlobs.Add(v.Blob);
                }
            }

            var moving = _entries.Values.Where(e => IsUnder(e.Path, from)).OrderBy(e => e.Path.Length).ToList();
            foreach (var e in moving)
            {
                var newPath = to + e.Path[from.Length..];
                if (copy)
                {
                    var c = e.Clone();
                    c.Path = newPath;
                    c.Created = DateTime.UtcNow;
                    if (e.Blob != null)
                    {
                        c.Blob = Guid.NewGuid().ToString("N");
                        copies.Add((e.Blob, c.Blob));
                    }
                    _entries[newPath] = c;
                }
                else
                {
                    _entries.Remove(e.Path);
                    e.Path = newPath;
                    _entries[newPath] = e;
                }
            }
            // Blobs für Kopien zuerst anlegen, damit der Index nie auf fehlende Dateien zeigt
            foreach (var (src, dst) in copies)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(BlobPath(dst))!);
                File.Copy(BlobPath(src), BlobPath(dst));
            }
            SaveIndex();
        }

        foreach (var b in orphanBlobs)
            TryDelete(BlobPath(b));
        return created;
    }

    #endregion

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch
        {
            // Datei wird ggf. gerade gelesen – bleibt als verwaiste, verschlüsselte Datei liegen
        }
    }

    public static long DiskUsage(string dir)
    {
        try
        {
            return Directory.Exists(dir)
                ? new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length)
                : 0;
        }
        catch
        {
            return 0;
        }
    }
}
