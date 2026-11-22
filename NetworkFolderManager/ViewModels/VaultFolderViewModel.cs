using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NetworkFolderManager.Dialogs;
using NetworkFolderManager.Models;
using NetworkFolderManager.Services;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;

namespace NetworkFolderManager.ViewModels;

/// <summary>Eine Datei bzw. ein Unterordner im Server-Ordner.</summary>
public partial class VaultItem : ObservableObject
{
    private readonly Action<VaultItem>? _requestThumbnail;
    private bool _thumbRequested;

    public ItemDto Dto { get; }

    public VaultItem(ItemDto dto, Action<VaultItem>? requestThumbnail)
    {
        Dto = dto;
        _requestThumbnail = requestThumbnail;
    }

    public string Name => Dto.Name;
    public string Path => Dto.Path;
    public bool IsDir => Dto.IsDir;
    public string Kind => Dto.Kind;
    public bool IsImage => Dto.Kind == "image";
    public bool IsVideo => Dto.Kind == "video";
    public bool IsViewable => IsImage || IsVideo || (Dto.Kind == "text" && Dto.Size < 2 * 1024 * 1024);
    public string SizeText => IsDir ? "–" : VaultFormat.Size(Dto.Size);
    public string ModifiedText => Dto.Modified.ToLocalTime().ToString("g");
    public string SubText => IsDir ? Dto.Modified.ToLocalTime().ToString("d") : SizeText;
    public string Ext => IsDir || !Name.Contains('.') ? "" : System.IO.Path.GetExtension(Name).TrimStart('.').ToUpperInvariant() is { Length: > 4 } e ? e[..4] : System.IO.Path.GetExtension(Name).TrimStart('.').ToUpperInvariant();

    public Brush KindBrush => new SolidColorBrush(Dto.Kind switch
    {
        "image" => Color.FromRgb(0x34, 0xC7, 0x59),
        "video" => Color.FromRgb(0xAF, 0x52, 0xDE),
        "audio" => Color.FromRgb(0xFF, 0x2D, 0x55),
        "pdf" => Color.FromRgb(0xFF, 0x3B, 0x30),
        "text" => Color.FromRgb(0x8E, 0x8E, 0x93),
        _ => Color.FromRgb(0x0A, 0x84, 0xFF)
    });

    [ObservableProperty] private ImageSource? _thumbnailImage;

    /// <summary>Vorschaubild – wird erst geladen, wenn es angezeigt wird (Virtualisierung).</summary>
    public ImageSource? Thumbnail
    {
        get
        {
            if (!_thumbRequested && IsImage && _requestThumbnail != null)
            {
                _thumbRequested = true;
                _requestThumbnail(this);
            }
            return ThumbnailImage;
        }
    }

    partial void OnThumbnailImageChanged(ImageSource? value) => OnPropertyChanged(nameof(Thumbnail));
}

/// <summary>Zeile im Foto-Raster (Monatsüberschrift oder bis zu N Bilder).</summary>
public class PhotoRow
{
    public string? Header { get; init; }
    public List<VaultItem> Items { get; init; } = new();
    public bool IsHeader => Header != null;
}

public record Crumb(string Name, string Path);

/// <summary>
/// Fenster eines geöffneten Server-Ordners (angelehnt an iCloud): Fotos (nach Monaten), Dateien,
/// Bildbetrachter, Hoch-/Herunterladen und Einbinden als Netzlaufwerk.
/// </summary>
public partial class VaultFolderViewModel : ObservableObject, IDisposable
{
    private readonly ServerEntry _server;
    private readonly FolderDto _folder;
    private string _token;
    private string _password;
    private readonly SemaphoreSlim _thumbGate = new(4);
    private readonly Dictionary<string, ImageSource> _thumbCache = new();
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "NFM-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly CancellationTokenSource _cts = new();
    private List<VaultItem> _media = new();
    private int _photoColumns = 5;

    public VaultFolderViewModel(ServerEntry server, FolderDto folder, UnlockResponse unlock, string password)
    {
        _server = server;
        _folder = folder;
        _token = unlock.FolderToken;
        _password = password;
    }

    private VaultClient Client => _server.Client!;
    public string FolderName => _folder.Name;
    public string ServerName => _server.Name;
    public bool IsMine => _folder.IsMine;
    public string Title => $"{_folder.Name} – {_server.Name}";

    public ObservableCollection<PhotoRow> PhotoRows { get; } = new();
    public ObservableCollection<VaultItem> Items { get; } = new();
    public ObservableCollection<Crumb> Crumbs { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFiles))]
    private bool _isPhotos = true;

    public bool IsFiles => !IsPhotos;

    [ObservableProperty] private string _currentPath = "/";
    [ObservableProperty] private bool _isListLayout;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isEmpty;
    [ObservableProperty] private string _emptyText = string.Empty;
    [ObservableProperty] private string _status = string.Empty;
    [ObservableProperty] private string _statsText = string.Empty;
    [ObservableProperty] private int _photoCount;
    [ObservableProperty] private int _fileCount;
    [ObservableProperty] private string _search = string.Empty;
    [ObservableProperty] private VaultItem? _selectedItem;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DriveButtonText))]
    private string? _mountedDrive;

    public string DriveButtonText => MountedDrive == null ? Loc.T("Als Netzlaufwerk verbinden") : Loc.T("Laufwerk {0} öffnen", MountedDrive);

    /// <summary>Wird aufgerufen, wenn das Fenster geschlossen werden soll (Sperren, Ordner gelöscht).</summary>
    public event EventHandler? CloseRequested;

    public async Task InitializeAsync()
    {
        await RefreshStatsAsync();
        await ShowPhotosAsync();
    }

    #region Server-Aufrufe mit automatischem Neu-Entsperren

    private async Task<T> CallAsync<T>(Func<string, Task<T>> call)
    {
        try
        {
            return await call(_token);
        }
        catch (VaultApiException ex) when (ex.Code == "folderLocked")
        {
            // Zugang abgelaufen (30 Min. ohne Nutzung) → mit dem bekannten Passwort neu entsperren
            var unlock = await Client.UnlockAsync(_folder.Id, _password);
            _token = unlock.FolderToken;
            return await call(_token);
        }
    }

    private Task CallAsync(Func<string, Task> call) => CallAsync<bool>(async t =>
    {
        await call(t);
        return true;
    });

    private void ShowError(Exception ex)
    {
        if (ex is VaultApiException { Code: "wrongPassword" })
        {
            Status = Loc.T("Das Ordnerpasswort wurde geändert. Bitte den Ordner neu öffnen.");
            CloseRequested?.Invoke(this, EventArgs.Empty);
            return;
        }
        Status = ex.Message;
    }

    #endregion

    #region Ansicht

    [RelayCommand]
    private Task ShowPhotosAsync()
    {
        IsPhotos = true;
        return LoadPhotosAsync();
    }

    [RelayCommand]
    private Task ShowFilesAsync()
    {
        IsPhotos = false;
        return NavigateAsync(CurrentPath);
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        await RefreshStatsAsync();
        if (IsPhotos)
            await LoadPhotosAsync();
        else
            await NavigateAsync(CurrentPath);
    }

    private async Task RefreshStatsAsync()
    {
        try
        {
            var s = await CallAsync(t => Client.StatsAsync(_folder.Id, t));
            PhotoCount = s.Photos + s.Videos;
            FileCount = s.Files;
            StatsText = Loc.T("{0} · verschlüsselt", VaultFormat.Size(s.Bytes));
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private async Task LoadPhotosAsync()
    {
        IsLoading = true;
        try
        {
            var media = await CallAsync(t => Client.MediaAsync(_folder.Id, t));
            _media = media.Select(m => new VaultItem(m, RequestThumbnail)).ToList();
            RebuildPhotoRows();
            IsEmpty = _media.Count == 0;
            EmptyText = Loc.T("Noch keine Fotos oder Videos.\nBilder hierher ziehen oder „Hochladen“ – sie landen im Ordner „Fotos“.");
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>Spaltenzahl an die Fensterbreite anpassen (vom Fenster aufgerufen).</summary>
    public void SetPhotoAreaWidth(double width)
    {
        var columns = Math.Max(2, (int)((width - 24) / 158));
        if (columns == _photoColumns)
            return;
        _photoColumns = columns;
        RebuildPhotoRows();
    }

    private void RebuildPhotoRows()
    {
        PhotoRows.Clear();
        var culture = System.Globalization.CultureInfo.CurrentUICulture;
        foreach (var group in _media.GroupBy(m => new DateTime(m.Dto.Modified.ToLocalTime().Year, m.Dto.Modified.ToLocalTime().Month, 1)))
        {
            PhotoRows.Add(new PhotoRow { Header = group.Key.ToString("MMMM yyyy", culture) });
            foreach (var chunk in group.Chunk(_photoColumns))
                PhotoRows.Add(new PhotoRow { Items = chunk.ToList() });
        }
    }

    partial void OnSearchChanged(string value)
    {
        if (IsFiles)
            ApplyFilter();
    }

    private List<VaultItem> _allItems = new();

    private void ApplyFilter()
    {
        Items.Clear();
        var q = Search.Trim();
        foreach (var it in _allItems.Where(i => q.Length == 0 || i.Name.Contains(q, StringComparison.OrdinalIgnoreCase)))
            Items.Add(it);
        IsEmpty = Items.Count == 0;
        EmptyText = q.Length > 0 ? Loc.T("Keine Treffer.") : Loc.T("Dieser Ordner ist leer.\nDateien oder Ordner hierher ziehen zum Hochladen.");
    }

    private static bool IsHidden(string name) =>
        name.StartsWith("._", StringComparison.Ordinal) || name is ".DS_Store" or "Thumbs.db" or "desktop.ini";

    [RelayCommand]
    private async Task NavigateAsync(string? path)
    {
        path = string.IsNullOrEmpty(path) ? "/" : path;
        IsLoading = true;
        try
        {
            var items = await CallAsync(t => Client.ItemsAsync(_folder.Id, t, path));
            CurrentPath = path;
            _allItems = items.Where(i => !IsHidden(i.Name)).Select(i => new VaultItem(i, RequestThumbnail)).ToList();
            Search = string.Empty;
            ApplyFilter();

            Crumbs.Clear();
            Crumbs.Add(new Crumb(Loc.T("Dateien"), "/"));
            var acc = "";
            foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                acc += "/" + part;
                Crumbs.Add(new Crumb(part, acc));
            }
        }
        catch (VaultApiException ex) when (ex.Code == "notFound" && path != "/")
        {
            await NavigateAsync("/");
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private Task GoUpAsync() => NavigateAsync(ParentOf(CurrentPath));

    private static string ParentOf(string p) => p.LastIndexOf('/') <= 0 ? "/" : p[..p.LastIndexOf('/')];
    private static string Join(string dir, string name) => (dir == "/" ? "" : dir) + "/" + name;

    [RelayCommand]
    private void ToggleLayout() => IsListLayout = !IsListLayout;

    [RelayCommand]
    private async Task OpenItemAsync(VaultItem? item)
    {
        if (item == null)
            return;
        if (item.IsDir)
        {
            await NavigateAsync(item.Path);
            return;
        }
        if (item.IsViewable)
        {
            var list = IsPhotos ? _media : Items.Where(i => i.IsViewable).ToList();
            OpenViewer(list, Math.Max(0, list.IndexOf(item)));
            return;
        }
        await OpenExternallyAsync(item);
    }

    /// <summary>Datei in einen temporären Ordner laden und mit dem Standardprogramm öffnen.</summary>
    private async Task OpenExternallyAsync(VaultItem item)
    {
        try
        {
            var path = await DownloadToTempAsync(item);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            Status = Loc.T("{0} geöffnet (lokale Kopie – Änderungen bitte erneut hochladen).", item.Name);
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private async Task<string> DownloadToTempAsync(VaultItem item)
    {
        Directory.CreateDirectory(_tempDir);
        var target = Path.Combine(_tempDir, Guid.NewGuid().ToString("N")[..6] + "_" + SafeFileName(item.Name));
        var progress = new Progress<double>(p => Status = Loc.T("Lade {0}... {1:0} %", item.Name, p));
        await CallAsync(t => Client.DownloadAsync(_folder.Id, t, item.Path, target, progress, _cts.Token));
        Status = string.Empty;
        return target;
    }

    private static string SafeFileName(string name) =>
        string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    #endregion

    #region Vorschaubilder

    private void RequestThumbnail(VaultItem item) => _ = LoadThumbnailAsync(item);

    private async Task LoadThumbnailAsync(VaultItem item)
    {
        var key = item.Path + "|" + item.Dto.Modified.Ticks;
        if (_thumbCache.TryGetValue(key, out var cached))
        {
            item.ThumbnailImage = cached;
            return;
        }

        await _thumbGate.WaitAsync(_cts.Token);
        try
        {
            var bytes = await CallAsync(t => Client.GetBytesAsync(_folder.Id, t, item.Path, _cts.Token));
            var image = await Task.Run(() => Decode(bytes, 320));
            if (image == null)
                return;
            _thumbCache[key] = image;
            item.ThumbnailImage = image;
        }
        catch
        {
            // Vorschau ist nicht wichtig (z.B. HEIC ohne Codec)
        }
        finally
        {
            _thumbGate.Release();
        }
    }

    private static BitmapSource? Decode(byte[] bytes, int maxWidth)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            if (maxWidth > 0)
                image.DecodePixelWidth = maxWidth;
            image.StreamSource = new MemoryStream(bytes);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }

    #endregion

    #region Betrachter

    private List<VaultItem> _viewerList = new();
    private int _viewerIndex;
    private int _viewerVersion;

    [ObservableProperty] private bool _isViewerOpen;
    [ObservableProperty] private VaultItem? _viewerItem;
    [ObservableProperty] private ImageSource? _viewerImage;
    [ObservableProperty] private Uri? _viewerVideo;
    [ObservableProperty] private string? _viewerText;
    [ObservableProperty] private bool _viewerLoading;
    [ObservableProperty] private string _viewerCounter = string.Empty;
    [ObservableProperty] private string _viewerInfo = string.Empty;
    [ObservableProperty] private bool _canViewPrevious;
    [ObservableProperty] private bool _canViewNext;

    private void OpenViewer(List<VaultItem> list, int index)
    {
        _viewerList = list;
        _viewerIndex = index;
        IsViewerOpen = true;
        _ = ShowViewerItemAsync();
    }

    private async Task ShowViewerItemAsync()
    {
        if (_viewerIndex < 0 || _viewerIndex >= _viewerList.Count)
            return;
        var version = ++_viewerVersion;
        var item = _viewerList[_viewerIndex];
        ViewerItem = item;
        ViewerCounter = $"{_viewerIndex + 1} / {_viewerList.Count}";
        ViewerInfo = $"{item.ModifiedText} · {item.SizeText}";
        CanViewPrevious = _viewerIndex > 0;
        CanViewNext = _viewerIndex < _viewerList.Count - 1;
        ViewerVideo = null;
        ViewerText = null;
        ViewerImage = item.ThumbnailImage; // erst die Vorschau, dann das volle Bild
        ViewerLoading = true;

        try
        {
            if (item.IsVideo)
            {
                var path = await DownloadToTempAsync(item);
                if (version == _viewerVersion)
                {
                    ViewerImage = null;
                    ViewerVideo = new Uri(path);
                }
            }
            else if (item.Kind == "text")
            {
                var bytes = await CallAsync(t => Client.GetBytesAsync(_folder.Id, t, item.Path, _cts.Token));
                if (version == _viewerVersion)
                {
                    ViewerImage = null;
                    ViewerText = Encoding.UTF8.GetString(bytes);
                }
            }
            else
            {
                var bytes = await CallAsync(t => Client.GetBytesAsync(_folder.Id, t, item.Path, _cts.Token));
                var image = await Task.Run(() => Decode(bytes, 2560));
                if (version == _viewerVersion)
                {
                    ViewerImage = image;
                    if (image == null)
                        ViewerText = Loc.T("Dieses Bildformat kann Windows nicht anzeigen. Über „Herunterladen“ speichern.");
                }
            }
        }
        catch (Exception ex)
        {
            if (version == _viewerVersion)
                ShowError(ex);
        }
        finally
        {
            if (version == _viewerVersion)
                ViewerLoading = false;
        }
    }

    [RelayCommand]
    private void ViewerPrevious()
    {
        if (_viewerIndex <= 0)
            return;
        _viewerIndex--;
        _ = ShowViewerItemAsync();
    }

    [RelayCommand]
    private void ViewerNext()
    {
        if (_viewerIndex >= _viewerList.Count - 1)
            return;
        _viewerIndex++;
        _ = ShowViewerItemAsync();
    }

    [RelayCommand]
    private void CloseViewer()
    {
        _viewerVersion++;
        IsViewerOpen = false;
        ViewerVideo = null;
        ViewerImage = null;
        ViewerText = null;
    }

    [RelayCommand]
    private Task ViewerDownloadAsync() => ViewerItem == null ? Task.CompletedTask : DownloadAsync(ViewerItem);

    [RelayCommand]
    private async Task ViewerDeleteAsync()
    {
        if (ViewerItem is not { } item || !await DeleteCoreAsync(item))
            return;
        _viewerList.Remove(item);
        if (_viewerList.Count == 0)
        {
            CloseViewer();
            return;
        }
        _viewerIndex = Math.Min(_viewerIndex, _viewerList.Count - 1);
        await ShowViewerItemAsync();
    }

    #endregion

    #region Dateiaktionen

    [RelayCommand]
    private async Task DownloadAsync(VaultItem? item)
    {
        item ??= SelectedItem;
        if (item == null || item.IsDir)
            return;
        var dlg = new Microsoft.Win32.SaveFileDialog { FileName = item.Name, Title = Loc.T("Datei speichern") };
        if (dlg.ShowDialog() != true)
            return;
        try
        {
            var progress = new Progress<double>(p => Status = Loc.T("Lade {0}... {1:0} %", item.Name, p));
            await CallAsync(t => Client.DownloadAsync(_folder.Id, t, item.Path, dlg.FileName, progress, _cts.Token));
            Status = Loc.T("{0} gespeichert.", item.Name);
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    [RelayCommand]
    private async Task NewFolderAsync()
    {
        var dir = IsPhotos ? "/" : CurrentPath;
        var result = FormDialog.Show(null, Loc.T("Neuer Ordner"), null, new[] { FormField.Text("name", Loc.T("Name")) }, Loc.T("Erstellen"),
            async v => await CallAsync(t => Client.MkdirAsync(_folder.Id, t, Join(dir, v["name"]))));
        if (result != null && IsFiles)
            await NavigateAsync(CurrentPath);
    }

    [RelayCommand]
    private async Task RenameAsync(VaultItem? item)
    {
        item ??= SelectedItem;
        if (item == null)
            return;
        var result = FormDialog.Show(null, Loc.T("Umbenennen"), null, new[] { FormField.Text("name", Loc.T("Neuer Name"), item.Name) }, Loc.T("Speichern"),
            async v =>
            {
                if (v["name"].Length > 0 && v["name"] != item.Name)
                    await CallAsync(t => Client.MoveAsync(_folder.Id, t, item.Path, Join(ParentOf(item.Path), v["name"])));
            });
        if (result != null)
            await RefreshAsync();
    }

    [RelayCommand]
    private async Task DeleteAsync(VaultItem? item)
    {
        item ??= SelectedItem;
        if (item != null && await DeleteCoreAsync(item))
            await RefreshAsync();
    }

    private async Task<bool> DeleteCoreAsync(VaultItem item)
    {
        var text = item.IsDir
            ? Loc.T("Der Ordner „{0}“ und alles darin wird endgültig gelöscht.", item.Name)
            : Loc.T("„{0}“ wird endgültig gelöscht.", item.Name);
        if (!FormDialog.Confirm(null, Loc.T("Löschen?"), text, Loc.T("Löschen")))
            return false;
        try
        {
            await CallAsync(t => Client.DeleteItemAsync(_folder.Id, t, item.Path));
            _media.Remove(item);
            return true;
        }
        catch (Exception ex)
        {
            ShowError(ex);
            return false;
        }
    }

    #endregion

    #region Upload

    [ObservableProperty] private bool _isUploading;
    [ObservableProperty] private double _uploadProgress;
    [ObservableProperty] private string _uploadText = string.Empty;
    private CancellationTokenSource? _uploadCts;

    [RelayCommand]
    private async Task UploadAsync()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Multiselect = true,
            Title = IsPhotos ? Loc.T("Fotos und Videos hochladen") : Loc.T("Dateien hochladen"),
            Filter = IsPhotos
                ? Loc.T("Fotos und Videos") + "|*.jpg;*.jpeg;*.png;*.gif;*.webp;*.heic;*.heif;*.bmp;*.tif;*.tiff;*.mp4;*.mov;*.m4v;*.webm;*.avi;*.mkv|" + Loc.T("Alle Dateien") + "|*.*"
                : Loc.T("Alle Dateien") + "|*.*"
        };
        if (dlg.ShowDialog() == true)
            await UploadPathsAsync(dlg.FileNames);
    }

    [RelayCommand]
    private void CancelUpload() => _uploadCts?.Cancel();

    /// <summary>Dateien und Ordner (per Drag &amp; Drop) hochladen – Ordnerstruktur bleibt erhalten.</summary>
    public async Task UploadPathsAsync(IReadOnlyList<string> paths)
    {
        if (IsUploading)
        {
            Status = Loc.T("Es läuft bereits ein Upload.");
            return;
        }

        var targetDir = IsPhotos ? "/Fotos" : CurrentPath;
        var jobs = new List<(string Local, string Remote)>();
        var dirs = new List<string>();
        foreach (var p in paths)
        {
            if (File.Exists(p))
            {
                jobs.Add((p, Join(targetDir, Path.GetFileName(p))));
            }
            else if (Directory.Exists(p))
            {
                var root = Join(targetDir, Path.GetFileName(p.TrimEnd('\\', '/')));
                dirs.Add(root);
                foreach (var d in Directory.EnumerateDirectories(p, "*", SearchOption.AllDirectories))
                    dirs.Add(root + "/" + Path.GetRelativePath(p, d).Replace('\\', '/'));
                foreach (var f in Directory.EnumerateFiles(p, "*", SearchOption.AllDirectories))
                    jobs.Add((f, root + "/" + Path.GetRelativePath(p, f).Replace('\\', '/')));
            }
        }
        if (jobs.Count == 0 && dirs.Count == 0)
            return;

        IsUploading = true;
        UploadProgress = 0;
        _uploadCts = new CancellationTokenSource();
        var ct = _uploadCts.Token;
        var total = Math.Max(1, jobs.Sum(j => new FileInfo(j.Local).Length));
        var perFile = new long[jobs.Count];
        var done = 0;
        var failed = new List<string>();

        try
        {
            // Zielordner anlegen (vorhandene ignorieren)
            if (targetDir != "/")
                dirs.Insert(0, targetDir);
            foreach (var d in dirs.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    await CallAsync(t => Client.MkdirAsync(_folder.Id, t, d));
                }
                catch (VaultApiException ex) when (ex.Code == "exists")
                {
                    // gibt es schon
                }
            }

            using var gate = new SemaphoreSlim(3);
            var tasks = jobs.Select(async (job, index) =>
            {
                await gate.WaitAsync(ct);
                try
                {
                    var progress = new Progress<long>(bytes =>
                    {
                        perFile[index] = bytes;
                        UploadProgress = perFile.Sum() * 100.0 / total;
                    });
                    await CallAsync(t => Client.UploadAsync(_folder.Id, t, job.Remote, job.Local, progress, ct));
                    var n = Interlocked.Increment(ref done);
                    UploadText = Loc.T("{0} von {1} hochgeladen", n, jobs.Count);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    lock (failed)
                        failed.Add($"{Path.GetFileName(job.Local)}: {ex.Message}");
                }
                finally
                {
                    gate.Release();
                }
            }).ToList();
            UploadText = Loc.T("Lade {0} Datei(en) hoch...", jobs.Count);
            await Task.WhenAll(tasks);

            Status = failed.Count == 0
                ? Loc.T("{0} Datei(en) hochgeladen.", done)
                : Loc.T("{0} hochgeladen, {1} fehlgeschlagen: {2}", done, failed.Count, string.Join("; ", failed.Take(3)));
        }
        catch (OperationCanceledException)
        {
            Status = Loc.T("Upload abgebrochen ({0} Datei(en) hochgeladen).", done);
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
        finally
        {
            IsUploading = false;
            _uploadCts.Dispose();
            _uploadCts = null;
        }
        await RefreshAsync();
    }

    #endregion

    #region Ordner (Netzlaufwerk, Passwort, Sperren)

    [RelayCommand]
    private async Task MountDriveAsync()
    {
        if (MountedDrive != null && Directory.Exists(MountedDrive + "\\"))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", MountedDrive + "\\") { UseShellExecute = true });
            return;
        }

        var server = _server.Saved!;
        if (!WebDavMountService.IsPrepared(server))
        {
            var ok = FormDialog.Confirm(null, Loc.T("Netzlaufwerk einrichten"),
                Loc.T("Einmalig für diesen Server sind Administratorrechte nötig:\n\n• Das Zertifikat von „{0}“ wird als vertrauenswürdig installiert (es gilt nur für lokale Adressen, nicht für Websites).\n• Der Windows-WebDAV-Dienst wird für Dateien bis 4 GB eingerichtet.\n\nFingerabdruck: {1}",
                    _server.Name, server.FingerprintShort),
                Loc.T("Einrichten"), danger: false);
            if (!ok)
                return;
            Status = Loc.T("Richte Netzlaufwerk ein (Administratorrechte)...");
            if (!await WebDavMountService.PrepareElevatedAsync(server))
            {
                Status = Loc.T("Einrichtung abgebrochen oder fehlgeschlagen.");
                return;
            }
        }

        Status = Loc.T("Verbinde Netzlaufwerk...");
        try
        {
            MountedDrive = await WebDavMountService.MountAsync(server, _folder.Name, _password, persistent: true);
            Status = Loc.T("„{0}“ ist als Laufwerk {1} verbunden (Anmeldung: {0} + Ordnerpasswort).", _folder.Name, MountedDrive);
            Process.Start(new ProcessStartInfo("explorer.exe", MountedDrive + "\\") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Status = ex.Message;
        }
    }

    [RelayCommand]
    private void ChangePassword()
    {
        if (!IsMine)
            return;
        var result = FormDialog.Show(null, Loc.T("Ordnerpasswort ändern"),
            Loc.T("Alle, die den Ordner geöffnet oder als Laufwerk verbunden haben, müssen danach das neue Passwort eingeben."),
            new[]
            {
                FormField.Password("old", Loc.T("Bisheriges Passwort")),
                FormField.Password("pw", Loc.T("Neues Passwort"), Loc.T("Mindestens 8 Zeichen")),
                FormField.Password("pw2", Loc.T("Passwort wiederholen"))
            },
            Loc.T("Speichern"),
            async v =>
            {
                FormDialog.CheckNewPassword(v["pw"], v["pw2"]);
                var unlock = await Client.ChangeFolderPasswordAsync(_folder.Id, v["old"], v["pw"]);
                _token = unlock.FolderToken;
                _password = v["pw"];
            });
        if (result == null)
            return;
        if (MountedDrive != null)
        {
            WebDavMountService.Unmount(MountedDrive);
            MountedDrive = null;
        }
        Status = Loc.T("Ordnerpasswort geändert.");
    }

    [RelayCommand]
    private async Task LockAsync()
    {
        try
        {
            await Client.LockAsync(_folder.Id, _token);
        }
        catch
        {
            // egal
        }
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    #endregion

    public void Dispose()
    {
        _cts.Cancel();
        _uploadCts?.Cancel();
        _ = Client.LockAsync(_folder.Id, _token).ContinueWith(_ => { }, TaskScheduler.Default);
        _password = string.Empty;
        // Entschlüsselte temporäre Kopien wieder entfernen
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
            // Datei evtl. noch in einem Programm geöffnet
        }
    }
}
