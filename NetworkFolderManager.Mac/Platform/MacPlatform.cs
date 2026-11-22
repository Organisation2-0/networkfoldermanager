using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using NetworkFolderManager.Services;
using System.Diagnostics;
using System.IO;

namespace NetworkFolderManager.Mac;

/// <summary>macOS-Implementierung (Avalonia) der plattformabhängigen Dienste.</summary>
public sealed class MacPlatform : IPlatform
{
    private static Window? MainWindow =>
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;


    public void Post(Action action) => Dispatcher.UIThread.Post(action);

    public string? GetInstallerReceiveFolder() => null;

    public async Task<string?> PickFolderAsync(string title, string? initialDirectory)
    {
        var window = MainWindow;
        if (window == null)
            return null;

        var options = new FolderPickerOpenOptions { Title = title, AllowMultiple = false };
        if (initialDirectory != null)
            options.SuggestedStartLocation = await window.StorageProvider.TryGetFolderFromPathAsync(initialDirectory);

        var result = await window.StorageProvider.OpenFolderPickerAsync(options);
        return result.Count > 0 ? result[0].TryGetLocalPath() : null;
    }

    public async Task<IReadOnlyList<string>> PickFilesAsync(string title)
    {
        var window = MainWindow;
        if (window == null)
            return Array.Empty<string>();

        var result = await window.StorageProvider.OpenFilePickerAsync(
            new FilePickerOpenOptions { Title = title, AllowMultiple = true });
        return result.Select(f => f.TryGetLocalPath()).Where(p => p != null).Select(p => p!).ToList();
    }

    public async Task<bool> ConfirmAsync(string message, string title)
    {
        var window = MainWindow;
        if (window == null)
            return false;

        var yes = new Button { Content = Loc.T("Ja"), MinWidth = 90, IsDefault = true, HorizontalContentAlignment = HorizontalAlignment.Center };
        var no = new Button { Content = Loc.T("Nein"), MinWidth = 90, IsCancel = true, HorizontalContentAlignment = HorizontalAlignment.Center };
        var dialog = new Window
        {
            Title = title,
            Width = 420,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Content = new StackPanel
            {
                Margin = new Thickness(20),
                Spacing = 18,
                Children =
                {
                    new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 8,
                        Children = { no, yes }
                    }
                }
            }
        };
        yes.Click += (_, _) => dialog.Close(true);
        no.Click += (_, _) => dialog.Close(false);
        return await dialog.ShowDialog<bool>(window);
    }

    public async Task<ClipboardContent?> GetClipboardAsync()
    {
        var clipboard = MainWindow?.Clipboard;
        if (clipboard == null)
            return null;

        var files = await clipboard.TryGetFilesAsync();
        if (files is { Length: > 0 })
        {
            var paths = files.Select(f => f.TryGetLocalPath()).Where(p => p != null).Select(p => p!).ToList();
            if (paths.Count > 0)
                return new ClipboardContent(paths, null);
        }

        // Text hat Vorrang (normales Einfügen); nur ohne Text wird ein Bild gesendet
        if (!string.IsNullOrEmpty(await clipboard.TryGetTextAsync()))
            return null;

        var bitmap = await clipboard.TryGetBitmapAsync();
        if (bitmap == null)
            return null;
        using var ms = new MemoryStream();
        bitmap.Save(ms);
        return new ClipboardContent(null, ms.ToArray());
    }

    public void OpenFile(string path) => Run("open", path);

    public void RevealInFileManager(string path) => Run("open", "-R", path);

    public void OpenFolder(string path) => Run("open", path);

    public void Notify(string title, string text, string? chatPeerId = null)
    {
        // Systembenachrichtigung über AppleScript (benötigt keine Signatur/Entitlements)
        var script = $"display notification \"{Escape(text)}\" with title \"{Escape(title)}\"";
        Run("osascript", "-e", script);
    }

    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", " ").Replace("\n", " ");

    /// <summary>Startet ein Programm ohne Shell und ohne zu warten.</summary>
    public static void Run(string file, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(file) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in args)
                psi.ArgumentList.Add(a);
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            LogService.Error("Platform/Run", ex);
        }
    }

    /// <summary>Startet ein Programm und liefert (Exitcode, Ausgabe).</summary>
    public static async Task<(int ExitCode, string Output)> RunAsync(string file, TimeSpan timeout, params string[] args)
    {
        var psi = new ProcessStartInfo(file)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException(file);
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync(cts.Token);
            var stderr = process.StandardError.ReadToEndAsync(cts.Token);
            await process.WaitForExitAsync(cts.Token);
            return (process.ExitCode, (await stdout + await stderr).Trim());
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(true); } catch { /* ignorieren */ }
            return (-1, "timeout");
        }
    }
}
