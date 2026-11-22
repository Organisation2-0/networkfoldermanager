using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace NetworkFolderManager.Server;

/// <summary>Passwort-Hashes für Konten (PBKDF2-SHA256).</summary>
public static class Passwords
{
    private const int Iterations = 210_000;
    public const int MinLength = 8;

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2-sha256${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string stored)
    {
        try
        {
            var parts = stored.Split('$');
            if (parts.Length != 4 || parts[0] != "pbkdf2-sha256")
                return false;
            var iterations = int.Parse(parts[1]);
            var salt = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Prüft die Mindestanforderungen; null = in Ordnung, sonst Fehlermeldung.</summary>
    public static string? Validate(string? password)
    {
        if (string.IsNullOrEmpty(password) || password.Length < MinLength)
            return $"Das Passwort muss mindestens {MinLength} Zeichen lang sein.";
        if (password.Length > 256)
            return "Das Passwort ist zu lang.";
        return null;
    }
}

/// <summary>
/// Verschlüsselung der Ordner:
///  - Ordnerschlüssel (256 Bit, zufällig) verschlüsselt alle Dateien und das Inhaltsverzeichnis (AES-256-GCM).
///  - Er wird mit einem aus dem Ordnerpasswort abgeleiteten Schlüssel (PBKDF2-SHA256) verpackt gespeichert.
///  Ohne Ordnerpasswort (auch als Admin oder mit Zugriff auf die Festplatte) sind die Daten nicht lesbar.
/// </summary>
public static class FolderCrypto
{
    public const int KdfIterations = 310_000;
    private static readonly byte[] WrapAad = "NFM-FOLDER-KEY-V1"u8.ToArray();

    public static byte[] NewKey() => RandomNumberGenerator.GetBytes(32);

    public static byte[] NewSalt() => RandomNumberGenerator.GetBytes(16);

    public static byte[] DeriveKek(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, 32);

    public static byte[] Wrap(byte[] kek, byte[] key) => Seal(kek, key, WrapAad);

    public static byte[]? Unwrap(byte[] kek, byte[] wrapped) => Open(kek, wrapped, WrapAad);

    /// <summary>Verschlüsselt kleine Daten: Nonce(12) | Tag(16) | Chiffrat.</summary>
    public static byte[] Seal(byte[] key, byte[] plain, byte[] aad)
    {
        var result = new byte[12 + 16 + plain.Length];
        var nonce = result.AsSpan(0, 12);
        RandomNumberGenerator.Fill(nonce);
        using var gcm = new AesGcm(key, 16);
        gcm.Encrypt(nonce, plain, result.AsSpan(28), result.AsSpan(12, 16), aad);
        return result;
    }

    public static byte[]? Open(byte[] key, byte[] sealedData, byte[] aad)
    {
        if (sealedData.Length < 28)
            return null;
        var plain = new byte[sealedData.Length - 28];
        try
        {
            using var gcm = new AesGcm(key, 16);
            gcm.Decrypt(sealedData.AsSpan(0, 12), sealedData.AsSpan(28), sealedData.AsSpan(12, 16), plain, aad);
            return plain;
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    public static string Sha256Hex(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}

/// <summary>Zufällige Zugangs-Token (URL-sicher) und deren Hash zum Speichern.</summary>
public static class Tokens
{
    public static string New() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string Hash(string token) => FolderCrypto.Sha256Hex("nfm-token:" + token);

    /// <summary>Gut lesbarer Einmal-Code (ohne verwechselbare Zeichen).</summary>
    public static string NewSetupCode()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var chars = new char[10];
        for (var i = 0; i < chars.Length; i++)
            chars[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
        return new string(chars, 0, 5) + "-" + new string(chars, 5, 5);
    }
}

/// <summary>
/// Bremst Passwort-Raten: nach 8 Fehlversuchen in 10 Minuten wird die Quelle für 5 Minuten gesperrt.
/// </summary>
public sealed class AttemptLimiter
{
    private const int MaxFailures = 8;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan Lockout = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, Entry> _entries = new();

    private sealed class Entry
    {
        public readonly Queue<DateTime> Failures = new();
        public DateTime BlockedUntil;
    }

    public bool IsBlocked(string key, out TimeSpan retryAfter)
    {
        retryAfter = TimeSpan.Zero;
        if (!_entries.TryGetValue(key, out var e))
            return false;
        lock (e)
        {
            var now = DateTime.UtcNow;
            if (e.BlockedUntil > now)
            {
                retryAfter = e.BlockedUntil - now;
                return true;
            }
            return false;
        }
    }

    public void Fail(string key)
    {
        var e = _entries.GetOrAdd(key, _ => new Entry());
        lock (e)
        {
            var now = DateTime.UtcNow;
            e.Failures.Enqueue(now);
            while (e.Failures.Count > 0 && e.Failures.Peek() < now - Window)
                e.Failures.Dequeue();
            if (e.Failures.Count >= MaxFailures)
            {
                e.BlockedUntil = now + Lockout;
                e.Failures.Clear();
            }
        }
    }

    public void Success(string key) => _entries.TryRemove(key, out _);

    public void Cleanup()
    {
        var now = DateTime.UtcNow;
        foreach (var (key, e) in _entries)
        {
            lock (e)
            {
                if (e.BlockedUntil < now && (e.Failures.Count == 0 || e.Failures.Peek() < now - Window))
                    _entries.TryRemove(key, out _);
            }
        }
    }
}

/// <summary>Regeln für Benutzer- und Ordnernamen.</summary>
public static partial class Names
{
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{2,31}$")]
    private static partial Regex UsernameRegex();

    public static string? ValidateUsername(string? name) =>
        name != null && UsernameRegex().IsMatch(name)
            ? null
            : "Benutzername: 3–32 Zeichen, nur Buchstaben, Ziffern, Punkt, Minus und Unterstrich.";

    public static string? ValidateFolderName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "Bitte einen Ordnernamen angeben.";
        if (name.Length > 64)
            return "Der Ordnername darf höchstens 64 Zeichen lang sein.";
        if (name != name.Trim() || name.EndsWith('.') || name.StartsWith('.'))
            return "Der Ordnername darf nicht mit Leerzeichen oder Punkt beginnen/enden.";
        if (name.IndexOfAny(new[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|', '%', '#' }) >= 0 || name.Any(char.IsControl))
            return "Der Ordnername darf keines dieser Zeichen enthalten: / \\ : * ? \" < > | % #";
        return null;
    }

    /// <summary>Prüft einen einzelnen Datei-/Ordnernamen innerhalb eines Server-Ordners.</summary>
    public static bool IsValidItemName(string name) =>
        name.Length is > 0 and <= 255 && name != "." && name != ".." &&
        name.IndexOfAny(new[] { '/', '\\', '\0' }) < 0 && !name.Any(char.IsControl);
}
