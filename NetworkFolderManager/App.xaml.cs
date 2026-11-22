using NetworkFolderManager.Services;
using System.Threading;
using Application = System.Windows.Application;
using ExitEventArgs = System.Windows.ExitEventArgs;
using StartupEventArgs = System.Windows.StartupEventArgs;

namespace NetworkFolderManager;

/// <summary>
/// Interaktionslogik für "App.xaml"
///
/// Kommandozeile:
///   --minimized                     Im Infobereich starten (Autostart)
///   --setup-share "Pfad" "Name" ["Passwortdatei"]  Netzwerkordner + Zugang + Freigabe + Firewall (Admin)
///   --setup-firewall                Nur Firewall-Regeln anlegen (Admin)
///   --remove-share                  Freigabe + Firewall-Regel entfernen (Admin, Deinstallation)
///   --setup-webdav "Zertifikat" FP  Server-Zertifikat vertrauen + WebDAV-Client einrichten (Admin, für Netzlaufwerke)
/// </summary>
public partial class App : Application
{
    private static readonly string ShowEventName = @"Local\NetworkFolderManager_Show" + SettingsService.Profile;
    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _showEvent;

    protected override void OnStartup(StartupEventArgs e)
    {
        var args = e.Args;

        // ---- Admin-/Installer-Modi ohne Oberfläche ----
        if (args.Length >= 1)
        {
            switch (args[0].ToLowerInvariant())
            {
                case "--setup-share" when args.Length >= 3:
                    Shutdown(ShareSetupService.SetupShare(args[1], args[2], args.Length >= 4 ? args[3] : null));
                    return;
                case "--setup-firewall":
                    Shutdown(ShareSetupService.SetupFirewallOnly());
                    return;
                case "--remove-share":
                    Shutdown(ShareSetupService.RemoveShare());
                    return;
                case "--setup-webdav" when args.Length >= 3:
                    Shutdown(WebDavMountService.SetupElevated(args[1], args[2]));
                    return;
            }
        }

        // ---- Neustart (z.B. Sprachwechsel): warten, bis die alte Instanz beendet ist ----
        if (args.Length >= 2 && args[0] == "--after-restart" && int.TryParse(args[1], out var oldPid))
        {
            try
            {
                using var old = System.Diagnostics.Process.GetProcessById(oldPid);
                old.WaitForExit(15000);
            }
            catch
            {
                // bereits beendet
            }
            args = args.Skip(2).ToArray();
        }

        // ---- Nur eine Instanz: zweiter Start holt das vorhandene Fenster nach vorne ----
        _singleInstanceMutex = new Mutex(true, @"Local\NetworkFolderManager_SingleInstance" + SettingsService.Profile, out var isNew);
        if (!isNew)
        {
            try
            {
                if (EventWaitHandle.TryOpenExisting(ShowEventName, out var existing))
                {
                    existing.Set();
                    existing.Dispose();
                }
            }
            catch
            {
                // ignorieren
            }
            Shutdown();
            return;
        }

        base.OnStartup(e);
        Platform.Current = new WindowsPlatform();

        // Alle unbehandelten Fehler protokollieren (Details: %APPDATA%\NetworkFolderManager\error.log)
        AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
            LogService.Error("AppDomain", ex.ExceptionObject as Exception ?? new Exception(ex.ExceptionObject?.ToString()));
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, ex) =>
        {
            LogService.Error("Task", ex.Exception);
            ex.SetObserved();
        };
        DispatcherUnhandledException += (s, ex) =>
        {
            LogService.Error("UI", ex.Exception);
            System.Windows.MessageBox.Show(
                Loc.T("Ein unerwarteter Fehler ist aufgetreten:\n{0}: {1}\n\nDetails stehen in:\n{2}",
                    ex.Exception.GetType().Name, ex.Exception.Message, LogService.LogFile),
                Loc.T("Fehler"),
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
            ex.Handled = true;
        };

        SessionEnding += (_, _) => NotificationService.MarkExiting();
        NotificationService.Initialize();

        var window = new MainWindow();
        MainWindow = window;

        var startMinimized = args.Any(a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase));
        if (startMinimized)
            window.StartHidden();
        else
            window.Show();

        // Signal einer zweiten Instanz → Fenster anzeigen
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        var showThread = new Thread(() =>
        {
            while (_showEvent.WaitOne())
            {
                if (Dispatcher.HasShutdownStarted)
                    break;
                Dispatcher.BeginInvoke(window.RestoreFromTray);
            }
        })
        { IsBackground = true, Name = "ShowSignal" };
        showThread.Start();
    }

    /// <summary>Programm neu starten (z.B. damit eine geänderte Sprache wirkt).</summary>
    public static void Restart()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!)
            {
                Arguments = $"--after-restart {Environment.ProcessId}",
                UseShellExecute = false
            });
        }
        catch (Exception ex)
        {
            LogService.Error("Restart", ex);
            return;
        }
        NotificationService.MarkExiting();
        Current.Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        NotificationService.Shutdown();
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }
}
