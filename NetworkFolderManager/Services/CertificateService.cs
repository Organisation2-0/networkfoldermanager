using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace NetworkFolderManager.Services;

/// <summary>
/// Verwaltet das TLS-Zertifikat dieser Installation für den verschlüsselten Chat.
/// Das Zertifikat ist selbstsigniert und liegt im Zertifikatspeicher des Benutzers
/// (certmgr.msc → Eigene Zertifikate). Andere Teilnehmer merken sich beim ersten Kontakt
/// dessen Fingerabdruck ("Trust on first use") und erkennen so später jeden Austausch.
/// </summary>
public static class CertificateService
{
    private static X509Certificate2? _cached;

    public static X509Certificate2 GetOrCreate(string peerId)
    {
        if (_cached != null)
            return _cached;

        var subject = $"CN=NetworkFolderManager {peerId}";
        if (!OperatingSystem.IsWindows())
            return _cached = GetOrCreateFromFile(subject);

        using (var store = new X509Store(StoreName.My, StoreLocation.CurrentUser))
        {
            store.Open(OpenFlags.ReadWrite);
            var existing = store.Certificates
                .Find(X509FindType.FindBySubjectDistinguishedName, subject, validOnly: false)
                .OfType<X509Certificate2>()
                .Where(c => c.HasPrivateKey && c.NotAfter > DateTime.Now.AddDays(30))
                .OrderByDescending(c => c.NotAfter)
                .FirstOrDefault();

            if (existing != null)
                return _cached = existing;

            using var rsa = RSA.Create(3072);
            var request = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, critical: true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                new OidCollection
                {
                    new Oid("1.3.6.1.5.5.7.3.1"), // Serverauthentifizierung
                    new Oid("1.3.6.1.5.5.7.3.2")  // Clientauthentifizierung
                }, critical: false));

            using var created = request.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(20));

            // Über PFX neu laden, damit der private Schlüssel dauerhaft gespeichert wird (SChannel braucht das)
            var pfx = created.Export(X509ContentType.Pfx);
            var persisted = new X509Certificate2(pfx, (string?)null,
                X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.UserKeySet);
            CryptographicOperations.ZeroMemory(pfx);

            persisted.FriendlyName = "NetworkFolderManager Chat";
            store.Add(persisted);
            return _cached = persisted;
        }
    }

    /// <summary>
    /// macOS/Linux: Das Zertifikat liegt als PFX-Datei (nur für den Benutzer lesbar) im Einstellungsordner,
    /// weil der Zertifikatspeicher dort für TLS-Server unzuverlässig ist.
    /// </summary>
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private static X509Certificate2 GetOrCreateFromFile(string subject)
    {
        var file = System.IO.Path.Combine(SettingsService.SettingsDir, "chat-certificate.pfx");
        try
        {
            if (System.IO.File.Exists(file))
            {
                var loaded = new X509Certificate2(file, (string?)null, X509KeyStorageFlags.Exportable);
                if (loaded.NotAfter > DateTime.Now.AddDays(30))
                    return loaded;
            }
        }
        catch
        {
            // beschädigt → neu erzeugen
        }

        using var rsa = RSA.Create(3072);
        var request = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid("1.3.6.1.5.5.7.3.1"), new Oid("1.3.6.1.5.5.7.3.2") }, critical: false));
        using var created = request.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(20));

        var pfx = created.Export(X509ContentType.Pfx);
        System.IO.Directory.CreateDirectory(SettingsService.SettingsDir);
        System.IO.File.WriteAllBytes(file, pfx);
        try { System.IO.File.SetUnixFileMode(file, System.IO.UnixFileMode.UserRead | System.IO.UnixFileMode.UserWrite); } catch { /* ignorieren */ }
        var result = new X509Certificate2(pfx, (string?)null, X509KeyStorageFlags.Exportable);
        CryptographicOperations.ZeroMemory(pfx);
        return result;
    }

    /// <summary>SHA-256-Fingerabdruck als Hex-String.</summary>
    public static string Fingerprint(X509Certificate? cert)
    {
        if (cert == null)
            return string.Empty;
        return Convert.ToHexString(SHA256.HashData(cert.GetRawCertData()));
    }

    /// <summary>Kurze, vergleichbare "Sicherheitsnummer" (z.B. zum Abgleichen am Telefon).</summary>
    public static string ShortCode(string fingerprint) =>
        string.IsNullOrEmpty(fingerprint)
            ? "–"
            : string.Join(" ", Enumerable.Range(0, 5).Select(i => fingerprint.Substring(i * 4, 4)));
}
