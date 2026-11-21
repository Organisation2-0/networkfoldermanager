using NetworkFolderManager.Models;
using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace NetworkFolderManager.Services;

/// <summary>
/// Kopf einer Chat-Übertragung. Bei Bildern/Dateien folgen danach genau <see cref="FileSize"/> Bytes.
/// </summary>
public class ChatEnvelope
{
    public string Type { get; set; } = "msg"; // msg | ack
    public string Id { get; set; } = string.Empty;
    public string From { get; set; } = string.Empty;
    public string FromName { get; set; } = string.Empty;
    public string FromMachine { get; set; } = string.Empty;
    public int FromPort { get; set; }
    public MessageKind Kind { get; set; }
    public string Text { get; set; } = string.Empty;
    public string? FileName { get; set; }
    public long FileSize { get; set; }
    public DateTime Timestamp { get; set; }
    public bool Ok { get; set; }
    public string? Error { get; set; }
}

/// <summary>
/// Ein empfangene Nachricht inkl. Absenderdaten.
/// </summary>
public class ChatReceivedEventArgs : EventArgs
{
    public required ChatEnvelope Envelope { get; init; }
    public required string SenderAddress { get; init; }
    public required ChatMessage Message { get; init; }
    public required string SenderFingerprint { get; init; }
}

/// <summary>
/// Direkte, verschlüsselte Peer-to-Peer-Übertragung von Nachrichten, Bildern und Dateien im lokalen Netzwerk.
/// Jede Verbindung ist TLS-verschlüsselt (gegenseitige Authentifizierung mit den Zertifikaten beider Seiten,
/// Fingerabdrücke werden beim ersten Kontakt gemerkt und danach geprüft).
/// Protokoll je Verbindung (innerhalb von TLS): [Int32 Länge][JSON-Kopf][Dateibytes] → [Int32 Länge][JSON-Ack].
/// </summary>
public sealed class ChatService : IDisposable
{
    public const int PreferredPort = 45822;
    public const long MaxFileSize = 4L * 1024 * 1024 * 1024; // 4 GB
    private const int MaxHeaderSize = 256 * 1024;
    private const int BufferSize = 256 * 1024;
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(30);

    private readonly Func<ChatEnvelope> _senderInfo;
    private readonly X509Certificate2 _certificate;
    private readonly Func<string, string, bool> _verifySender;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;

    /// <summary>Standard: neuer Ordner "NetworkFolderManager" in den Dokumenten des Benutzers.</summary>
    public static string DefaultReceiveFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "NetworkFolderManager" + (SettingsService.Profile.Length > 0 ? "-" + SettingsService.Profile : ""));

    /// <summary>Ordner für empfangene Bilder/Dateien (in den Einstellungen bzw. im Installer wählbar).</summary>
    public static string ReceiveFolder { get; set; } = DefaultReceiveFolder;

    /// <summary>Tatsächlich verwendeter TCP-Port.</summary>
    public int Port { get; private set; }

    public event EventHandler<ChatReceivedEventArgs>? MessageReceived;

    /// <param name="senderInfo">Liefert die eigenen Absenderdaten</param>
    /// <param name="certificate">Eigenes TLS-Zertifikat</param>
    /// <param name="verifySender">Prüft (Peer-ID, Fingerabdruck) eines eingehenden Absenders</param>
    public ChatService(Func<ChatEnvelope> senderInfo, X509Certificate2 certificate, Func<string, string, bool> verifySender)
    {
        _senderInfo = senderInfo;
        _certificate = certificate;
        _verifySender = verifySender;
    }

    /// <summary>Fingerabdruck des eigenen Zertifikats.</summary>
    public string OwnFingerprint => CertificateService.Fingerprint(_certificate);

    public void Start()
    {
        if (_listener != null)
            return;

        // Bevorzugten Port nehmen, sonst den nächsten freien (z.B. zwei Benutzer auf einem PC)
        for (var port = PreferredPort; port < PreferredPort + 10 && _listener == null; port++)
        {
            try
            {
                var l = new TcpListener(IPAddress.Any, port);
                l.Server.ExclusiveAddressUse = true;
                l.Start();
                _listener = l;
                Port = port;
            }
            catch (SocketException)
            {
                // belegt
            }
        }

        if (_listener == null)
        {
            _listener = new TcpListener(IPAddress.Any, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        }

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _ = Task.Run(() => AcceptLoopAsync(ct));
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener != null)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(ct);
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
                continue;
            }
            catch (Exception ex)
            {
                LogService.Error("Chat/Accept", ex);
                continue;
            }

            _ = Task.Run(() => HandleClientAsync(client, ct));
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken appCt)
    {
        using var _ = client;
        var remote = ((IPEndPoint)client.Client.RemoteEndPoint!).Address.ToString();
        string? partPath = null;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(appCt);
        timeout.CancelAfter(IdleTimeout);

        // TLS: Zertifikate werden hier nur angenommen; die Identität wird unten per Fingerabdruck geprüft
        await using var stream = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
        try
        {
            await stream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = _certificate,
                ClientCertificateRequired = true,
                RemoteCertificateValidationCallback = (_, cert, _, _) => cert != null,
                CertificateRevocationCheckMode = System.Security.Cryptography.X509Certificates.X509RevocationMode.NoCheck
            }, timeout.Token);
        }
        catch (Exception ex)
        {
            LogService.Info($"Chat: TLS-Handshake mit {remote} fehlgeschlagen: {ex.GetBaseException().Message}");
            return; // kein TLS / ungültiger Partner
        }

        ChatEnvelope? env = null;
        try
        {
            env = await ReadEnvelopeAsync(stream, timeout.Token);
            if (env == null || env.Type != "msg" || string.IsNullOrEmpty(env.From) || string.IsNullOrEmpty(env.Id))
                return;

            var senderFingerprint = CertificateService.Fingerprint(stream.RemoteCertificate);
            if (!_verifySender(env.From, senderFingerprint))
                throw new System.Security.SecurityException(Loc.T("Sicherheitsprüfung fehlgeschlagen: Der Absender hat einen anderen Schlüssel als bisher."));

            var message = new ChatMessage
            {
                Id = env.Id,
                Outgoing = false,
                Kind = env.Kind,
                Text = env.Text ?? string.Empty,
                FileName = env.FileName,
                FileSize = env.FileSize,
                Timestamp = env.Timestamp == default ? DateTime.Now : env.Timestamp.ToLocalTime(),
                Status = MessageStatus.Received
            };

            if (env.Kind != MessageKind.Text)
            {
                if (env.FileSize < 0 || env.FileSize > MaxFileSize)
                    throw new InvalidDataException(Loc.T("Datei ist zu groß."));

                var target = GetUniquePath(Path.Combine(ReceiveFolder, SanitizeFileName(env.FileName)));
                Directory.CreateDirectory(ReceiveFolder);
                partPath = target + ".part";

                await using (var file = new FileStream(partPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, true))
                {
                    var buffer = new byte[BufferSize];
                    var remaining = env.FileSize;
                    while (remaining > 0)
                    {
                        timeout.CancelAfter(IdleTimeout); // Leerlauf-Timeout pro Block
                        var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), timeout.Token);
                        if (read == 0)
                            throw new EndOfStreamException(Loc.T("Verbindung während der Übertragung getrennt."));
                        await file.WriteAsync(buffer.AsMemory(0, read), timeout.Token);
                        remaining -= read;
                    }
                }

                File.Move(partPath, target);
                partPath = null;
                message.FilePath = target;
                message.FileName = Path.GetFileName(target);
            }

            await WriteEnvelopeAsync(stream, new ChatEnvelope { Type = "ack", Id = env.Id, Ok = true }, timeout.Token);

            MessageReceived?.Invoke(this, new ChatReceivedEventArgs
            {
                Envelope = env,
                SenderAddress = remote,
                Message = message,
                SenderFingerprint = senderFingerprint
            });
        }
        catch (Exception ex)
        {
            LogService.Info($"Chat: Empfang von {remote} fehlgeschlagen: {ex.GetBaseException().Message}");
            if (env != null)
            {
                try
                {
                    await WriteEnvelopeAsync(stream, new ChatEnvelope { Type = "ack", Id = env.Id, Ok = false, Error = ex.Message }, CancellationToken.None);
                }
                catch
                {
                    // Verbindung bereits weg
                }
            }
        }
        finally
        {
            if (partPath != null)
            {
                try { File.Delete(partPath); } catch { /* ignorieren */ }
            }
        }
    }

    /// <summary>
    /// Sendet eine Nachricht an einen Peer. Wirft eine Exception, wenn der Peer nicht erreichbar ist
    /// oder die Nachricht ablehnt.
    /// </summary>
    public async Task SendAsync(Peer peer, ChatMessage message, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(peer.Address) || peer.Port <= 0)
            throw new InvalidOperationException(Loc.T("Empfänger ist nicht erreichbar."));

        FileStream? file = null;
        if (message.Kind != MessageKind.Text)
        {
            if (string.IsNullOrEmpty(message.FilePath) || !File.Exists(message.FilePath))
                throw new FileNotFoundException(Loc.T("Die Datei existiert nicht mehr."), message.FilePath);
            file = new FileStream(message.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, true);
            message.FileSize = file.Length;
        }

        await using var _ = file;
        using var client = new TcpClient(AddressFamily.InterNetwork);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));

        await client.ConnectAsync(IPAddress.Parse(peer.Address), peer.Port, timeout.Token);

        // TLS: Server muss genau den gemerkten Schlüssel des Empfängers haben
        var expected = peer.CertFingerprint;
        await using var stream = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
        var clientCerts = new X509CertificateCollection { _certificate };
        await stream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = "NetworkFolderManager",
            ClientCertificates = clientCerts,
            LocalCertificateSelectionCallback = (_, _, _, _, _) => _certificate,
            RemoteCertificateValidationCallback = (_, cert, _, _) =>
                cert != null && !string.IsNullOrEmpty(expected) &&
                string.Equals(CertificateService.Fingerprint(cert), expected, StringComparison.OrdinalIgnoreCase),
            CertificateRevocationCheckMode = System.Security.Cryptography.X509Certificates.X509RevocationMode.NoCheck
        }, timeout.Token);

        var env = _senderInfo();
        env.Type = "msg";
        env.Id = message.Id;
        env.Kind = message.Kind;
        env.Text = message.Text;
        env.FileName = message.FileName;
        env.FileSize = message.FileSize;
        env.Timestamp = message.Timestamp.ToUniversalTime();

        timeout.CancelAfter(IdleTimeout);
        await WriteEnvelopeAsync(stream, env, timeout.Token);

        if (file != null)
        {
            var buffer = new byte[BufferSize];
            long sent = 0;
            int read;
            while ((read = await file.ReadAsync(buffer, timeout.Token)) > 0)
            {
                timeout.CancelAfter(IdleTimeout);
                await stream.WriteAsync(buffer.AsMemory(0, read), timeout.Token);
                sent += read;
                progress?.Report(message.FileSize > 0 ? sent * 100.0 / message.FileSize : 100);
            }
        }
        await stream.FlushAsync(timeout.Token);

        timeout.CancelAfter(IdleTimeout);
        var ack = await ReadEnvelopeAsync(stream, timeout.Token);
        if (ack == null || ack.Type != "ack" || ack.Id != message.Id)
            throw new IOException(Loc.T("Keine Empfangsbestätigung erhalten."));
        if (!ack.Ok)
            throw new IOException(ack.Error ?? Loc.T("Empfänger hat die Nachricht abgelehnt."));
    }

    private static async Task WriteEnvelopeAsync(Stream stream, ChatEnvelope env, CancellationToken ct)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(env);
        var len = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(len, json.Length);
        await stream.WriteAsync(len, ct);
        await stream.WriteAsync(json, ct);
    }

    private static async Task<ChatEnvelope?> ReadEnvelopeAsync(Stream stream, CancellationToken ct)
    {
        var len = new byte[4];
        await stream.ReadExactlyAsync(len, ct);
        var size = BinaryPrimitives.ReadInt32LittleEndian(len);
        if (size <= 0 || size > MaxHeaderSize)
            throw new InvalidDataException(Loc.T("Ungültiger Nachrichtenkopf."));

        var data = new byte[size];
        await stream.ReadExactlyAsync(data, ct);
        return JsonSerializer.Deserialize<ChatEnvelope>(data);
    }

    /// <summary>
    /// Entfernt Pfadanteile und ungültige Zeichen aus einem fremden Dateinamen (Schutz vor "..\..\").
    /// </summary>
    public static string SanitizeFileName(string? name)
    {
        name = Path.GetFileName((name ?? string.Empty).Replace('/', '\\'));
        var invalid = Path.GetInvalidFileNameChars();
        name = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim(' ', '.');
        if (string.IsNullOrWhiteSpace(name))
            name = $"Datei_{DateTime.Now:yyyyMMdd_HHmmss}";
        return name.Length > 150 ? name[^150..] : name;
    }

    public static string GetUniquePath(string path)
    {
        if (!File.Exists(path) && !File.Exists(path + ".part"))
            return path;

        var dir = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 1; ; i++)
        {
            var candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(candidate) && !File.Exists(candidate + ".part"))
                return candidate;
        }
    }

    public void Stop()
    {
        _cts?.Cancel();
        _listener?.Stop();
        _listener = null;
    }

    public void Dispose() => Stop();
}
