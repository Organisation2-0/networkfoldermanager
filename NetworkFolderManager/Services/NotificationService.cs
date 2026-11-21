using System.Drawing;
using System.IO;
using System.Windows.Forms;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;

namespace NetworkFolderManager.Services;

/// <summary>
/// Zeigt Benachrichtigungen im Programm und als Windows-Benachrichtigung (über ein Tray-Icon).
/// Unter Windows 10/11 erscheinen Balloon-Tips als normale Toast-Benachrichtigung im Info-Center.
/// </summary>
public static class NotificationService
{
    private static NotifyIcon? _trayIcon;
    private static string? _lastClickPath;
    private static string? _lastChatPeerId;

    /// <summary>Klick auf eine Chat-Benachrichtigung (Parameter: Peer-ID).</summary>
    public static event EventHandler<string>? ChatNotificationClicked;

    /// <summary>True, sobald der Benutzer "Beenden" im Tray-Menü gewählt hat.</summary>
    public static bool IsExiting { get; private set; }

    /// <summary>Markiert das Programm als "wird beendet" (z.B. Windows-Abmeldung).</summary>
    public static void MarkExiting() => IsExiting = true;

    /// <summary>Wird ausgelöst, wenn das Tray-Icon doppelt geklickt wird (Fenster wiederherstellen).</summary>
    public static event EventHandler? RestoreRequested;

    /// <summary>
    /// Erstellt das Tray-Icon. Muss einmal auf dem UI-Thread aufgerufen werden.
    /// </summary>
    public static void Initialize()
    {
        if (_trayIcon != null)
            return;

        _trayIcon = new NotifyIcon
        {
            Icon = LoadAppIcon(),
            Text = "NetworkFolderManager",
            Visible = true
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add("Öffnen", null, (_, _) => RestoreRequested?.Invoke(null, EventArgs.Empty));
        menu.Items.Add("Beenden", null, (_, _) =>
        {
            IsExiting = true;
            Application.Current.Shutdown();
        });
        _trayIcon.ContextMenuStrip = menu;

        _trayIcon.DoubleClick += (_, _) => RestoreRequested?.Invoke(null, EventArgs.Empty);
        _trayIcon.BalloonTipClicked += (_, _) =>
        {
            // Klick auf die Benachrichtigung öffnet den Chat bzw. den Ordner mit markierter Datei
            if (_lastChatPeerId != null)
            {
                RestoreRequested?.Invoke(null, EventArgs.Empty);
                ChatNotificationClicked?.Invoke(null, _lastChatPeerId);
            }
            else if (_lastClickPath != null && File.Exists(_lastClickPath))
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{_lastClickPath}\"");
            else
                RestoreRequested?.Invoke(null, EventArgs.Empty);
        };
    }

    /// <summary>
    /// Zeigt eine Windows-Benachrichtigung an.
    /// </summary>
    /// <param name="title">Titel der Benachrichtigung</param>
    /// <param name="message">Nachrichtentext</param>
    /// <param name="filePath">Optional: Datei, die beim Klick im Explorer markiert wird</param>
    /// <param name="chatPeerId">Optional: Chat, der beim Klick geöffnet wird</param>
    public static void ShowToastNotification(string title, string message, string? filePath = null, string? chatPeerId = null)
    {
        if (_trayIcon == null)
        {
            ShowInApp(title, message);
            return;
        }

        _lastClickPath = filePath;
        _lastChatPeerId = chatPeerId;
        _trayIcon.ShowBalloonTip(5000, title, message, ToolTipIcon.Info);
    }

    /// <summary>
    /// Zeigt eine Benachrichtigung im Programm (als MessageBox).
    /// </summary>
    public static void ShowInApp(string title, string message, MessageBoxImage image = MessageBoxImage.Information)
    {
        Application.Current.Dispatcher.Invoke(() =>
            MessageBox.Show(message, title, MessageBoxButton.OK, image));
    }

    /// <summary>
    /// Entfernt das Tray-Icon (beim Beenden aufrufen, sonst bleibt ein "Geister-Icon").
    /// </summary>
    public static void Shutdown()
    {
        if (_trayIcon == null)
            return;

        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _trayIcon = null;
    }

    private static Icon LoadAppIcon()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (exe != null)
            {
                var icon = Icon.ExtractAssociatedIcon(exe);
                if (icon != null)
                    return icon;
            }
        }
        catch
        {
            // Fallback unten
        }
        return SystemIcons.Application;
    }
}
