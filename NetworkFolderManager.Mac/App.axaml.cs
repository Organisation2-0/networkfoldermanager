using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using NetworkFolderManager.Mac;
using NetworkFolderManager.Services;

namespace NetworkFolderManager;

public partial class App : Application
{
    private MainWindow? _window;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Der Chat soll auch bei geschlossenem Fenster weiterlaufen → nur über "Beenden" schließen
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            foreach (var (de, en) in MacStrings.Pairs)
                Loc.Extra[de] = en;
            Platform.Current = new MacPlatform();

            _window = new MainWindow();
            desktop.MainWindow = _window;
            if (!desktop.Args?.Contains("--minimized") ?? true)
                _window.Show();

            // Tray-Menü beschriften (Sprache)
            if (TrayIcon.GetIcons(this) is { Count: > 0 } icons && icons[0].Menu is { } menu)
            {
                ((NativeMenuItem)menu.Items[0]).Header = Loc.T("Öffnen");
                ((NativeMenuItem)menu.Items[2]).Header = Loc.T("Beenden");
            }

            // Klick auf das Dock-Symbol bei geschlossenem Fenster → Fenster wieder zeigen
            if (TryGetFeature(typeof(IActivatableLifetime)) is IActivatableLifetime activatable)
            {
                activatable.Activated += (_, e) =>
                {
                    if (e.Kind == ActivationKind.Reopen)
                        _window?.ShowFromTray();
                };
            }

            desktop.Exit += (_, _) => _window?.Dispose();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void TrayOpen_Click(object? sender, EventArgs e) => _window?.ShowFromTray();

    private void TrayQuit_Click(object? sender, EventArgs e)
    {
        _window?.AllowClose();
        (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
    }
}
