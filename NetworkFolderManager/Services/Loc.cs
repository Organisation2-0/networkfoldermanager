using Microsoft.Win32;
using System.Globalization;
using System.Windows.Markup;

namespace NetworkFolderManager.Services;

/// <summary>
/// Sprachumschaltung Deutsch / English.
/// Die deutschen Texte sind zugleich die Schlüssel; für Englisch wird in <see cref="Strings"/> nachgeschlagen
/// (fehlt ein Eintrag, bleibt der deutsche Text stehen).
///
/// Die Sprache steht in HKCU\Software\noobnet.eu\NetworkFolderManager (Wert "Language": "de" oder "en").
/// Der Installer setzt sie anhand der gewählten Setup-Sprache; ohne Eintrag gilt die Sprache von Windows.
/// Eine Änderung in den Einstellungen wirkt nach einem Neustart des Programms.
/// </summary>
public static partial class Loc
{
    private const string RegKey = @"Software\noobnet.eu\NetworkFolderManager";

    /// <summary>Aktive Sprache ("de" oder "en"); wird beim Programmstart einmalig bestimmt.</summary>
    public static string Language { get; } = ReadLanguage();

    public static bool IsEnglish => Language == "en";

    /// <summary>Gespeicherte Sprachwahl (null = automatisch nach Windows-Sprache).</summary>
    public static string? SavedLanguage
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RegKey);
                return key?.GetValue("Language") as string;
            }
            catch
            {
                return null;
            }
        }
        set
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(RegKey);
                if (value is "de" or "en")
                    key.SetValue("Language", value);
                else
                    key.DeleteValue("Language", false);
            }
            catch (Exception ex)
            {
                LogService.Error("Loc/Setting", ex);
            }
        }
    }

    private static string ReadLanguage()
    {
        var saved = SavedLanguage;
        if (saved is "de" or "en")
            return saved;
        return CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("de", StringComparison.OrdinalIgnoreCase) ? "de" : "en";
    }

    /// <summary>Übersetzt einen deutschen Text; mit <paramref name="args"/> wird er wie string.Format ({0}, {1}, ...) gefüllt.</summary>
    public static string T(string german, params object?[] args)
    {
        var text = IsEnglish && Strings.TryGetValue(german, out var english) ? english : german;
        return args.Length == 0 ? text : string.Format(text, args);
    }
}

/// <summary>XAML: <c>Text="{loc:Tr 'Deutscher Text'}"</c></summary>
[MarkupExtensionReturnType(typeof(string))]
public class TrExtension : MarkupExtension
{
    public TrExtension() { }
    public TrExtension(string text) => Text = text;

    public string Text { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider) => Loc.T(Text);
}
