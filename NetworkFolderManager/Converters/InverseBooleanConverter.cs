using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace NetworkFolderManager.Converters;

/// <summary>
/// Kehrt einen bool-Wert um (z.B. für IsEnabled="{Binding IsMonitoring, Converter=...}").
/// </summary>
public class InverseBooleanConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is bool b ? !b : value;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is bool b ? !b : value;
}

/// <summary>
/// Lädt ein Vorschaubild aus einem Dateipfad, ohne die Datei zu sperren.
/// ConverterParameter = maximale Breite in Pixeln (Standard 320).
/// </summary>
public class ImagePathConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string path || !File.Exists(path))
            return null;

        try
        {
            var width = int.TryParse(parameter as string, out var w) ? w : 320;
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad; // Datei sofort freigeben
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.DecodePixelWidth = width;
            image.UriSource = new Uri(path);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Nicht-leerer String / Zahl &gt; 0 / true → Visible, sonst Collapsed. ConverterParameter "invert" kehrt um.
/// </summary>
public class NotEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var visible = value switch
        {
            string s => !string.IsNullOrWhiteSpace(s),
            int i => i > 0,
            bool b => b,
            null => false,
            _ => true
        };
        if (parameter as string == "invert")
            visible = !visible;
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
