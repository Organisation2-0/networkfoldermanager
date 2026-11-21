using Microsoft.Win32;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetworkFolderManager.Services;

/// <summary>
/// Informationen über ein verfügbares Update.
/// </summary>
public record UpdateInfo(Version Version, string Tag, string SetupUrl, string SetupName, long Size, string? Sha256Url, string? Notes);

/// <summary>
/// Automatische Updates über GitHub-Releases (https://github.com/Organisation2-0/NetworkFolderManager/releases).
///
/// Ablauf: Das neueste Release wird abgefragt; ist dessen Version höher, wird
/// "NetworkFolderManager-Setup-&lt;Version&gt;.exe" heruntergeladen, geprüft (SHA-256 aus der
/// beiliegenden ".sha256"-Datei sowie Herausgeber/Version in der Datei) und im Update-Modus
/// gestartet. Der Installer übernimmt alle bisherigen Einstellungen und startet das Programm neu.
///
/// Die Einstellung "automatisch aktualisieren" liegt in HKCU\Software\noobnet.eu\NetworkFolderManager
/// (Wert "AutoUpdate") – dieselbe Stelle, die auch der Installer setzt.
/// </summary>
public static class UpdateService
{
    public const string RepoOwner = "Organisation2-0";
    public const string RepoName = "NetworkFolderManager";
    public const string ReleasesPage = $"https://github.com/{RepoOwner}/{RepoName}/releases";
    private const string RegKey = @"Software\noobnet.eu\NetworkFolderManager";

    private static readonly HttpClient Http = CreateClient();

    public static Version CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version is { } v ? new Version(v.Major, v.Minor, Math.Max(0, v.Build)) : new Version(1, 0, 0);

    /// <summary>Automatische Updates ein/aus (Standard: an).</summary>
    public static bool AutoUpdateEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RegKey);
                return key?.GetValue("AutoUpdate") is not int value || value != 0;
            }
            catch
            {
                return true;
            }
        }
        set
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(RegKey);
                key.SetValue("AutoUpdate", value ? 1 : 0, RegistryValueKind.DWord);
            }
            catch (Exception ex)
            {
                LogService.Error("Update/Setting", ex);
            }
        }
    }

    /// <summary>
    /// Fragt das neueste Release ab. Gibt null zurück, wenn kein neueres vorhanden ist.
    /// </summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        using var response = await Http.GetAsync(
            $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest", ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null; // noch kein Release veröffentlicht
        response.EnsureSuccessStatusCode();

        var release = await JsonSerializer.DeserializeAsync<GitHubRelease>(
            await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        if (release == null || release.Draft || release.Prerelease || string.IsNullOrEmpty(release.TagName))
            return null;

        if (!Version.TryParse(release.TagName.TrimStart('v', 'V'), out var version) || version <= CurrentVersion)
            return null;

        var setup = release.Assets.FirstOrDefault(a =>
            a.Name.StartsWith("NetworkFolderManager-Setup-", StringComparison.OrdinalIgnoreCase) &&
            a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
        if (setup == null)
            return null;

        var sha = release.Assets.FirstOrDefault(a =>
            a.Name.Equals(Path.ChangeExtension(setup.Name, ".sha256"), StringComparison.OrdinalIgnoreCase));

        return new UpdateInfo(version, release.TagName, setup.DownloadUrl, setup.Name, setup.Size, sha?.DownloadUrl, release.Body);
    }

    /// <summary>
    /// Lädt das Setup herunter und prüft es. Gibt den Pfad der geprüften Datei zurück.
    /// </summary>
    public static async Task<string> DownloadAsync(UpdateInfo update, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var dir = Path.Combine(Path.GetTempPath(), "NetworkFolderManager-Update");
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, Path.GetFileName(update.SetupName));

        using (var response = await Http.GetAsync(update.SetupUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? update.Size;
            await using var source = await response.Content.ReadAsStreamAsync(ct);
            await using var file = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, ct)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read), ct);
                done += read;
                if (total > 0)
                    progress?.Report(done * 100.0 / total);
            }
        }

        try
        {
            await VerifyAsync(update, target, ct);
        }
        catch
        {
            try { File.Delete(target); } catch { /* ignorieren */ }
            throw;
        }
        return target;
    }

    private static async Task VerifyAsync(UpdateInfo update, string file, CancellationToken ct)
    {
        // 1. Prüfsumme (falls im Release vorhanden – build-installer.ps1 erzeugt sie immer)
        if (update.Sha256Url != null)
        {
            var expected = (await Http.GetStringAsync(update.Sha256Url, ct)).Trim().Split(' ', '\t')[0];
            await using var stream = File.OpenRead(file);
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Die Prüfsumme des Updates stimmt nicht – Download wird verworfen.");
        }

        // 2. Herausgeber und Version der Setup-Datei
        var info = FileVersionInfo.GetVersionInfo(file);
        // (Inno Setup füllt diese Felder mit Leerzeichen auf → Trim)
        if (info.CompanyName?.Trim() != "noobnet.eu" || info.ProductName?.Trim() != "NetworkFolderManager")
            throw new InvalidDataException("Die heruntergeladene Datei stammt nicht von noobnet.eu.");
        if (!Version.TryParse(info.ProductVersion?.Trim(), out var fileVersion) || fileVersion < update.Version)
            throw new InvalidDataException("Die Version der heruntergeladenen Datei passt nicht zum Update.");
    }

    /// <summary>
    /// Startet das Setup im Update-Modus (fragt per UAC nach Administratorrechten).
    /// Das Setup schließt dieses Programm, aktualisiert es und startet es danach wieder.
    /// </summary>
    public static bool StartInstaller(string setupPath)
    {
        try
        {
            Process.Start(new ProcessStartInfo(setupPath)
            {
                UseShellExecute = true,
                Arguments = "/SILENT /SUPPRESSMSGBOXES /NORESTART /UPDATE=1"
            });
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false; // UAC abgelehnt
        }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("NetworkFolderManager-Updater", CurrentVersion.ToString()));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    private class GitHubRelease
    {
        [JsonPropertyName("tag_name")] public string TagName { get; set; } = string.Empty;
        [JsonPropertyName("body")] public string? Body { get; set; }
        [JsonPropertyName("draft")] public bool Draft { get; set; }
        [JsonPropertyName("prerelease")] public bool Prerelease { get; set; }
        [JsonPropertyName("assets")] public List<GitHubAsset> Assets { get; set; } = new();
    }

    private class GitHubAsset
    {
        [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
        [JsonPropertyName("size")] public long Size { get; set; }
        [JsonPropertyName("browser_download_url")] public string DownloadUrl { get; set; } = string.Empty;
    }
}
