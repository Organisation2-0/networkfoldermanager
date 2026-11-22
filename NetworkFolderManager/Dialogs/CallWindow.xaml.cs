using Microsoft.Web.WebView2.Core;
using NetworkFolderManager.Services;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;

namespace NetworkFolderManager.Dialogs;

/// <summary>
/// Anruf-Fenster: hostet die Oberfläche (Assets/call.html) in WebView2. Kamera, Mikrofon, Bildschirmaufnahme,
/// Kodierung und die direkte WebRTC-Verbindung übernimmt der Chromium-Kern (Hardware-Encoder, bis 1440p60).
/// </summary>
public partial class CallWindow : Window
{
    private const string Host = "https://nfm-call.local/";
    private static CoreWebView2Environment? _environment;

    private readonly List<string> _pendingToPage = new();
    private bool _pageReady;
    private bool _closedByApp;
    private WindowState _stateBeforeFullscreen;
    private WindowStyle _styleBeforeFullscreen;

    /// <summary>Nachricht der Seite: Typ ("ready", "signal", "state", "warn", "error", "hangup") und JSON-Inhalt.</summary>
    public event Action<string, JsonElement>? PageEvent;

    /// <summary>Fenster wurde vom Benutzer geschlossen (= Auflegen).</summary>
    public event Action? ClosedByUser;

    public CallWindow(string title)
    {
        InitializeComponent();
        Title = title;
        Loaded += async (_, _) => await InitializeWebAsync();
        Closed += (_, _) =>
        {
            if (!_closedByApp)
                ClosedByUser?.Invoke();
        };
    }

    /// <summary>Schließt das Fenster ohne "ClosedByUser" auszulösen.</summary>
    public void CloseFromApp()
    {
        _closedByApp = true;
        try { Post(new { cmd = "stop" }); } catch { /* ignorieren */ }
        Close();
    }

    /// <summary>Sendet eine Nachricht an die Seite (wird gepuffert, bis die Seite bereit ist).</summary>
    public void Post(object message)
    {
        var json = JsonSerializer.Serialize(message);
        if (_pageReady && Web.CoreWebView2 != null)
            Web.CoreWebView2.PostWebMessageAsJson(json);
        else
            _pendingToPage.Add(json);
    }

    private async Task InitializeWebAsync()
    {
        try
        {
            if (_environment == null)
            {
                var options = new CoreWebView2EnvironmentOptions(
                    // Lokale IP-Adressen nicht hinter mDNS-Namen verstecken (direkte Verbindung im LAN),
                    // Encoder nicht künstlich wegen CPU-Last herunterregeln.
                    "--disable-features=WebRtcHideLocalIpsWithMdns --webrtc-max-cpu-consumption-percentage=100");
                _environment = await CoreWebView2Environment.CreateAsync(
                    null, Path.Combine(SettingsService.SettingsDir, "webview2"), options);
            }

            await Web.EnsureCoreWebView2Async(_environment);
        }
        catch (Exception ex)
        {
            LogService.Error("Call/WebView2", ex);
            System.Windows.MessageBox.Show(
                Loc.T("Für Anrufe wird die Microsoft WebView2-Laufzeit benötigt (bei Windows 11 enthalten). Download: https://go.microsoft.com/fwlink/p/?LinkId=2124703") +
                "\n\n" + ex.Message,
                Loc.T("Anruf nicht möglich"), MessageBoxButton.OK, MessageBoxImage.Warning);
            Close();
            return;
        }

        var core = Web.CoreWebView2;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;

        // Die Seite kommt aus den eingebetteten Ressourcen; sonst darf nichts geladen werden.
        core.AddWebResourceRequestedFilter(Host + "*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += OnWebResourceRequested;
        core.NavigationStarting += (_, e) =>
        {
            if (!e.Uri.StartsWith(Host, StringComparison.Ordinal))
                e.Cancel = true;
        };
        core.NewWindowRequested += (_, e) => e.Handled = true;
        core.PermissionRequested += (_, e) =>
        {
            e.State = e.PermissionKind is CoreWebView2PermissionKind.Microphone
                or CoreWebView2PermissionKind.Camera
                or CoreWebView2PermissionKind.Autoplay
                ? CoreWebView2PermissionState.Allow
                : CoreWebView2PermissionState.Deny;
        };
        core.ContainsFullScreenElementChanged += (_, _) => SetFullscreen(core.ContainsFullScreenElement);
        core.WebMessageReceived += OnWebMessage;

        core.Navigate(Host + "call.html");
    }

    private void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        if (!e.Request.Uri.Equals(Host + "call.html", StringComparison.Ordinal))
        {
            e.Response = _environment!.CreateWebResourceResponse(null, 404, "Not Found", "");
            return;
        }

        var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("call.html");
        e.Response = _environment!.CreateWebResourceResponse(
            stream, 200, "OK", "Content-Type: text/html; charset=utf-8\r\nCache-Control: no-store");
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            var evt = root.GetProperty("evt").GetString() ?? string.Empty;

            if (evt == "ready")
            {
                _pageReady = true;
                foreach (var json in _pendingToPage)
                    Web.CoreWebView2.PostWebMessageAsJson(json);
                _pendingToPage.Clear();
            }

            PageEvent?.Invoke(evt, root.Clone());
        }
        catch (Exception ex)
        {
            LogService.Error("Call/WebMessage", ex);
        }
    }

    private void SetFullscreen(bool on)
    {
        if (on)
        {
            _stateBeforeFullscreen = WindowState;
            _styleBeforeFullscreen = WindowStyle;
            WindowStyle = WindowStyle.None;
            WindowState = WindowState.Maximized;
        }
        else
        {
            WindowStyle = _styleBeforeFullscreen;
            WindowState = _stateBeforeFullscreen;
        }
    }
}
