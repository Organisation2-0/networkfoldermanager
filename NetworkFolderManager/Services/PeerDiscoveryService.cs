using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace NetworkFolderManager.Services;

/// <summary>
/// Inhalt eines Discovery-Pakets (UDP-Broadcast).
/// </summary>
public class PeerAnnouncement
{
    public string App { get; set; } = PeerDiscoveryService.AppTag;
    public int V { get; set; } = 1;
    public string Type { get; set; } = "hello"; // hello | reply | bye
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Machine { get; set; } = string.Empty;
    public int Port { get; set; }
    public string? Share { get; set; }

    /// <summary>Benutzername für den freigegebenen Ordner (COMPUTER\Konto).</summary>
    public string? ShareUser { get; set; }

    /// <summary>SHA-256-Fingerabdruck des Chat-Zertifikats.</summary>
    public string Fp { get; set; } = string.Empty;

    /// <summary>Absender-IP (wird beim Empfang gesetzt).</summary>
    public string Address { get; set; } = string.Empty;
}

/// <summary>
/// Findet andere NetworkFolderManager-Installationen im lokalen Netzwerk per UDP-Broadcast.
/// Jede Instanz sendet alle 5 Sekunden ein "hello"; Peers ohne Lebenszeichen für 20 Sekunden gelten als offline.
/// </summary>
public sealed class PeerDiscoveryService : IDisposable
{
    public const string AppTag = "NFM";
    public const int DiscoveryPort = 45821;
    private static readonly TimeSpan AnnounceInterval = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan OfflineAfter = TimeSpan.FromSeconds(20);

    private readonly Func<PeerAnnouncement> _selfFactory;
    private UdpClient? _udp;
    private CancellationTokenSource? _cts;
    private readonly Dictionary<string, DateTime> _lastSeen = new();
    private readonly object _lock = new();

    /// <summary>Ein Peer hat sich gemeldet (neu oder erneut).</summary>
    public event EventHandler<PeerAnnouncement>? PeerSeen;

    /// <summary>Ein Peer ist offline gegangen (bye oder Timeout).</summary>
    public event EventHandler<string>? PeerLost;

    public PeerDiscoveryService(Func<PeerAnnouncement> selfFactory)
    {
        _selfFactory = selfFactory;
    }

    public void Start()
    {
        if (_udp != null)
            return;

        _udp = new UdpClient(AddressFamily.InterNetwork);
        _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _udp.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));
        _udp.EnableBroadcast = true;

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        // Im Thread-Pool laufen lassen (nicht auf dem UI-Thread), damit Fehler in der
        // Oberfläche die Netzwerkschleifen nie beeinflussen können
        _ = Task.Run(() => ReceiveLoopAsync(ct));
        _ = Task.Run(() => AnnounceLoopAsync(ct));
    }

    /// <summary>Sofort ein "hello" senden (z.B. nach Namensänderung).</summary>
    public void AnnounceNow() => _ = BroadcastAsync("hello");

    private async Task AnnounceLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await BroadcastAsync("hello");
                CheckTimeouts();
            }
            catch (Exception ex)
            {
                LogService.Error("Discovery/Announce", ex);
            }

            try
            {
                await Task.Delay(AnnounceInterval, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var selfId = _selfFactory().Id;
        while (!ct.IsCancellationRequested && _udp != null)
        {
            UdpReceiveResult result;
            try
            {
                result = await _udp.ReceiveAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException)
            {
                continue; // z.B. ICMP "Port unreachable"
            }
            catch (Exception ex)
            {
                LogService.Error("Discovery/Receive", ex);
                await Task.Delay(500, CancellationToken.None);
                continue;
            }

            try
            {
                HandleAnnouncement(result, selfId);
            }
            catch (Exception ex)
            {
                LogService.Error("Discovery/Handle", ex);
            }
        }
    }

    private void HandleAnnouncement(UdpReceiveResult result, string selfId)
    {
        {

            PeerAnnouncement? msg;
            try
            {
                msg = JsonSerializer.Deserialize<PeerAnnouncement>(Encoding.UTF8.GetString(result.Buffer));
            }
            catch
            {
                return;
            }

            if (msg == null || msg.App != AppTag || string.IsNullOrEmpty(msg.Id) || msg.Id == selfId || msg.Port <= 0)
                return;

            msg.Address = result.RemoteEndPoint.Address.ToString();

            if (msg.Type == "bye")
            {
                lock (_lock)
                    _lastSeen.Remove(msg.Id);
                PeerLost?.Invoke(this, msg.Id);
                return;
            }

            bool isNew;
            lock (_lock)
            {
                isNew = !_lastSeen.ContainsKey(msg.Id);
                _lastSeen[msg.Id] = DateTime.UtcNow;
            }

            // Neuen Peers direkt antworten, damit beide sich sofort sehen
            if (isNew && msg.Type == "hello")
                _ = SendToAsync("reply", new IPEndPoint(result.RemoteEndPoint.Address, DiscoveryPort));

            PeerSeen?.Invoke(this, msg);
        }
    }

    private void CheckTimeouts()
    {
        List<string> lost;
        lock (_lock)
        {
            var limit = DateTime.UtcNow - OfflineAfter;
            lost = _lastSeen.Where(kv => kv.Value < limit).Select(kv => kv.Key).ToList();
            foreach (var id in lost)
                _lastSeen.Remove(id);
        }
        foreach (var id in lost)
            PeerLost?.Invoke(this, id);
    }

    private async Task BroadcastAsync(string type)
    {
        foreach (var address in GetBroadcastAddresses())
            await SendToAsync(type, new IPEndPoint(address, DiscoveryPort));
    }

    private async Task SendToAsync(string type, IPEndPoint target)
    {
        var udp = _udp;
        if (udp == null)
            return;

        var self = _selfFactory();
        self.Type = type;
        var data = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(self));
        try
        {
            await udp.SendAsync(data, data.Length, target);
        }
        catch
        {
            // Netzwerk nicht verfügbar o.ä.
        }
    }

    /// <summary>
    /// Gerichtete Broadcast-Adressen aller aktiven IPv4-Netzwerkadapter + 255.255.255.255.
    /// (Der globale Broadcast geht unter Windows nur über einen Adapter hinaus.)
    /// </summary>
    private static IEnumerable<IPAddress> GetBroadcastAddresses()
    {
        var result = new HashSet<IPAddress> { IPAddress.Broadcast };
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up ||
                    nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;

                foreach (var ua in nic.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork || ua.IPv4Mask == null)
                        continue;

                    var ip = ua.Address.GetAddressBytes();
                    var mask = ua.IPv4Mask.GetAddressBytes();
                    var bc = new byte[4];
                    for (var i = 0; i < 4; i++)
                        bc[i] = (byte)(ip[i] | ~mask[i]);
                    result.Add(new IPAddress(bc));
                }
            }
        }
        catch
        {
            // ignorieren
        }
        return result;
    }

    public void Stop()
    {
        if (_udp == null)
            return;

        try
        {
            // Abmelden, damit andere uns sofort als offline sehen
            BroadcastAsync("bye").Wait(500);
        }
        catch
        {
            // ignorieren
        }

        _cts?.Cancel();
        _udp.Dispose();
        _udp = null;
    }

    public void Dispose() => Stop();
}
