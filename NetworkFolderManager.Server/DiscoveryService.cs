using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using NetworkFolderManager.Models;

namespace NetworkFolderManager.Server;

/// <summary>
/// Server-Erkennung im lokalen Netz (UDP 45823):
///  - Clients senden ein "query" als Broadcast → jeder Server antwortet direkt mit "announce".
///  - Server melden sich zusätzlich alle 30 s per Broadcast, damit sie sich gegenseitig kennen
///    (die Web-App zeigt so andere Server im Netzwerk an).
/// </summary>
public sealed class DiscoveryService : BackgroundService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly ServerState _state;
    private readonly ILogger<DiscoveryService> _log;
    private readonly ConcurrentDictionary<string, (ServerAnnouncement Info, DateTime Seen)> _known = new();

    public DiscoveryService(ServerState state, ILogger<DiscoveryService> log)
    {
        _state = state;
        _log = log;
        state.Discovery = this;
    }

    public IReadOnlyList<ServerAnnouncement> KnownServers()
    {
        var limit = DateTime.UtcNow - TimeSpan.FromMinutes(2);
        return _known.Values.Where(v => v.Seen > limit).Select(v => v.Info).OrderBy(s => s.Name).ToList();
    }

    private ServerAnnouncement Self(string type) => new()
    {
        Type = type,
        Id = _state.ServerId,
        Name = _state.ServerName,
        Port = _state.Port,
        Fp = _state.Certificates.Fingerprint,
        Version = ServerState.Version
    };

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!_state.DiscoveryEnabled)
        {
            _log.LogInformation("Server-Erkennung ist ausgeschaltet.");
            return;
        }

        UdpClient udp;
        try
        {
            udp = new UdpClient(AddressFamily.InterNetwork);
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, VaultProtocol.DiscoveryPort));
            udp.EnableBroadcast = true;
        }
        catch (Exception ex)
        {
            _log.LogWarning("Server-Erkennung (UDP {Port}) nicht verfügbar: {Message}", VaultProtocol.DiscoveryPort, ex.Message);
            return;
        }

        using (udp)
        {
            _ = Task.Run(() => AnnounceLoopAsync(udp, ct), ct);
            while (!ct.IsCancellationRequested)
            {
                UdpReceiveResult result;
                try
                {
                    result = await udp.ReceiveAsync(ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (SocketException)
                {
                    continue;
                }

                try
                {
                    await HandleAsync(udp, result);
                }
                catch (Exception ex)
                {
                    _log.LogDebug(ex, "Discovery-Paket ignoriert");
                }
            }
        }
    }

    private async Task HandleAsync(UdpClient udp, UdpReceiveResult result)
    {
        if (result.Buffer.Length > 4096)
            return;
        var msg = JsonSerializer.Deserialize<ServerAnnouncement>(result.Buffer, Json);
        if (msg == null || msg.App != VaultProtocol.AppTag)
            return;

        if (msg.Type == "query")
        {
            var data = JsonSerializer.SerializeToUtf8Bytes(Self("announce"), Json);
            await udp.SendAsync(data, data.Length, result.RemoteEndPoint);
            return;
        }

        if (msg.Type == "announce" && msg.Id != _state.ServerId && !string.IsNullOrEmpty(msg.Id) && msg.Port > 0)
        {
            msg.Address = result.RemoteEndPoint.Address.ToString();
            msg.Name = msg.Name.Length > 64 ? msg.Name[..64] : msg.Name;
            _known[msg.Id] = (msg, DateTime.UtcNow);
        }
    }

    private async Task AnnounceLoopAsync(UdpClient udp, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var data = JsonSerializer.SerializeToUtf8Bytes(Self("announce"), Json);
                foreach (var address in BroadcastAddresses())
                    await udp.SendAsync(data, data.Length, new IPEndPoint(address, VaultProtocol.DiscoveryPort));
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Announce fehlgeschlagen");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public static IEnumerable<IPAddress> BroadcastAddresses()
    {
        var result = new HashSet<IPAddress> { IPAddress.Broadcast };
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
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
}
