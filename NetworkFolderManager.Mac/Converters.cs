using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;
using NetworkFolderManager.Models;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;

namespace NetworkFolderManager;

/// <summary>Bilddatei → verkleinerte Vorschau (für Chat-Bilder).</summary>
public class PathToBitmapConverter : IValueConverter
{
    private static readonly ConcurrentDictionary<string, Bitmap?> Cache = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string path || path.Length == 0)
            return null;

        return Cache.GetOrAdd(path, p =>
        {
            try
            {
                if (!File.Exists(p))
                    return null;
                using var stream = File.OpenRead(p);
                return Bitmap.DecodeToWidth(stream, 640);
            }
            catch
            {
                return null; // kein darstellbares Bild
            }
        });
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Kleine Konverter für den Nachrichtenstatus.</summary>
public static class ChatConverters
{
    public static readonly IValueConverter IsSending =
        new FuncValueConverter<MessageStatus, bool>(s => s == MessageStatus.Sending);

    public static readonly IValueConverter IsFailed =
        new FuncValueConverter<MessageStatus, bool>(s => s == MessageStatus.Failed);
}
