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
