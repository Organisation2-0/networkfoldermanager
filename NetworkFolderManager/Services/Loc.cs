using Microsoft.Win32;
using System.Globalization;
using System.IO;

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

    /// <summary>Gespeicherte Sprachwahl (null = automatisch nach Systemsprache).</summary>
    public static string? SavedLanguage
    {
        get
        {
            try
            {
                if (!OperatingSystem.IsWindows())
                    return File.Exists(LanguageFile) ? File.ReadAllText(LanguageFile).Trim() : null;
                return ReadRegistry();
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
                if (!OperatingSystem.IsWindows())
                {
                    if (value is "de" or "en")
                    {
                        Directory.CreateDirectory(SettingsService.SettingsDir);
                        File.WriteAllText(LanguageFile, value);
                    }
                    else if (File.Exists(LanguageFile))
                    {
                        File.Delete(LanguageFile);
                    }
                    return;
                }
                WriteRegistry(value);
            }
            catch (Exception ex)
            {
                LogService.Error("Loc/Setting", ex);
            }
        }
    }

    private static string LanguageFile => Path.Combine(SettingsService.SettingsDir, "language.txt");

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string? ReadRegistry()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegKey);
        return key?.GetValue("Language") as string;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void WriteRegistry(string? value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RegKey);
        if (value is "de" or "en")
            key.SetValue("Language", value);
        else
            key.DeleteValue("Language", false);
    }

    private static string ReadLanguage()
    {
        var saved = SavedLanguage;
        if (saved is "de" or "en")
            return saved;
        return CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("de", StringComparison.OrdinalIgnoreCase) ? "de" : "en";
    }

    /// <summary>Zusätzliche Übersetzungen (z.B. der macOS-App); haben Vorrang vor <see cref="Strings"/>.</summary>
    public static Dictionary<string, string> Extra { get; } = new();

    /// <summary>Übersetzt einen deutschen Text; mit <paramref name="args"/> wird er wie string.Format ({0}, {1}, ...) gefüllt.</summary>
    public static string T(string german, params object?[] args)
    {
        var text = german;
        if (IsEnglish && (Extra.TryGetValue(german, out var english) || Strings.TryGetValue(german, out english)))
            text = english;
        return args.Length == 0 ? text : string.Format(text, args);
    }
}
