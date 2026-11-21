using System.IO;

namespace NetworkFolderManager.Services;

/// <summary>
/// Einfaches Fehlerprotokoll unter %APPDATA%\NetworkFolderManager\error.log (max. ~1 MB, danach rotiert).
/// </summary>
public static class LogService
{
    private static readonly object Lock = new();

    public static string LogFile => Path.Combine(SettingsService.SettingsDir, "error.log");

    public static void Error(string context, Exception ex) =>
        Write($"FEHLER [{context}] {ex}");

    public static void Info(string message) => Write(message);

    private static void Write(string text)
    {
        try
        {
            lock (Lock)
            {
                Directory.CreateDirectory(SettingsService.SettingsDir);
                var file = LogFile;
                if (File.Exists(file) && new FileInfo(file).Length > 1024 * 1024)
                    File.Move(file, file + ".old", overwrite: true);
                File.AppendAllText(file, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {text}{Environment.NewLine}");
            }
        }
        catch
        {
            // Protokollieren darf nie selbst einen Fehler auslösen
        }
    }
}
