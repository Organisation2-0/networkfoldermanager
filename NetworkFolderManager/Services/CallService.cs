using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using NetworkFolderManager.Dialogs;
using NetworkFolderManager.Models;
using NetworkFolderManager.ViewModels;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Threading;

namespace NetworkFolderManager.Services;

/// <summary>
/// Kamera- und Bildschirm-Anrufe (Windows). Die Verbindung selbst ist WebRTC, direkt von PC zu PC
/// (ohne STUN/TURN-Server, Video/Audio mit DTLS-SRTP verschlüsselt). Das Aushandeln (Klingeln, Annehmen,
/// WebRTC-Angebot/-Antwort) läuft über den bestehenden TLS-Chatkanal, dessen Zertifikate bereits
/// gegenseitig per Fingerabdruck geprüft werden – die Medienschlüssel sind damit an den Kontakt gebunden.
///
/// Signale (JSON in <see cref="ChatEnvelope.Text"/>): invite | accept | decline | busy | sdp | hangup.
/// </summary>
public sealed partial class CallService : ObservableObject, IDisposable
{
    private static readonly TimeSpan RingTimeout = TimeSpan.FromSeconds(45);

    private sealed class Session
    {
        public required string Id { get; init; }
        public required Peer Peer { get; init; }
        public required bool Outgoing { get; init; }
        public required string Kind { get; init; } // camera | screen
        public CallWindow? Window { get; set; }
        public IncomingCallWindow? Incoming { get; set; }
        public DispatcherTimer? RingTimer { get; set; }
        public bool Accepted { get; set; }
        public string? HeldSdp { get; set; } // Angebot des Anrufers, bis der Angerufene angenommen hat
    }

    private readonly ChatViewModel _chat;
    private readonly AppSettings _settings;
    private readonly RingtoneService _ringtone = new();
    private Session? _call;

    public CallService(ChatViewModel chat, AppSettings settings)
    {
        _chat = chat;
        _settings = settings;
        _ringtonePath = settings.RingtonePath;
        _ringtoneVolume = Math.Clamp(settings.RingtoneVolume, 0, 100);
        _chat.CallSignalReceived += (_, e) => OnSignal(e.Peer, e.Json);
    }

    #region Klingelton-Einstellungen

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RingtoneDisplay))]
    private string _ringtonePath;

    [ObservableProperty]
    private int _ringtoneVolume;

    public string RingtoneDisplay => string.IsNullOrEmpty(RingtonePath)
        ? Loc.T("Standard-Klingelton")
        : Path.GetFileName(RingtonePath);

    partial void OnRingtonePathChanged(string value)
    {
        _settings.RingtonePath = value ?? string.Empty;
        SettingsService.Save(_settings);
    }

    partial void OnRingtoneVolumeChanged(int value)
    {
        _settings.RingtoneVolume = value;
        SettingsService.Save(_settings);
    }

    private static string? PickAudioFile(string? current)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = Loc.T("Klingelton wählen"),
            Filter = Loc.T("Audiodateien") + "|*.mp3;*.wav;*.wma;*.m4a;*.aac;*.flac|" + Loc.T("Alle Dateien (*.*)") + "|*.*",
            CheckFileExists = true
        };
        if (!string.IsNullOrEmpty(current) && File.Exists(current))
            dialog.InitialDirectory = Path.GetDirectoryName(current);
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    [RelayCommand]
    private void BrowseRingtone()
    {
        if (PickAudioFile(RingtonePath) is { } file)
        {
            RingtonePath = file;
            TestRingtone();
        }
    }

    [RelayCommand]
    private void ResetRingtone()
    {
        RingtonePath = string.Empty;
        _ringtone.Stop();
    }

    /// <summary>Klingelton kurz anhören (erneuter Klick stoppt ihn).</summary>
    [RelayCommand]
    private void TestRingtone()
    {
        if (_ringtone.IsPlaying)
        {
            _ringtone.Stop();
            return;
        }
        PlayRingtone(RingtonePath);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (_call == null)
                _ringtone.Stop();
        };
        timer.Start();
    }

    /// <summary>Eigenen Klingelton für den ausgewählten Kontakt festlegen.</summary>
    [RelayCommand]
    private void ChoosePeerRingtone()
    {
        if (_chat.SelectedPeer is not { } peer)
            return;
        if (PickAudioFile(peer.RingtonePath) is { } file)
        {
            peer.RingtonePath = file;
            _chat.SavePeer(peer);
            PlayRingtone(file);
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                if (_call == null)
                    _ringtone.Stop();
            };
            timer.Start();
        }
    }

    [RelayCommand]
    private void ResetPeerRingtone()
    {
        if (_chat.SelectedPeer is not { } peer)
            return;
        peer.RingtonePath = null;
        _chat.SavePeer(peer);
    }

    private void PlayRingtone(string? path) => _ringtone.Play(path, RingtoneVolume);

    #endregion

    #region Anrufen

    /// <param name="kind">"camera" (Videoanruf) oder "screen" (Bildschirm teilen)</param>
    [RelayCommand]
    private async Task StartCallAsync(string? kind)
    {
        if (_chat.SelectedPeer is not { } peer)
            return;
        kind = kind == "screen" ? "screen" : "camera";

        if (_call != null)
        {
            Platform.Current.Notify(Loc.T("Anruf"), Loc.T("Es läuft bereits ein Anruf."));
            return;
        }
        if (!peer.IsOnline || peer.KeyChanged)
        {
            await Platform.Current.ConfirmAsync(
                peer.KeyChanged
                    ? Loc.T("Der Sicherheitsschlüssel von {0} hat sich geändert. Bitte zuerst im Chat prüfen und akzeptieren.", peer.DisplayName)
                    : Loc.T("{0} ist gerade nicht online.", peer.DisplayName),
                Loc.T("Anruf nicht möglich"));
            return;
        }

        var session = new Session { Id = Guid.NewGuid().ToString("N"), Peer = peer, Outgoing = true, Kind = kind };
        _call = session;
        OpenCallWindow(session);
        session.Window!.Post(StartMessage(session, "caller", kind == "screen" ? "screen" : "camera"));

        StartRingTimer(session, () => EndCall(session, Loc.T("{0} hat nicht geantwortet.", peer.DisplayName), notifyPeer: true));

        try
        {
            await _chat.SendCallSignalAsync(peer, Signal(session, "invite", ("kind", kind)));
        }
        catch (Exception ex)
        {
            LogService.Info($"Anruf: {peer.DisplayName} nicht erreichbar: {ex.GetBaseException().Message}");
            EndCall(session, Loc.T("{0} ist nicht erreichbar.", peer.DisplayName), notifyPeer: false);
        }
    }

    private static object StartMessage(Session s, string role, string source) => new
    {
        cmd = "start",
        role,
        source,
        peerName = s.Peer.DisplayName,
        t = new Dictionary<string, string>
        {
            ["mic"] = Loc.T("Mikrofon"),
            ["micOff"] = Loc.T("Stumm"),
            ["camera"] = Loc.T("Kamera"),
            ["screen"] = Loc.T("Bildschirm teilen"),
            ["stopScreen"] = Loc.T("Teilen beenden"),
            ["fullscreen"] = Loc.T("Vollbild"),
            ["hangup"] = Loc.T("Auflegen"),
            ["quality"] = Loc.T("Qualität"),
            ["waiting"] = Loc.T("Klingelt … warte auf Antwort"),
            ["connecting"] = Loc.T("Verbinde …"),
            ["reconnecting"] = Loc.T("Verbindung wird wiederhergestellt …"),
            ["noMic"] = Loc.T("Kein Mikrofon verfügbar"),
            ["noCam"] = Loc.T("Kamera nicht verfügbar"),
            ["noScreen"] = Loc.T("Bildschirmfreigabe abgebrochen"),
        }
    };

    private void OpenCallWindow(Session s)
    {
        var window = new CallWindow(Loc.T("Anruf – {0}", s.Peer.DisplayName));
        s.Window = window;
        window.PageEvent += (evt, data) => OnPageEvent(s, evt, data);
        window.ClosedByUser += () =>
        {
            s.Window = null;
            EndCall(s, null, notifyPeer: true);
        };
        window.Show();
    }

    #endregion

    #region Signale

    private static string Signal(Session s, string type, params (string Key, string Value)[] extra)
    {
        var obj = new JsonObject { ["t"] = type, ["id"] = s.Id };
        foreach (var (key, value) in extra)
            obj[key] = value;
        return obj.ToJsonString();
    }

    private void SendSignal(Session s, string json) => SendSignal(s.Peer, json);

    private void SendSignal(Peer peer, string json)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await _chat.SendCallSignalAsync(peer, json);
            }
            catch (Exception ex)
            {
                LogService.Info($"Anruf: Signal an {peer.DisplayName} fehlgeschlagen: {ex.GetBaseException().Message}");
            }
        });
    }

    private void OnSignal(Peer peer, string json)
    {
        JsonNode? node;
        try { node = JsonNode.Parse(json); }
        catch { return; }

        var type = node?["t"]?.GetValue<string>();
        var id = node?["id"]?.GetValue<string>();
        if (node == null || string.IsNullOrEmpty(type) || string.IsNullOrEmpty(id))
            return;

        if (type == "invite")
        {
            OnInvite(peer, id, node["kind"]?.GetValue<string>() == "screen" ? "screen" : "camera");
            return;
        }

        // Alle anderen Signale müssen zum aktuellen Anruf mit genau diesem Kontakt gehören
        var s = _call;
        if (s == null || s.Id != id || s.Peer.Id != peer.Id)
            return;

        switch (type)
        {
            case "accept" when s.Outgoing:
                s.Accepted = true;
                s.RingTimer?.Stop();
                if (s.HeldSdp != null)
                {
                    SendSignal(s, s.HeldSdp);
                    s.HeldSdp = null;
                }
                break;

            case "decline" when s.Outgoing:
                EndCall(s, Loc.T("{0} hat den Anruf abgelehnt.", peer.DisplayName), notifyPeer: false);
                break;

            case "busy" when s.Outgoing:
                EndCall(s, Loc.T("{0} telefoniert gerade.", peer.DisplayName), notifyPeer: false);
                break;

            case "sdp":
                s.Window?.Post(new { cmd = "signal", data = JsonSerializer.Deserialize<JsonElement>(json) });
                break;

            case "hangup":
                EndCall(s, !s.Outgoing && s.Incoming != null
                    ? Loc.T("Verpasster Anruf von {0}.", peer.DisplayName)
                    : Loc.T("Anruf beendet."), notifyPeer: false);
                break;
        }
    }

    private void OnInvite(Peer peer, string id, string kind)
    {
        if (peer.KeyChanged)
            return;

        if (_call != null)
        {
            // besetzt (oder derselbe Anruf wurde doppelt zugestellt)
            if (_call.Id != id)
                SendSignal(peer, new JsonObject { ["t"] = "busy", ["id"] = id }.ToJsonString());
            return;
        }

        var s = new Session { Id = id, Peer = peer, Outgoing = false, Kind = kind };
        _call = s;

        PlayRingtone(string.IsNullOrEmpty(peer.RingtonePath) ? RingtonePath : peer.RingtonePath);
        Platform.Current.Notify(peer.DisplayName,
            kind == "screen" ? Loc.T("🖥 möchte seinen Bildschirm teilen") : Loc.T("📷 Videoanruf"), peer.Id);

        var incoming = new IncomingCallWindow(peer.DisplayName, kind == "screen");
        s.Incoming = incoming;
        incoming.Answered += accepted =>
        {
            s.Incoming = null;
            if (_call != s)
                return;
            if (accepted)
                AcceptCall(s);
            else
                EndCall(s, null, notifyPeer: false, declined: true);
        };
        incoming.Show();
        incoming.Activate();

        StartRingTimer(s, () => EndCall(s, Loc.T("Verpasster Anruf von {0}.", peer.DisplayName), notifyPeer: false, declined: true));
    }

    private void AcceptCall(Session s)
    {
        s.Accepted = true;
        s.RingTimer?.Stop();
        _ringtone.Stop();

        OpenCallWindow(s);
        // Bei einem Videoanruf Kamera mitsenden; bei Bildschirmfreigabe zuerst nur zuschauen
        s.Window!.Post(StartMessage(s, "callee", s.Kind == "camera" ? "camera" : "none"));
        SendSignal(s, Signal(s, "accept"));
    }

    private void OnPageEvent(Session s, string evt, JsonElement data)
    {
        if (_call != s)
            return;

        switch (evt)
        {
            case "signal":
                var payload = JsonNode.Parse(data.GetProperty("data").GetRawText())!.AsObject();
                payload["id"] = s.Id;
                var json = payload.ToJsonString();
                // Das Angebot des Anrufers erst senden, wenn der Angerufene angenommen hat
                if (s.Outgoing && !s.Accepted)
                    s.HeldSdp = json;
                else
                    SendSignal(s, json);
                break;

            case "hangup":
                EndCall(s, null, notifyPeer: true);
                break;

            case "state" when data.TryGetProperty("state", out var st) && st.GetString() == "failed":
                EndCall(s, Loc.T("Verbindung zu {0} fehlgeschlagen. Ist die Windows-Firewall für Edge WebView2 (privates Netzwerk) freigegeben?", s.Peer.DisplayName), notifyPeer: true);
                break;

            case "warn" or "error":
                var message = data.TryGetProperty("message", out var m) ? m.GetString() : null;
                LogService.Info($"Anruf ({evt}): {message}");
                if (evt == "error")
                    EndCall(s, Loc.T("Anruf-Fehler: {0}", message ?? ""), notifyPeer: true);
                else if (!string.IsNullOrEmpty(message))
                    Platform.Current.Notify(Loc.T("Anruf"), message);
                break;
        }
    }

    #endregion

    #region Beenden

    private void StartRingTimer(Session s, Action onTimeout)
    {
        s.RingTimer = new DispatcherTimer { Interval = RingTimeout };
        s.RingTimer.Tick += (_, _) =>
        {
            s.RingTimer?.Stop();
            if (!s.Accepted)
                onTimeout();
        };
        s.RingTimer.Start();
    }

    private void EndCall(Session s, string? message, bool notifyPeer, bool declined = false)
    {
        if (_call != s)
            return;
        _call = null;

        s.RingTimer?.Stop();
        _ringtone.Stop();

        if (notifyPeer)
            SendSignal(s, Signal(s, "hangup"));
        else if (declined)
            SendSignal(s, Signal(s, "decline"));

        var incoming = s.Incoming;
        s.Incoming = null;
        incoming?.Close();

        var window = s.Window;
        s.Window = null;
        window?.CloseFromApp();

        if (!string.IsNullOrEmpty(message))
            Platform.Current.Notify(Loc.T("Anruf"), message);
    }

    public void Dispose()
    {
        if (_call is { } s)
            EndCall(s, null, notifyPeer: true);
        _ringtone.Dispose();
    }

    #endregion
}
