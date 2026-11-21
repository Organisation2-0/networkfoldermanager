using System.Runtime.InteropServices;
using System.Text;

namespace NetworkFolderManager.Services;

/// <summary>
/// Speichert Zugangsdaten für Netzwerkordner sicher in der Windows-Anmeldeinformationsverwaltung
/// (Systemsteuerung → Anmeldeinformationsverwaltung → Windows-Anmeldeinformationen).
/// Windows verwendet diese Daten automatisch für SMB-Verbindungen zum jeweiligen Server –
/// das Passwort muss also nur einmal eingegeben werden. Es wird verschlüsselt (DPAPI) im
/// Benutzerprofil abgelegt und nie im Klartext von dieser App gespeichert.
/// </summary>
public static class CredentialService
{
    private const int CRED_TYPE_DOMAIN_PASSWORD = 2;
    private const int CRED_PERSIST_LOCAL_MACHINE = 2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public int Flags;
        public int Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string UserName;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref CREDENTIAL credential, int flags);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, int type, int flags, out IntPtr credential);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, int type, int flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);

    /// <summary>Zugangsdaten für einen Server speichern (überschreibt vorhandene).</summary>
    public static void Save(string server, string username, string password)
    {
        var blob = Encoding.Unicode.GetBytes(password);
        var blobPtr = Marshal.AllocHGlobal(Math.Max(1, blob.Length));
        try
        {
            Marshal.Copy(blob, 0, blobPtr, blob.Length);
            var cred = new CREDENTIAL
            {
                Type = CRED_TYPE_DOMAIN_PASSWORD,
                TargetName = Normalize(server),
                UserName = username,
                CredentialBlob = blobPtr,
                CredentialBlobSize = blob.Length,
                Persist = CRED_PERSIST_LOCAL_MACHINE,
                Comment = "NetworkFolderManager"
            };
            if (!CredWrite(ref cred, 0))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
        finally
        {
            // Passwort-Kopie im Speicher überschreiben
            Array.Clear(blob);
            Marshal.Copy(new byte[blob.Length], 0, blobPtr, blob.Length);
            Marshal.FreeHGlobal(blobPtr);
        }
    }

    /// <summary>Gespeicherten Benutzernamen für einen Server lesen (Passwort wird nie ausgelesen).</summary>
    public static string? GetUsername(string server)
    {
        if (!CredRead(Normalize(server), CRED_TYPE_DOMAIN_PASSWORD, 0, out var ptr))
            return null;
        try
        {
            return Marshal.PtrToStructure<CREDENTIAL>(ptr).UserName;
        }
        finally
        {
            CredFree(ptr);
        }
    }

    public static bool Exists(string server) => GetUsername(server) != null;

    public static void Delete(string server) =>
        CredDelete(Normalize(server), CRED_TYPE_DOMAIN_PASSWORD, 0);

    private static string Normalize(string server) => server.Trim().TrimStart('\\').Split('\\')[0];
}
