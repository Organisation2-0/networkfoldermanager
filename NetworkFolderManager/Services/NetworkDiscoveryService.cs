using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;

namespace NetworkFolderManager.Services;

/// <summary>
/// Dient zum Scannen des lokalen Netzwerks nach freigegebenen Ordnern
/// sowie zum Verbinden/Trennen von (passwortgeschützten) Freigaben.
/// Nutzt NETAPI32.DLL (NetServerEnum/NetShareEnum) und MPR.DLL (WNet*).
/// </summary>
public static class NetworkDiscoveryService
{
    private const int MaxPreferredLength = -1;
    private const int ErrorMoreData = 234;
    private const int NerrSuccess = 0;
    private const uint SvTypeWorkstation = 0x00000001;
    private const uint SvTypeServer = 0x00000002;
    private const uint StypeDisktree = 0;
    private const uint StypeMask = 0x000000FF;

    /// <summary>
    /// Scant das lokale Netzwerk nach allen freigegebenen Ordnern.
    /// </summary>
    public static async Task<List<Models.NetworkFolder>> ScanNetworkAsync(
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Report(Loc.T("Suche Computer im Netzwerk..."));
        var computers = await Task.Run(EnumerateComputers, cancellationToken);

        var results = new List<Models.NetworkFolder>();
        var lockObj = new object();
        var done = 0;

        // Freigaben parallel abfragen (nicht erreichbare Rechner blockieren sonst lange)
        await Parallel.ForEachAsync(
            computers,
            new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = cancellationToken },
            async (computer, ct) =>
            {
                var shares = await Task.Run(() => EnumerateShares(computer), ct);
                lock (lockObj)
                {
                    results.AddRange(shares);
                    done++;
                    progress?.Report(Loc.T("Freigaben werden abgefragt ({0}/{1})...", done, computers.Count));
                }
            });

        return results
            .OrderBy(f => f.ComputerName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(f => f.ShareName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Erstellt einen NetworkFolder aus einem manuell eingegebenen UNC-Pfad (\\server\freigabe).
    /// </summary>
    public static Models.NetworkFolder? ParseUncPath(string input)
    {
        var path = input.Trim().Replace('/', '\\').TrimEnd('\\');
        if (!path.StartsWith(@"\\"))
            path = @"\\" + path.TrimStart('\\');

        var parts = path.Substring(2).Split('\\', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            return null;

        return CreateFolder(parts[0], parts[1], Loc.T("Manuell hinzugefügt"));
    }

    /// <summary>
    /// Ermittelt Computernamen über den Computer-Browser-Dienst (NetServerEnum)
    /// und das Windows-Netzwerk (WNetEnumResource). Der lokale Rechner ist immer enthalten.
    /// </summary>
    private static List<string> EnumerateComputers()
    {
        var computers = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Environment.MachineName };

        foreach (var name in EnumerateViaNetServerEnum())
            computers.Add(name);

        foreach (var name in EnumerateViaWNet())
            computers.Add(name);

        return computers.ToList();
    }

    private static IEnumerable<string> EnumerateViaNetServerEnum()
    {
        var names = new List<string>();
        var bufPtr = IntPtr.Zero;
        try
        {
            var result = WNetApi.NetServerEnum(
                null, 100, out bufPtr, MaxPreferredLength,
                out var entriesRead, out _,
                SvTypeWorkstation | SvTypeServer, null, IntPtr.Zero);

            if (result is NerrSuccess or ErrorMoreData && bufPtr != IntPtr.Zero)
            {
                var size = Marshal.SizeOf<WNetApi.SERVER_INFO_100>();
                for (var i = 0; i < entriesRead; i++)
                {
                    var info = Marshal.PtrToStructure<WNetApi.SERVER_INFO_100>(bufPtr + i * size);
                    if (!string.IsNullOrWhiteSpace(info.sv100_name))
                        names.Add(info.sv100_name);
                }
            }
        }
        catch
        {
            // Browser-Dienst ist unter Windows 10/11 oft deaktiviert – kein Fehler.
        }
        finally
        {
            if (bufPtr != IntPtr.Zero)
                WNetApi.NetApiBufferFree(bufPtr);
        }
        return names;
    }

    /// <summary>
    /// Durchläuft das "Microsoft Windows Network" rekursiv bis zur Server-Ebene.
    /// </summary>
    private static IEnumerable<string> EnumerateViaWNet()
    {
        var names = new List<string>();
        try
        {
            EnumerateWNetContainer(null, names, depth: 0);
        }
        catch
        {
            // ignorieren
        }
        return names;
    }

    private static void EnumerateWNetContainer(WNetApi.NETRESOURCE? container, List<string> names, int depth)
    {
        if (depth > 3)
            return;

        IntPtr containerPtr = IntPtr.Zero;
        try
        {
            if (container.HasValue)
            {
                containerPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WNetApi.NETRESOURCE>());
                Marshal.StructureToPtr(container.Value, containerPtr, false);
            }

            if (WNetApi.WNetOpenEnum(WNetApi.RESOURCE_GLOBALNET, WNetApi.RESOURCETYPE_DISK, 0, containerPtr, out var hEnum) != 0)
                return;

            try
            {
                const int bufferSize = 16 * 1024;
                var buffer = Marshal.AllocHGlobal(bufferSize);
                try
                {
                    while (true)
                    {
                        var count = -1;
                        var size = bufferSize;
                        var ret = WNetApi.WNetEnumResource(hEnum, ref count, buffer, ref size);
                        if (ret != 0)
                            break;

                        var structSize = Marshal.SizeOf<WNetApi.NETRESOURCE>();
                        for (var i = 0; i < count; i++)
                        {
                            var res = Marshal.PtrToStructure<WNetApi.NETRESOURCE>(buffer + i * structSize);
                            if (res.dwDisplayType == WNetApi.RESOURCEDISPLAYTYPE_SERVER)
                            {
                                var name = res.lpRemoteName?.TrimStart('\\');
                                if (!string.IsNullOrWhiteSpace(name))
                                    names.Add(name);
                            }
                            else if ((res.dwUsage & WNetApi.RESOURCEUSAGE_CONTAINER) != 0)
                            {
                                EnumerateWNetContainer(res, names, depth + 1);
                            }
                        }
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            finally
            {
                WNetApi.WNetCloseEnum(hEnum);
            }
        }
        finally
        {
            if (containerPtr != IntPtr.Zero)
            {
                Marshal.DestroyStructure<WNetApi.NETRESOURCE>(containerPtr);
                Marshal.FreeHGlobal(containerPtr);
            }
        }
    }

    /// <summary>
    /// Enumeriert alle Datei-Freigaben (ohne Admin-/versteckte Freigaben) eines Computers.
    /// </summary>
    private static List<Models.NetworkFolder> EnumerateShares(string computerName)
    {
        var shares = new List<Models.NetworkFolder>();
        var bufPtr = IntPtr.Zero;
        try
        {
            var result = WNetApi.NetShareEnum(
                $@"\\{computerName}", 1, out bufPtr, MaxPreferredLength,
                out var entriesRead, out _, IntPtr.Zero);

            if (result is NerrSuccess or ErrorMoreData && bufPtr != IntPtr.Zero)
            {
                var size = Marshal.SizeOf<WNetApi.SHARE_INFO_1>();
                for (var i = 0; i < entriesRead; i++)
                {
                    var info = Marshal.PtrToStructure<WNetApi.SHARE_INFO_1>(bufPtr + i * size);

                    // Nur Datei-Freigaben, keine Drucker/IPC und keine versteckten ($) Freigaben
                    if ((info.shi1_type & StypeMask) != StypeDisktree || info.shi1_netname.EndsWith('$'))
                        continue;

                    shares.Add(CreateFolder(computerName, info.shi1_netname,
                        string.IsNullOrWhiteSpace(info.shi1_remark) ? Loc.T("Netzwerkfreigabe") : info.shi1_remark));
                }
            }
        }
        catch
        {
            // Rechner nicht erreichbar
        }
        finally
        {
            if (bufPtr != IntPtr.Zero)
                WNetApi.NetApiBufferFree(bufPtr);
        }
        return shares;
    }

    private static Models.NetworkFolder CreateFolder(string computer, string share, string description) => new()
    {
        ComputerName = computer,
        ShareName = share,
        DisplayName = $@"{computer}\{share}",
        Path = $@"\\{computer}\{share}",
        Description = description,
        IsConnected = false,
        IsOwnFolder = false
    };

    /// <summary>
    /// Verbindet mit einem (passwortgeschützten) Netzwerkordner.
    /// Ohne Benutzername/Passwort verwendet Windows gespeicherte Anmeldedaten.
    /// Wirft eine <see cref="Win32Exception"/> mit lesbarer Meldung bei Fehlern.
    /// </summary>
    public static Task ConnectToFolderAsync(
        string uncPath,
        string? username,
        string? password,
        bool remember = false,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            var netResource = new WNetApi.NETRESOURCE
            {
                dwType = WNetApi.RESOURCETYPE_DISK,
                lpRemoteName = uncPath
            };

            var flags = remember ? WNetApi.CONNECT_UPDATE_PROFILE : WNetApi.CONNECT_TEMPORARY;
            var ret = WNetApi.WNetAddConnection2(ref netResource, password, username, flags);

            // Bereits mit anderen Anmeldedaten verbunden → trennen und erneut versuchen
            if (ret == WNetApi.ERROR_SESSION_CREDENTIAL_CONFLICT)
            {
                WNetApi.WNetCancelConnection2(uncPath, 0, true);
                ret = WNetApi.WNetAddConnection2(ref netResource, password, username, flags);
            }

            if (ret != 0)
                throw new Win32Exception(ret, DescribeError(ret));
        }, cancellationToken);
    }

    /// <summary>
    /// Trennt die Verbindung zu einem Netzwerkordner.
    /// </summary>
    public static Task DisconnectFolderAsync(string uncPath, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            var ret = WNetApi.WNetCancelConnection2(uncPath, WNetApi.CONNECT_UPDATE_PROFILE, true);
            if (ret != 0 && ret != WNetApi.ERROR_NOT_CONNECTED)
                throw new Win32Exception(ret, DescribeError(ret));
        }, cancellationToken);
    }

    /// <summary>
    /// Prüft, ob ein Pfad (lokal oder UNC) erreichbar ist.
    /// </summary>
    public static Task<bool> IsPathAccessibleAsync(string path, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            try
            {
                return Directory.Exists(path);
            }
            catch
            {
                return false;
            }
        }, cancellationToken);
    }

    private static string DescribeError(int code) => code switch
    {
        5 => Loc.T("Zugriff verweigert."),
        53 => Loc.T("Netzwerkpfad nicht gefunden."),
        67 => Loc.T("Netzwerkname nicht gefunden."),
        86 => Loc.T("Falsches Passwort."),
        1219 => Loc.T("Es besteht bereits eine Verbindung mit anderen Anmeldedaten zu diesem Server."),
        1326 => Loc.T("Benutzername oder Passwort ist falsch."),
        1203 => Loc.T("Kein Netzwerkanbieter hat den Pfad akzeptiert."),
        1222 => Loc.T("Das Netzwerk ist nicht verfügbar."),
        _ => new Win32Exception(code).Message
    };
}

/// <summary>
/// P/Invoke Definitionen für NETAPI32.DLL und MPR.DLL.
/// </summary>
internal static class WNetApi
{
    public const int RESOURCE_GLOBALNET = 2;
    public const int RESOURCETYPE_DISK = 1;
    public const int RESOURCEUSAGE_CONTAINER = 2;
    public const int RESOURCEDISPLAYTYPE_SERVER = 2;
    public const int CONNECT_UPDATE_PROFILE = 1;
    public const int CONNECT_TEMPORARY = 4;
    public const int ERROR_NOT_CONNECTED = 2250;
    public const int ERROR_SESSION_CREDENTIAL_CONFLICT = 1219;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct NETRESOURCE
    {
        public int dwScope;
        public int dwType;
        public int dwDisplayType;
        public int dwUsage;
        public string? lpLocalName;
        public string? lpRemoteName;
        public string? lpComment;
        public string? lpProvider;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct SERVER_INFO_100
    {
        public uint sv100_platform_id;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string sv100_name;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct SHARE_INFO_1
    {
        [MarshalAs(UnmanagedType.LPWStr)]
        public string shi1_netname;
        public uint shi1_type;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string shi1_remark;
    }

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    public static extern int NetServerEnum(
        string? serverName, int level, out IntPtr bufPtr, int prefMaxLen,
        out int entriesRead, out int totalEntries, uint serverType,
        string? domain, IntPtr resumeHandle);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    public static extern int NetShareEnum(
        string serverName, int level, out IntPtr bufPtr, int prefMaxLen,
        out int entriesRead, out int totalEntries, IntPtr resumeHandle);

    [DllImport("netapi32.dll")]
    public static extern int NetApiBufferFree(IntPtr buffer);

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    public static extern int WNetAddConnection2(ref NETRESOURCE netResource, string? password, string? username, int flags);

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    public static extern int WNetCancelConnection2(string name, int flags, bool force);

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    public static extern int WNetOpenEnum(int scope, int type, int usage, IntPtr netResource, out IntPtr hEnum);

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    public static extern int WNetEnumResource(IntPtr hEnum, ref int count, IntPtr buffer, ref int bufferSize);

    [DllImport("mpr.dll")]
    public static extern int WNetCloseEnum(IntPtr hEnum);
}
