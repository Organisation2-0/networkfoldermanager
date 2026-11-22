using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace NetworkFolderManager.Server;

/// <summary>
/// TLS für den Server:
///  - Eine eigene Stammzertifizierungsstelle (CA, 10 Jahre) – deren Fingerabdruck wird bei der Erkennung
///    verteilt und von der Windows-App fest geprüft ("Pinning"). Für Browser/Finder kann sie unter /ca.crt
///    heruntergeladen und als vertrauenswürdig markiert werden.
///  - Ein Serverzertifikat (von der CA signiert) mit allen aktuellen IP-Adressen und Rechnernamen.
///    Ändern sich die Adressen (DHCP), wird es automatisch neu ausgestellt – die CA bleibt gleich.
/// </summary>
public sealed class CertificateManager
{
    private readonly ServerPaths _paths;
    private readonly ILogger? _log;
    private readonly object _lock = new();
    private X509Certificate2 _ca;
    private X509Certificate2 _leaf;
    private string _leafNames = string.Empty;

    public CertificateManager(ServerPaths paths, ILogger? log = null)
    {
        _paths = paths;
        _log = log;
        Directory.CreateDirectory(Path.GetDirectoryName(paths.CaFile)!);
        ServerPaths.Restrict(Path.GetDirectoryName(paths.CaFile)!, isDir: true);
        _ca = LoadOrCreateCa();
        _leaf = LoadOrCreateLeaf();
    }

    /// <summary>SHA-256 des CA-Zertifikats (Hex, Großbuchstaben).</summary>
    public string Fingerprint => Convert.ToHexString(SHA256.HashData(_ca.RawData));

    /// <summary>Für Menschen lesbar: "AB12 CD34 ..." (die ersten 16 Byte).</summary>
    public string FingerprintShort => string.Join(" ", Enumerable.Range(0, 8).Select(i => Fingerprint.Substring(i * 4, 4)));

    public X509Certificate2 Current
    {
        get
        {
            lock (_lock)
                return _leaf;
        }
    }

    public byte[] CaPem() => System.Text.Encoding.ASCII.GetBytes(_ca.ExportCertificatePem() + "\n");

    public byte[] CaDer() => _ca.Export(X509ContentType.Cert);

    /// <summary>Regelmäßig aufrufen: stellt das Serverzertifikat neu aus, wenn sich Adressen geändert haben oder es bald abläuft.</summary>
    public void Refresh()
    {
        lock (_lock)
        {
            var names = CurrentNames();
            if (names.Key == _leafNames && _leaf.NotAfter > DateTime.Now.AddDays(30))
                return;
            _log?.LogInformation("Serverzertifikat wird neu ausgestellt ({Names}).", names.Key);
            _leaf = CreateLeaf(names);
        }
    }

    private X509Certificate2 LoadOrCreateCa()
    {
        if (File.Exists(_paths.CaFile))
        {
            var ca = new X509Certificate2(_paths.CaFile, (string?)null, X509KeyStorageFlags.Exportable | KeyStorageFlags());
            if (ca.HasPrivateKey && ca.NotAfter > DateTime.Now.AddDays(30))
                return ca;
        }

        using var rsa = RSA.Create(3072);
        var req = new CertificateRequest(
            new X500DistinguishedName($"CN=NetworkFolderManager Server CA ({Environment.MachineName}), O=noobnet.eu"),
            rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));
        req.CertificateExtensions.Add(LocalNetworkNameConstraints());
        using var created = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));
        File.WriteAllBytes(_paths.CaFile, created.Export(X509ContentType.Pfx));
        ServerPaths.Restrict(_paths.CaFile, isDir: false);
        return new X509Certificate2(_paths.CaFile, (string?)null, X509KeyStorageFlags.Exportable | KeyStorageFlags());
    }

    private X509Certificate2 LoadOrCreateLeaf()
    {
        var names = CurrentNames();
        if (File.Exists(_paths.LeafFile) && File.Exists(_paths.LeafFile + ".names"))
        {
            try
            {
                var leaf = new X509Certificate2(_paths.LeafFile, (string?)null, KeyStorageFlags());
                if (File.ReadAllText(_paths.LeafFile + ".names") == names.Key && leaf.NotAfter > DateTime.Now.AddDays(30) &&
                    leaf.Issuer == _ca.Subject)
                {
                    _leafNames = names.Key;
                    return leaf;
                }
            }
            catch
            {
                // neu ausstellen
            }
        }
        return CreateLeaf(names);
    }

    private X509Certificate2 CreateLeaf((string Key, List<string> Dns, List<IPAddress> Ips) names)
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest(new X500DistinguishedName($"CN={Environment.MachineName}, O=noobnet.eu NetworkFolderManager"),
            rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));
        req.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(_ca, true, false));

        var san = new SubjectAlternativeNameBuilder();
        foreach (var dns in names.Dns)
            san.AddDnsName(dns);
        foreach (var ip in names.Ips)
            san.AddIpAddress(ip);
        req.CertificateExtensions.Add(san.Build());

        var serial = RandomNumberGenerator.GetBytes(16);
        serial[0] &= 0x7F;
        // max. 397 Tage – Apple/Browser lehnen längere Laufzeiten ab
        using var cert = req.Create(_ca, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(397), serial);
        using var withKey = cert.CopyWithPrivateKey(rsa);

        // Über PFX neu laden, damit der Schlüssel unter Windows (SChannel) nutzbar ist
        var pfx = withKey.Export(X509ContentType.Pfx);
        File.WriteAllBytes(_paths.LeafFile, pfx);
        File.WriteAllText(_paths.LeafFile + ".names", names.Key);
        ServerPaths.Restrict(_paths.LeafFile, isDir: false);
        _leafNames = names.Key;
        return new X509Certificate2(pfx, (string?)null, KeyStorageFlags());
    }

    /// <summary>Private/lokale IPv4-Netze (inkl. Loopback und CGNAT/Tailscale) und IPv6-ULA.</summary>
    private static readonly (string Net, int Prefix)[] LocalNets =
    {
        ("10.0.0.0", 8), ("172.16.0.0", 12), ("192.168.0.0", 16), ("127.0.0.0", 8), ("169.254.0.0", 16), ("100.64.0.0", 10),
        ("fc00::", 7), ("::1", 128)
    };

    private static bool IsLocalAddress(IPAddress ip) =>
        LocalNets.Any(n => InNet(ip, IPAddress.Parse(n.Net), n.Prefix));

    private static bool InNet(IPAddress ip, IPAddress net, int prefix)
    {
        if (ip.AddressFamily != net.AddressFamily)
            return false;
        var a = ip.GetAddressBytes();
        var b = net.GetAddressBytes();
        for (var i = 0; i < a.Length; i++)
        {
            var bits = Math.Clamp(prefix - i * 8, 0, 8);
            var mask = (byte)(0xFF << (8 - bits));
            if ((a[i] & mask) != (b[i] & mask))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Name Constraints für die CA: Sie darf nur Zertifikate für lokale Namen (*.local, *.lan, Rechnername, …)
    /// und private IP-Adressen ausstellen. Selbst wenn jemand den CA-Schlüssel vom Server stiehlt, kann er damit
    /// keine fremden Websites (z.B. Banken) fälschen – wichtig, weil Clients die CA als vertrauenswürdig installieren.
    /// </summary>
    private static X509Extension LocalNetworkNameConstraints()
    {
        var dnsNames = new SortedSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "localhost", "local", "lan", "home", "internal", "home.arpa", "fritz.box", Environment.MachineName.ToLowerInvariant()
        };
        try
        {
            dnsNames.Add(Dns.GetHostName().ToLowerInvariant());
        }
        catch
        {
            // ignorieren
        }

        var writer = new System.Formats.Asn1.AsnWriter(System.Formats.Asn1.AsnEncodingRules.DER);
        using (writer.PushSequence())
        {
            // permittedSubtrees [0] IMPLICIT GeneralSubtrees
            using (writer.PushSequence(new System.Formats.Asn1.Asn1Tag(System.Formats.Asn1.TagClass.ContextSpecific, 0)))
            {
                foreach (var dns in dnsNames)
                {
                    using (writer.PushSequence())
                        writer.WriteCharacterString(System.Formats.Asn1.UniversalTagNumber.IA5String, dns,
                            new System.Formats.Asn1.Asn1Tag(System.Formats.Asn1.TagClass.ContextSpecific, 2));
                }
                foreach (var (net, prefix) in LocalNets)
                {
                    var addr = IPAddress.Parse(net).GetAddressBytes();
                    var mask = new byte[addr.Length];
                    for (var i = 0; i < mask.Length; i++)
                        mask[i] = (byte)(0xFF << (8 - Math.Clamp(prefix - i * 8, 0, 8)));
                    using (writer.PushSequence())
                        writer.WriteOctetString(addr.Concat(mask).ToArray(),
                            new System.Formats.Asn1.Asn1Tag(System.Formats.Asn1.TagClass.ContextSpecific, 7));
                }
            }
        }
        return new X509Extension("2.5.29.30", writer.Encode(), critical: true);
    }

    // Aus PFX geladene Schlüssel (nicht "ephemeral") funktionieren auch mit SChannel unter Windows
    private static X509KeyStorageFlags KeyStorageFlags() => X509KeyStorageFlags.DefaultKeySet;

    /// <summary>Rechnername(n) und alle lokalen IP-Adressen dieses Geräts (nur was die CA-Name-Constraints erlauben).</summary>
    private (string Key, List<string> Dns, List<IPAddress> Ips) CurrentNames()
    {
        var host = Environment.MachineName.ToLowerInvariant();
        var dns = new SortedSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "localhost", host + ".local", host + ".fritz.box", host + ".lan", host + ".home", host + ".home.arpa", host + ".internal"
        };
        // Der reine Rechnername ist nur erlaubt, wenn die CA auf diesem Rechnernamen erstellt wurde
        if (_ca.Subject.Contains($"({Environment.MachineName})", StringComparison.OrdinalIgnoreCase))
            dns.Add(host);

        var ips = new SortedSet<string> { "127.0.0.1", "::1" };
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up)
                    continue;
                foreach (var ua in nic.GetIPProperties().UnicastAddresses)
                {
                    var a = ua.Address;
                    var usable = a.AddressFamily == AddressFamily.InterNetwork ||
                                 (a.AddressFamily == AddressFamily.InterNetworkV6 && !a.IsIPv6LinkLocal && a.ScopeId == 0);
                    if (usable && IsLocalAddress(a))
                        ips.Add(a.ToString());
                }
            }
        }
        catch
        {
            // ignorieren
        }

        var ipList = ips.Select(IPAddress.Parse).ToList();
        return (string.Join(",", dns) + "|" + string.Join(",", ips), dns.ToList(), ipList);
    }
}
