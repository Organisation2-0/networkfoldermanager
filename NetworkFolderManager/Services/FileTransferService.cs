using System.IO;

namespace NetworkFolderManager.Services;

/// <summary>
/// Dient zum Kopieren/Upload von Dateien zu Netzwerkordnern.
/// Meldet den Fortschritt über Events.
/// </summary>
public sealed class FileTransferService : IDisposable
{
    private const int BufferSize = 1024 * 1024; // 1 MB – gut für SMB

    /// <summary>Wird bei jeder Fortschrittsänderung ausgelöst (von einem Hintergrund-Thread).</summary>
    public event EventHandler<TransferProgressEventArgs>? ProgressChanged;

    /// <summary>Wird beim Abschluss eines Transfers ausgelöst (von einem Hintergrund-Thread).</summary>
    public event EventHandler<TransferResultEventArgs>? TransferCompleted;

    /// <summary>
    /// Kopiert eine einzelne Datei in einen Netzwerkordner.
    /// Existiert bereits eine Datei mit gleichem Namen, wird ein eindeutiger Name gewählt.
    /// </summary>
    public async Task<TransferResult> UploadFileAsync(
        string sourceFilePath,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        var fileName = Path.GetFileName(sourceFilePath);
        var destFilePath = Path.Combine(destinationPath, fileName);
        var created = false;

        try
        {
            if (!Directory.Exists(destinationPath))
                Directory.CreateDirectory(destinationPath);

            destFilePath = GetUniqueFilePath(destFilePath);
            var fileSize = new FileInfo(sourceFilePath).Length;

            await using (var source = new FileStream(sourceFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true))
            await using (var dest = new FileStream(destFilePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, useAsync: true))
            {
                created = true;
                var buffer = new byte[BufferSize];
                long total = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await dest.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    total += read;
                    ProgressChanged?.Invoke(this, new TransferProgressEventArgs(total, fileSize, fileName));
                }
                await dest.FlushAsync(cancellationToken);
            }

            var ok = new TransferResult(fileName, destFilePath, true, null);
            TransferCompleted?.Invoke(this, new TransferResultEventArgs(ok));
            return ok;
        }
        catch (Exception ex)
        {
            // Nur die von uns angelegte, unvollständige Datei entfernen
            if (created)
                TryDelete(destFilePath);

            var error = ex is OperationCanceledException ? "Transfer abgebrochen." : ex.Message;
            var failed = new TransferResult(fileName, destFilePath, false, error);
            TransferCompleted?.Invoke(this, new TransferResultEventArgs(failed));
            return failed;
        }
    }

    /// <summary>
    /// Kopiert mehrere Dateien mit begrenzter Parallelität in einen Netzwerkordner.
    /// </summary>
    public async Task<List<TransferResult>> UploadFilesAsync(
        IEnumerable<string> sourceFilePaths,
        string destinationPath,
        int maxConcurrent = 3,
        CancellationToken cancellationToken = default)
    {
        using var semaphore = new SemaphoreSlim(Math.Max(1, maxConcurrent));

        var tasks = sourceFilePaths.Select(async path =>
        {
            await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await UploadFileAsync(path, destinationPath, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                semaphore.Release();
            }
        });

        return (await Task.WhenAll(tasks)).ToList();
    }

    private static string GetUniqueFilePath(string path)
    {
        if (!File.Exists(path))
            return path;

        var dir = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 1; ; i++)
        {
            var candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(candidate))
                return candidate;
        }
    }

    private static void TryDelete(string destPath)
    {
        try
        {
            File.Delete(destPath);
        }
        catch
        {
            // ignorieren
        }
    }

    public void Dispose()
    {
        ProgressChanged = null;
        TransferCompleted = null;
    }
}

/// <summary>
/// Enthält Informationen über den Fortschritt eines Datei-Transfers.
/// </summary>
public class TransferProgressEventArgs : EventArgs
{
    public long BytesTransferred { get; }
    public long TotalBytes { get; }
    public string FileName { get; }

    public double Percentage => TotalBytes > 0 ? (double)BytesTransferred / TotalBytes * 100 : 100;

    public TransferProgressEventArgs(long bytesTransferred, long totalBytes, string fileName)
    {
        BytesTransferred = bytesTransferred;
        TotalBytes = totalBytes;
        FileName = fileName;
    }
}

/// <summary>
/// Event-Argumente für einen abgeschlossenen Datei-Transfer.
/// </summary>
public class TransferResultEventArgs : EventArgs
{
    public TransferResult Result { get; }

    public TransferResultEventArgs(TransferResult result) => Result = result;
}

/// <summary>
/// Ergebnis eines einzelnen Uploads.
/// </summary>
public record TransferResult(string FileName, string DestinationPath, bool Success, string? ErrorMessage);
