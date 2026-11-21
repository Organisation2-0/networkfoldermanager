using System.IO;

namespace NetworkFolderManager.Services;

/// <summary>
/// Überwacht einen lokalen oder Netzwerk-Ordner auf neue Dateien.
/// Löst Events aus, wenn Dateien hinzugefügt oder geändert werden.
/// </summary>
public sealed class FolderMonitorService : IDisposable
{
    private FileSystemWatcher? _watcher;
    private readonly object _lock = new();

    /// <summary>Wird ausgelöst, wenn eine neue Datei in den überwachten Ordner kopiert wurde.</summary>
    public event EventHandler<NewFileEventArgs>? NewFileDetected;

    /// <summary>Wird ausgelöst, wenn eine Datei geändert wurde.</summary>
    public event EventHandler<FileChangedEventArgs>? FileChanged;

    /// <summary>Wird ausgelöst, wenn eine Datei gelöscht wurde.</summary>
    public event EventHandler<FileDeletedEventArgs>? FileDeleted;

    /// <summary>Wird ausgelöst, wenn die Überwachung fehlschlägt (z.B. Netzwerk getrennt).</summary>
    public event EventHandler<ErrorEventArgs>? MonitorError;

    /// <summary>Gibt an, ob gerade ein Ordner überwacht wird.</summary>
    public bool IsMonitoring => _watcher != null;

    /// <summary>
    /// Startet die Überwachung eines Ordners.
    /// </summary>
    /// <param name="path">Der zu überwachende Pfad (lokal oder Netzwerk)</param>
    /// <param name="fileType">Dateityp-Filter (z.B. "*.txt", oder "*.*" für alle)</param>
    public void StartMonitoring(string path, string fileType = "*.*")
    {
        StopMonitoring();

        var fullPath = System.IO.Path.GetFullPath(path);

        lock (_lock)
        {
            _watcher = new FileSystemWatcher
            {
                Path = fullPath,
                Filter = fileType,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                IncludeSubdirectories = false,
                InternalBufferSize = 64 * 1024 // Größerer Buffer für Netzwerkordner
            };

            _watcher.Created += OnWatcherCreated;
            _watcher.Renamed += OnWatcherRenamed;
            _watcher.Changed += OnWatcherChanged;
            _watcher.Deleted += OnWatcherDeleted;
            _watcher.Error += OnWatcherError;
            _watcher.EnableRaisingEvents = true;
        }
    }

    /// <summary>
    /// Stoppt die Überwachung des aktuellen Ordners.
    /// </summary>
    public void StopMonitoring()
    {
        lock (_lock)
        {
            if (_watcher != null)
            {
                _watcher.EnableRaisingEvents = false;
                _watcher.Created -= OnWatcherCreated;
                _watcher.Renamed -= OnWatcherRenamed;
                _watcher.Changed -= OnWatcherChanged;
                _watcher.Deleted -= OnWatcherDeleted;
                _watcher.Error -= OnWatcherError;
                _watcher.Dispose();
                _watcher = null;
            }
        }
    }

    private void OnWatcherCreated(object sender, FileSystemEventArgs e)
    {
        if (Directory.Exists(e.FullPath))
            return; // Unterordner ignorieren

        _ = ReportWhenReadyAsync(e.FullPath, e.Name ?? Path.GetFileName(e.FullPath));
    }

    private void OnWatcherRenamed(object sender, RenamedEventArgs e)
    {
        // Viele Programme laden in eine temporäre Datei hoch und benennen sie danach um
        if (IsTempFile(e.OldName) && !IsTempFile(e.Name))
            _ = ReportWhenReadyAsync(e.FullPath, e.Name ?? Path.GetFileName(e.FullPath));
    }

    /// <summary>
    /// Wartet, bis die Datei nicht mehr geschrieben wird (max. ~10 Minuten), und meldet sie dann.
    /// </summary>
    private async Task ReportWhenReadyAsync(string fullPath, string name)
    {
        if (IsTempFile(name))
            return;

        for (var attempt = 0; attempt < 1200; attempt++)
        {
            await Task.Delay(500).ConfigureAwait(false);
            try
            {
                if (!File.Exists(fullPath))
                    return; // wieder verschwunden (z.B. umbenannt)

                using var _ = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.None);
                break; // exklusiv lesbar → Kopiervorgang abgeschlossen
            }
            catch (IOException)
            {
                // Datei wird noch geschrieben
            }
            catch (UnauthorizedAccessException)
            {
                break;
            }
        }

        NewFileDetected?.Invoke(this, new NewFileEventArgs(fullPath, name));
    }

    private static bool IsTempFile(string? name)
    {
        if (string.IsNullOrEmpty(name))
            return true;
        var ext = Path.GetExtension(name).ToLowerInvariant();
        return name.StartsWith("~$") || ext is ".tmp" or ".crdownload" or ".part" or ".partial";
    }

    private void OnWatcherChanged(object sender, FileSystemEventArgs e)
    {
        FileChanged?.Invoke(this, new FileChangedEventArgs(e.FullPath, e.Name ?? Path.GetFileName(e.FullPath)));
    }

    private void OnWatcherDeleted(object sender, FileSystemEventArgs e)
    {
        FileDeleted?.Invoke(this, new FileDeletedEventArgs(e.FullPath, e.Name ?? Path.GetFileName(e.FullPath)));
    }

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        MonitorError?.Invoke(this, e);
    }

    public void Dispose()
    {
        StopMonitoring();
        NewFileDetected = null;
        FileChanged = null;
        FileDeleted = null;
        MonitorError = null;
    }
}

/// <summary>
/// Event-Argumente für neu erkannte Dateien.
/// </summary>
public class NewFileEventArgs : EventArgs
{
    public string FullPath { get; }
    public string FileName { get; }
    public DateTime DetectedAt { get; }

    public NewFileEventArgs(string fullPath, string fileName)
    {
        FullPath = fullPath;
        FileName = fileName;
        DetectedAt = DateTime.Now;
    }
}

/// <summary>
/// Event-Argumente für geänderte Dateien.
/// </summary>
public class FileChangedEventArgs : EventArgs
{
    public string FullPath { get; }
    public string FileName { get; }
    public DateTime ChangedAt { get; }

    public FileChangedEventArgs(string fullPath, string fileName)
    {
        FullPath = fullPath;
        FileName = fileName;
        ChangedAt = DateTime.Now;
    }
}

/// <summary>
/// Event-Argumente für gelöschte Dateien.
/// </summary>
public class FileDeletedEventArgs : EventArgs
{
    public string FullPath { get; }
    public string FileName { get; }
    public DateTime DeletedAt { get; }

    public FileDeletedEventArgs(string fullPath, string fileName)
    {
        FullPath = fullPath;
        FileName = fileName;
        DeletedAt = DateTime.Now;
    }
}
