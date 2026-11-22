using Microsoft.Win32;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using Application = System.Windows.Application;
using Clipboard = System.Windows.Clipboard;
using MessageBox = System.Windows.MessageBox;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;

namespace NetworkFolderManager.Services;

/// <summary>Windows-Implementierung (WPF) der plattformabhängigen Dienste.</summary>
public sealed class WindowsPlatform : IPlatform
{
    public void Post(Action action)
    {
        var app = Application.Current;
        if (app == null)
            return;
        app.Dispatcher.BeginInvoke(action);
    }

    public string? GetInstallerReceiveFolder()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\noobnet.eu\NetworkFolderManager");
            return key?.GetValue("ReceiveFolder") as string;
        }
        catch
        {
            return null;
        }
    }

    public Task<string?> PickFolderAsync(string title, string? initialDirectory)
    {
        var dialog = new OpenFolderDialog { Title = title };
        if (initialDirectory != null)
            dialog.InitialDirectory = initialDirectory;
        return Task.FromResult(dialog.ShowDialog() == true ? dialog.FolderName : null);
    }

    public Task<IReadOnlyList<string>> PickFilesAsync(string title)
    {
        var dialog = new OpenFileDialog
        {
            Title = title,
            Filter = Loc.T("Alle Dateien (*.*)|*.*|Bilder|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.webp"),
            Multiselect = true
        };
        IReadOnlyList<string> result = dialog.ShowDialog() == true ? dialog.FileNames : Array.Empty<string>();
        return Task.FromResult(result);
    }

    public Task<bool> ConfirmAsync(string message, string title) =>
        Task.FromResult(MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes);

    public Task<ClipboardContent?> GetClipboardAsync()
    {
        if (Clipboard.ContainsFileDropList())
            return Task.FromResult<ClipboardContent?>(new ClipboardContent(Clipboard.GetFileDropList().Cast<string>().ToList(), null));

        if (Clipboard.ContainsImage() && Clipboard.GetImage() is { } image)
        {
            using var ms = new MemoryStream();
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            encoder.Save(ms);
            return Task.FromResult<ClipboardContent?>(new ClipboardContent(null, ms.ToArray()));
        }

        return Task.FromResult<ClipboardContent?>(null);
    }

    public void OpenFile(string path) =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });

    public void RevealInFileManager(string path) =>
        System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\"");

    public void OpenFolder(string path) =>
        System.Diagnostics.Process.Start("explorer.exe", path);

    public void Notify(string title, string text, string? chatPeerId = null) =>
        NotificationService.ShowToastNotification(title, text, chatPeerId: chatPeerId);
}
