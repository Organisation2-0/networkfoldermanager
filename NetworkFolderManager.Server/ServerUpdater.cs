using System.IO.Compression;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using NetworkFolderManager.Models;

namespace NetworkFolderManager.Server;

/// <summary>
/// Automatic server updates from GitHub releases:
///  - checks shortly after start and then every 6 hours (if enabled, default: on),
///  - downloads nfm-server-&lt;version&gt;-&lt;rid&gt;.zip, verifies its SHA-256 (.sha256 asset),
///  - replaces the executable and restarts (systemd / Windows service restart the process automatically).
/// Data (accounts, folders, certificates) is never touched.
/// </summary>
public sealed class ServerUpdater : BackgroundService
{
    public const string Repository = "Organisation2-0/NetworkFolderManager";
    private static readonly TimeSpan FirstCheck = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    private readonly ServerState _state;
    private readonly ILogger<ServerUpdater> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private string? _latest;
    private string? _latestZipUrl;
    private string? _latestShaUrl;
    private DateTime? _lastCheck;
    private string _stateName = "idle";
    private string? _message;

    public ServerUpdater(ServerState state, ILogger<ServerUpdater> log)
    {
        _state = state;
        _log = log;
        state.Updater = this;
    }

    /// <summary>Only the published single-file build can replace itself.</summary>
    public static bool Supported =>
        string.IsNullOrEmpty(typeof(ServerUpdater).Assembly.Location) && Environment.ProcessPath is { } p &&
        Path.GetFileNameWithoutExtension(p).Equals("nfm-server", StringComparison.OrdinalIgnoreCase);

    /// <summary>Runtime identifier of the release package for this machine (e.g. linux-x64).</summary>
    public static string Rid
    {
        get
        {
            var os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
            var arch = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.Arm64 => "arm64",
                Architecture.X64 => "x64",
                var a => a.ToString().ToLowerInvariant()
            };
            return $"{os}-{arch}";
        }
    }

    /// <summary>Running under systemd or as a Windows service (= restarted automatically after exit).</summary>
    private static bool RunsAsService =>
        Environment.GetEnvironmentVariable("INVOCATION_ID") != null ||
        (OperatingSystem.IsWindows() && Microsoft.Extensions.Hosting.WindowsServices.WindowsServiceHelpers.IsWindowsService());

    public ServerUpdateDto Status() => new()
    {
        CurrentVersion = ServerState.Version,
        LatestVersion = _latest,
        UpdateAvailable = _latest != null && IsNewer(_latest, ServerState.Version),
        AutoUpdate = _state.Config.Read(c => c.AutoUpdate),
        Supported = Supported,
        Busy = _gate.CurrentCount == 0,
        LastCheck = _lastCheck,
        State = Supported ? _stateName : "unsupported",
        Message = Supported ? _message : "Automatic updates are only available for the published nfm-server build."
    };

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        CleanupOldExecutable();
        try
        {
            await Task.Delay(FirstCheck, ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!ct.IsCancellationRequested)
        {
            if (Supported && _state.Config.Read(c => c.AutoUpdate))
            {
                try
                {
                    await CheckAsync(ct);
                    if (_latest != null && IsNewer(_latest, ServerState.Version))
                        await InstallAsync(ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log.LogWarning("Automatic update failed: {Message}", ex.Message);
                }
            }

            try
            {
                await Task.Delay(Interval, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("nfm-server", ServerState.Version));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return http;
    }

    /// <summary>Asks GitHub for the latest release.</summary>
    public async Task<ServerUpdateDto> CheckAsync(CancellationToken ct = default)
    {
        if (!await _gate.WaitAsync(0, ct))
            return Status();
        try
        {
            _stateName = "checking";
            using var http = CreateClient();
            // NFM_UPDATE_FEED: alternative release feed (same JSON format as GitHub), e.g. for testing
            var feed = Environment.GetEnvironmentVariable("NFM_UPDATE_FEED") is { Length: > 0 } f
                ? f
                : $"https://api.github.com/repos/{Repository}/releases/latest";
            using var doc = JsonDocument.Parse(await http.GetStringAsync(feed, ct));
            var root = doc.RootElement;
            var version = root.GetProperty("tag_name").GetString()?.TrimStart('v', 'V') ?? "";
            string? zip = null, sha = null;
            var zipName = $"nfm-server-{version}-{Rid}.zip";
            foreach (var asset in root.GetProperty("assets").EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString();
                var url = asset.GetProperty("browser_download_url").GetString();
                if (string.Equals(name, zipName, StringComparison.OrdinalIgnoreCase))
                    zip = url;
                else if (string.Equals(name, zipName[..^4] + ".sha256", StringComparison.OrdinalIgnoreCase))
                    sha = url;
            }

            _latest = version;
            _latestZipUrl = zip;
            _latestShaUrl = sha;
            _lastCheck = DateTime.UtcNow;
            _stateName = "idle";
            _message = IsNewer(version, ServerState.Version) && (zip == null || sha == null)
                ? $"Version {version} has no package for {Rid}."
                : null;
            if (IsNewer(version, ServerState.Version))
                _log.LogInformation("New version available: {Version}", version);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _stateName = "error";
            _message = "Update check failed: " + ex.GetBaseException().Message;
            _lastCheck = DateTime.UtcNow;
        }
        finally
        {
            _gate.Release();
        }
        return Status();
    }

    /// <summary>Downloads, verifies and installs the latest version, then restarts the server.</summary>
    public async Task<ServerUpdateDto> InstallAsync(CancellationToken ct = default)
    {
        if (!Supported)
            return Status();
        if (_latest == null)
            await CheckAsync(ct);
        if (_latest == null || !IsNewer(_latest, ServerState.Version) || _latestZipUrl == null || _latestShaUrl == null)
            return Status();
        if (!await _gate.WaitAsync(0, ct))
            return Status();

        var exe = Environment.ProcessPath!;
        var dir = Path.GetDirectoryName(exe)!;
        var newFile = Path.Combine(dir, ".nfm-server.new");
        var zipFile = Path.Combine(_state.Paths.DataDir, "update.zip");
        var restart = false;
        try
        {
            // Can we replace the executable at all?
            try
            {
                File.WriteAllText(newFile, "");
                File.Delete(newFile);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                _stateName = "noPermission";
                _message = $"No write permission for {dir}. Install version {_latest} once manually (install-linux.sh / install-windows.ps1) – afterwards updates are automatic.";
                _log.LogWarning("{Message}", _message);
                return Status();
            }

            _stateName = "downloading";
            _message = null;
            _log.LogInformation("Downloading version {Version} ({Rid})...", _latest, Rid);
            using var http = CreateClient();
            var expected = (await http.GetStringAsync(_latestShaUrl, ct)).Trim().Split(' ', '\t')[0].Trim();
            await using (var src = await http.GetStreamAsync(_latestZipUrl, ct))
            await using (var dst = File.Create(zipFile))
                await src.CopyToAsync(dst, ct);

            string actual;
            await using (var fs = File.OpenRead(zipFile))
                actual = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct));
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Checksum of the downloaded package does not match.");

            using (var zip = ZipFile.OpenRead(zipFile))
            {
                var entry = zip.Entries.FirstOrDefault(e => e.Name.Equals(Path.GetFileName(exe), StringComparison.OrdinalIgnoreCase))
                            ?? throw new InvalidDataException("The package does not contain " + Path.GetFileName(exe));
                entry.ExtractToFile(newFile, overwrite: true);
            }

            // Replace the executable (a running file can be renamed on Windows and overwritten on Linux/macOS)
            if (OperatingSystem.IsWindows())
            {
                var old = exe + ".old";
                try { File.Delete(old); } catch { /* still in use */ }
                File.Move(exe, old, overwrite: true);
                File.Move(newFile, exe);
            }
            else
            {
                File.SetUnixFileMode(newFile, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                                              UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                                              UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
                File.Move(newFile, exe, overwrite: true);
            }

            _log.LogWarning("Version {Version} installed.", _latest);
            if (RunsAsService)
            {
                _stateName = "restarting";
                restart = true;
            }
            else
            {
                _stateName = "installed";
                _message = $"Version {_latest} installed – please restart nfm-server.";
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _stateName = "error";
            _message = "Update failed: " + ex.GetBaseException().Message;
            _log.LogWarning("{Message}", _message);
            try { File.Delete(newFile); } catch { /* ignore */ }
        }
        finally
        {
            try { File.Delete(zipFile); } catch { /* ignore */ }
            _gate.Release();
        }

        if (restart)
        {
            // Give the HTTP response time to be sent, then exit with an error code →
            // systemd (Restart=on-failure/always) and the Windows service recovery start the new version.
            _ = Task.Run(async () =>
            {
                await Task.Delay(1500);
                _log.LogWarning("Restarting for the update...");
                Environment.Exit(3);
            });
        }
        return Status();
    }

    private static void CleanupOldExecutable()
    {
        try
        {
            if (Environment.ProcessPath is { } exe && File.Exists(exe + ".old"))
                File.Delete(exe + ".old");
        }
        catch
        {
            // still locked – next time
        }
    }

    public static bool IsNewer(string candidate, string current) =>
        Version.TryParse(candidate, out var a) && Version.TryParse(current, out var b) && a > b;
}
