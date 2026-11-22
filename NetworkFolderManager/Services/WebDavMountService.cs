using Microsoft.Win32;
using NetworkFolderManager.Models;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace NetworkFolderManager.Services;

/// <summary>
/// Bindet einen Server-Ordner per WebDAV als Netzlaufwerk ein (Explorer).
///
/// Einmalig pro Server (mit Administratorrechten, UAC):
///  - Stammzertifikat des Servers als vertrauenswürdig installieren (es ist per Name Constraints auf lokale
///    Adressen beschränkt und kann keine fremden Websites ausstellen),
///  - Dateigrößen-Limit des Windows-WebDAV-Clients (Standard 50 MB) auf 4 GB anheben, Dienst "WebClient" automatisch starten.
/// </summary>
public static class WebDavMountService
{
    private const string WebClientParams = @"SYSTEM\CurrentControlSet\Services\WebClient\Parameters";

    /// <summary>Prüft, ob die einmalige Einrichtung für diesen Server schon erledigt ist.</summary>
    public static bool IsPrepared(SavedServer server)
    {
        try
        {
            using var store = new X509Store(StoreName.Root, StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadOnly);
            var trusted = store.Certificates.Cast<X509Certificate2>()
                .Any(c => Convert.ToHexString(SHA256.HashData(c.RawData)).Equals(server.Fingerprint, StringComparison.OrdinalIgnoreCase));
            using var key = Registry.LocalMachine.OpenSubKey(WebClientParams);
            var limit = key?.GetValue("FileSizeLimitInBytes") is int v ? unchecked((uint)v) : 50_000_000u;
            return trusted && limit >= 0xFFFFFFF0u;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Startet die einmalige Einrichtung mit Administratorrechten (UAC-Abfrage).</summary>
    public static async Task<bool> PrepareElevatedAsync(SavedServer server)
    {
        var certFile = Path.Combine(Path.GetTempPath(), $"nfm-ca-{Guid.NewGuid():N}.cer");
        await File.WriteAllBytesAsync(certFile, Convert.FromBase64String(server.CaCertificate));
        try
        {
            var psi = new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = $"--setup-webdav \"{certFile}\" {server.Fingerprint}"
            };
            using var p = Process.Start(psi);
            if (p == null)
                return false;
            await p.WaitForExitAsync();
            return p.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false; // UAC abgelehnt
        }
        finally
        {
            try { File.Delete(certFile); } catch { /* ignorieren */ }
        }
    }

    /// <summary>Wird als Administrator ausgeführt: --setup-webdav "Zertifikatsdatei" Fingerabdruck</summary>
    public static int SetupElevated(string certFile, string fingerprint)
    {
        try
        {
            var cert = new X509Certificate2(File.ReadAllBytes(certFile));
            // Nur genau das Zertifikat installieren, das der Benutzer bestätigt hat
            if (!Convert.ToHexString(SHA256.HashData(cert.RawData)).Equals(fingerprint, StringComparison.OrdinalIgnoreCase))
                return 2;
            if (!HasLocalNameConstraints(cert))
                return 3;

            using (var store = new X509Store(StoreName.Root, StoreLocation.LocalMachine))
            {
                store.Open(OpenFlags.ReadWrite);
                store.Add(cert);
            }

            using (var key = Registry.LocalMachine.CreateSubKey(WebClientParams))
                key.SetValue("FileSizeLimitInBytes", unchecked((int)0xFFFFFFFF), RegistryValueKind.DWord);

            Run("sc.exe", "config WebClient start= auto");
            Run("net.exe", "stop WebClient");
            Run("net.exe", "start WebClient");
            return 0;
        }
        catch
        {
            return 1;
        }
    }

    /// <summary>Sicherheitsprüfung: Nur CAs mit (kritischen) Name Constraints werden installiert.</summary>
    private static bool HasLocalNameConstraints(X509Certificate2 cert) =>
        cert.Extensions.Cast<X509Extension>().Any(e => e.Oid?.Value == "2.5.29.30" && e.Critical);

    private static void Run(string exe, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, args) { CreateNoWindow = true, UseShellExecute = false });
            p?.WaitForExit(20000);
        }
        catch
        {
            // ignorieren
        }
    }

    #region Laufwerk verbinden

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private class NETRESOURCE
    {
        public int dwScope;
        public int dwType = 1; // RESOURCETYPE_DISK
        public int dwDisplayType;
        public int dwUsage;
        public string? lpLocalName;
        public string? lpRemoteName;
        public string? lpComment;
        public string? lpProvider;
    }

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetAddConnection2(NETRESOURCE netResource, string? password, string? username, int flags);

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetCancelConnection2(string name, int flags, bool force);

    private const int CONNECT_UPDATE_PROFILE = 0x1;
    private const int CONNECT_CMD_SAVECRED = 0x1000;

    /// <summary>UNC-Pfad für WebDAV über HTTPS: \\host@SSL@port\dav\Ordner</summary>
    public static string UncPath(SavedServer server, string folderName) =>
        $@"\\{server.Host}@SSL@{server.Port}\dav\{folderName}";

    /// <summary>Freien Laufwerksbuchstaben (von Z rückwärts) suchen.</summary>
    public static string? FreeDriveLetter()
    {
        var used = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
        for (var c = 'Z'; c >= 'G'; c--)
            if (!used.Contains(c))
                return c + ":";
        return null;
    }

    /// <summary>Verbindet das Laufwerk (Anmeldung: Ordnername + Ordnerpasswort). Gibt den Laufwerksbuchstaben zurück.</summary>
    public static async Task<string> MountAsync(SavedServer server, string folderName, string password, bool persistent)
    {
        var drive = FreeDriveLetter() ?? throw new InvalidOperationException(Loc.T("Kein freier Laufwerksbuchstabe."));
        var resource = new NETRESOURCE { lpLocalName = drive, lpRemoteName = UncPath(server, folderName) };
        var flags = persistent ? CONNECT_UPDATE_PROFILE | CONNECT_CMD_SAVECRED : 0;
        var result = await Task.Run(() => WNetAddConnection2(resource, password, folderName, flags));
        if (result != 0)
            throw new InvalidOperationException(MountError(result));
        return drive;
    }

    public static void Unmount(string drive) => WNetCancelConnection2(drive, CONNECT_UPDATE_PROFILE, true);

    private static string MountError(int code) => code switch
    {
        5 or 86 or 1326 => Loc.T("Anmeldung abgelehnt – falsches Ordnerpasswort?"),
        53 or 67 => Loc.T("Server nicht erreichbar oder Zertifikat nicht vertrauenswürdig. Läuft der Dienst „WebClient“?"),
        85 => Loc.T("Der Laufwerksbuchstabe ist bereits belegt."),
        1219 => Loc.T("Es besteht bereits eine Verbindung zu diesem Server mit anderen Anmeldedaten. Bitte diese zuerst trennen."),
        1244 => Loc.T("Anmeldung erforderlich – falsches Ordnerpasswort?"),
        _ => Loc.T("Netzlaufwerk konnte nicht verbunden werden (Fehler {0}: {1}).", code, new System.ComponentModel.Win32Exception(code).Message)
    };

    #endregion
}
