using NetworkFolderManager.Models;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace NetworkFolderManager.Services;

/// <summary>
/// Sucht NFM-Server im lokalen Netz: sendet ein "query" per Broadcast (UDP 45823) und sammelt die Antworten.
/// </summary>
public static class ServerDiscoveryClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task<List<ServerAnnouncement>> SearchAsync(TimeSpan duration, CancellationToken ct = default)
    {
        var found = new Dictionary<string, ServerAnnouncement>();
        using var udp = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));

        var query = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new ServerAnnouncement { Type = "query" }, Json));
        async Task SendQueriesAsync()
        {
            foreach (var address in BroadcastAddresses())
            {
                try
                {
                    await udp.SendAsync(query, query.Length, new IPEndPoint(address, VaultProtocol.DiscoveryPort));
                }
                catch
                {
                    // Adapter nicht verfügbar
                }
            }
            try
            {
                // Server auf diesem PC
                await udp.SendAsync(query, query.Length, new IPEndPoint(IPAddress.Loopback, VaultProtocol.DiscoveryPort));
            }
            catch
            {
                // ignorieren
            }
        }

        await SendQueriesAsync();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(duration);
        var resent = false;
        var started = DateTime.UtcNow;

        while (!timeout.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try
            {
                // Nach der halben Zeit nochmal fragen (UDP kann verloren gehen)
                if (!resent && DateTime.UtcNow - started > duration / 2)
                {
                    resent = true;
                    await SendQueriesAsync();
                }
                result = await udp.ReceiveAsync(timeout.Token);
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
                var msg = JsonSerializer.Deserialize<ServerAnnouncement>(result.Buffer, Json);
                if (msg is not { App: VaultProtocol.AppTag, Type: "announce" } || string.IsNullOrEmpty(msg.Id) || msg.Port <= 0)
                    continue;
                var address = result.RemoteEndPoint.Address;
                // Eigener PC → lieber eine echte Netzwerkadresse als 127.0.0.1 verwenden
                msg.Address = IPAddress.IsLoopback(address) && found.ContainsKey(msg.Id) ? found[msg.Id].Address : address.ToString();
                if (!found.TryGetValue(msg.Id, out var existing) || IPAddress.IsLoopback(IPAddress.Parse(existing.Address)))
                    found[msg.Id] = msg;
            }
            catch
            {
                // ungültiges Paket
            }
        }
        return found.Values.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static IEnumerable<IPAddress> BroadcastAddresses()
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
