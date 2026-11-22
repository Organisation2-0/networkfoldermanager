using NetworkFolderManager.Models;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Security;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace NetworkFolderManager.Services;

public class VaultApiException : Exception
{
    public int Status { get; }
    public string Code { get; }

    public VaultApiException(int status, string code, string message) : base(message)
    {
        Status = status;
        Code = code;
    }
}

/// <summary>
/// HTTPS-Client für einen NFM-Server. Das Serverzertifikat muss von der gespeicherten Server-CA stammen
/// (Fingerabdruck beim ersten Kontakt gemerkt) – sonst wird die Verbindung abgelehnt.
/// </summary>
public sealed class VaultClient : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http;
    private readonly X509Certificate2 _ca;

    public SavedServer Server { get; }
    public string? Token { get; set; }

    public VaultClient(SavedServer server)
    {
        Server = server;
        _ca = new X509Certificate2(Convert.FromBase64String(server.CaCertificate));
        if (!string.Equals(Convert.ToHexString(SHA256.HashData(_ca.RawData)), server.Fingerprint, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(Loc.T("Gespeichertes Serverzertifikat passt nicht zum Fingerabdruck."));

        var handler = new SocketsHttpHandler
        {
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(6),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1),
            SslOptions = new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = ValidateCertificate }
        };
        _http = new HttpClient(handler) { BaseAddress = new Uri(server.BaseUrl), Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.Add(VaultProtocol.ClientHeader, "1");
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("NetworkFolderManager", UpdateService.CurrentVersion.ToString()));
    }

    private bool ValidateCertificate(object sender, X509Certificate? certificate, X509Chain? _, SslPolicyErrors __)
    {
        if (certificate == null)
            return false;
        using var leaf = new X509Certificate2(certificate);
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(_ca);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        if (!chain.Build(leaf))
            return false;
        var root = chain.ChainElements[^1].Certificate;
        return root.RawData.AsSpan().SequenceEqual(_ca.RawData);
    }

    /// <summary>
    /// Erstkontakt: Infos und Stammzertifikat eines Servers abrufen (ohne Prüfung – der Fingerabdruck wird danach
    /// dem Benutzer angezeigt bzw. mit dem aus der Netzwerk-Erkennung verglichen).
    /// </summary>
    public static async Task<(ServerInfoDto Info, byte[] CaDer)> ProbeAsync(string host, int port, CancellationToken ct = default)
    {
        using var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(5),
            SslOptions = new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = (_, _, _, _) => true }
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        var baseUrl = new SavedServer { Host = host, Port = port }.BaseUrl;
        var info = await http.GetFromJsonAsync<ServerInfoDto>(baseUrl + "api/info", Json, ct)
                   ?? throw new InvalidOperationException(Loc.T("Keine Antwort vom Server."));
        var pem = await http.GetStringAsync(baseUrl + "ca.crt", ct);
        var ca = X509Certificate2.CreateFromPem(pem);
        var der = ca.RawData;
        if (!string.Equals(Convert.ToHexString(SHA256.HashData(der)), info.Fingerprint, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(Loc.T("Das Serverzertifikat passt nicht zum gemeldeten Fingerabdruck."));
        return (info, der);
    }

    #region Basis

    private HttpRequestMessage Request(HttpMethod method, string url, object? body = null, string? folderToken = null)
    {
        var req = new HttpRequestMessage(method, url);
        if (Token != null)
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        if (folderToken != null)
            req.Headers.Add(VaultProtocol.FolderTokenHeader, folderToken);
        if (body != null)
            req.Content = JsonContent.Create(body, body.GetType(), options: Json);
        return req;
    }

    private async Task<HttpResponseMessage> SendRawAsync(HttpRequestMessage req, CancellationToken ct, HttpCompletionOption option = HttpCompletionOption.ResponseContentRead)
    {
        HttpResponseMessage res;
        try
        {
            res = await _http.SendAsync(req, option, ct);
        }
        catch (HttpRequestException ex) when (ex.InnerException is System.Security.Authentication.AuthenticationException)
        {
            throw new VaultApiException(0, "certificate", Loc.T("Das Zertifikat des Servers hat sich geändert oder ist ungültig. Verbindung aus Sicherheitsgründen abgelehnt."));
        }
        catch (HttpRequestException ex)
        {
            throw new VaultApiException(0, "network", Loc.T("Server {0} nicht erreichbar: {1}", Server.Name, ex.GetBaseException().Message));
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new VaultApiException(0, "timeout", Loc.T("Zeitüberschreitung bei der Verbindung zu {0}.", Server.Name));
        }

        if (res.IsSuccessStatusCode)
            return res;

        ErrorDto? err = null;
        try
        {
            err = await res.Content.ReadFromJsonAsync<ErrorDto>(Json, ct);
        }
        catch
        {
            // kein JSON
        }
        var status = (int)res.StatusCode;
        res.Dispose();
        throw new VaultApiException(status, err?.Code ?? "http" + status, ServerErrors.Message(err?.Code, err?.Error ?? $"HTTP {status}"));
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string url, object? body = null, string? folderToken = null, CancellationToken ct = default)
    {
        using var req = Request(method, url, body, folderToken);
        using var res = await SendRawAsync(req, ct);
        return (await res.Content.ReadFromJsonAsync<T>(Json, ct))!;
    }

    private async Task SendAsync(HttpMethod method, string url, object? body = null, string? folderToken = null, CancellationToken ct = default)
    {
        using var req = Request(method, url, body, folderToken);
        using var _ = await SendRawAsync(req, ct);
    }

    private static string Q(string s) => Uri.EscapeDataString(s);

    #endregion

    #region Konto

    public Task<ServerInfoDto> InfoAsync() => SendAsync<ServerInfoDto>(HttpMethod.Get, "api/info");

    public async Task<UserDto> LoginAsync(string username, string password)
    {
        var res = await SendAsync<AuthResponse>(HttpMethod.Post, "api/auth/login",
            new LoginRequest { Username = username, Password = password, Device = $"Windows-App ({Environment.MachineName})" });
        Token = res.Token;
        return res.User;
    }

    public async Task<UserDto> RegisterAsync(string username, string password, string? displayName, string? setupCode)
    {
        var res = await SendAsync<AuthResponse>(HttpMethod.Post, "api/auth/register", new RegisterRequest
        {
            Username = username, Password = password, DisplayName = displayName, SetupCode = setupCode,
            Device = $"Windows-App ({Environment.MachineName})"
        });
        Token = res.Token;
        return res.User;
    }

    public Task<UserDto> MeAsync() => SendAsync<UserDto>(HttpMethod.Get, "api/auth/me");

    public async Task LogoutAsync()
    {
        try
        {
            await SendAsync(HttpMethod.Post, "api/auth/logout");
        }
        finally
        {
            Token = null;
        }
    }

    /// <summary>Bevorzugte Sprache im Profil speichern (gilt auf allen Geräten).</summary>
    public Task<UserDto> SetLanguageAsync(string language) =>
        SendAsync<UserDto>(HttpMethod.Put, "api/auth/language", new LanguageRequest { Language = language });

    public Task<UserDto> ChangePasswordAsync(string oldPassword, string newPassword) =>
        SendAsync<UserDto>(HttpMethod.Post, "api/auth/password", new ChangePasswordRequest { OldPassword = oldPassword, NewPassword = newPassword });

    #endregion

    #region Ordner

    public Task<List<FolderDto>> FoldersAsync() => SendAsync<List<FolderDto>>(HttpMethod.Get, "api/folders");

    public Task<FolderDto> CreateFolderAsync(string name, string password) =>
        SendAsync<FolderDto>(HttpMethod.Post, "api/folders", new CreateFolderRequest { Name = name, Password = password });

    public Task DeleteFolderAsync(string id, string password) =>
        SendAsync(HttpMethod.Delete, $"api/folders/{id}", new PasswordRequest { Password = password });

    public Task<UnlockResponse> UnlockAsync(string id, string password) =>
        SendAsync<UnlockResponse>(HttpMethod.Post, $"api/folders/{id}/unlock", new PasswordRequest { Password = password });

    public Task LockAsync(string id, string folderToken) =>
        SendAsync(HttpMethod.Post, $"api/folders/{id}/lock", null, folderToken);

    public Task<UnlockResponse> ChangeFolderPasswordAsync(string id, string oldPassword, string newPassword) =>
        SendAsync<UnlockResponse>(HttpMethod.Post, $"api/folders/{id}/password",
            new ChangePasswordRequest { OldPassword = oldPassword, NewPassword = newPassword });

    public Task<List<ItemDto>> ItemsAsync(string id, string token, string path) =>
        SendAsync<List<ItemDto>>(HttpMethod.Get, $"api/folders/{id}/items?path={Q(path)}", null, token);

    public Task<List<ItemDto>> MediaAsync(string id, string token) =>
        SendAsync<List<ItemDto>>(HttpMethod.Get, $"api/folders/{id}/media", null, token);

    public Task<FolderStatsDto> StatsAsync(string id, string token) =>
        SendAsync<FolderStatsDto>(HttpMethod.Get, $"api/folders/{id}/stats", null, token);

    public Task<ItemDto> MkdirAsync(string id, string token, string path) =>
        SendAsync<ItemDto>(HttpMethod.Post, $"api/folders/{id}/mkdir", new PathRequest { Path = path }, token);

    public Task<ItemDto> MoveAsync(string id, string token, string from, string to) =>
        SendAsync<ItemDto>(HttpMethod.Post, $"api/folders/{id}/move", new MoveRequest { From = from, To = to }, token);

    public Task DeleteItemAsync(string id, string token, string path) =>
        SendAsync(HttpMethod.Post, $"api/folders/{id}/delete", new PathRequest { Path = path }, token);

    /// <summary>Datei vollständig laden (für Vorschaubilder/Bilder).</summary>
    public async Task<byte[]> GetBytesAsync(string id, string token, string path, CancellationToken ct = default)
    {
        using var req = Request(HttpMethod.Get, $"api/folders/{id}/file?path={Q(path)}", null, token);
        using var res = await SendRawAsync(req, ct);
        return await res.Content.ReadAsByteArrayAsync(ct);
    }

    /// <summary>Datei in eine lokale Datei herunterladen.</summary>
    public async Task DownloadAsync(string id, string token, string path, string target, IProgress<double>? progress, CancellationToken ct = default)
    {
        using var req = Request(HttpMethod.Get, $"api/folders/{id}/file?path={Q(path)}", null, token);
        using var res = await SendRawAsync(req, ct, HttpCompletionOption.ResponseHeadersRead);
        var total = res.Content.Headers.ContentLength ?? 0;
        await using var src = await res.Content.ReadAsStreamAsync(ct);
        var tmp = target + ".part";
        try
        {
            await using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, true))
            {
                var buffer = new byte[1 << 16];
                long done = 0;
                int read;
                while ((read = await src.ReadAsync(buffer, ct)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                    done += read;
                    if (total > 0)
                        progress?.Report(done * 100.0 / total);
                }
            }
            File.Move(tmp, target, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch { /* ignorieren */ }
            throw;
        }
    }

    /// <summary>Lokale Datei hochladen (vorhandene Namen → "Name (1).ext").</summary>
    public async Task<ItemDto> UploadAsync(string id, string token, string targetPath, string localFile, IProgress<long>? progress, CancellationToken ct = default)
    {
        var info = new FileInfo(localFile);
        var mtime = new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds();
        await using var fs = new FileStream(localFile, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, true);
        using var req = Request(HttpMethod.Put, $"api/folders/{id}/file?path={Q(targetPath)}&mode=rename&mtime={mtime}", null, token);
        req.Content = new StreamContent(new ProgressStream(fs, progress), 1 << 16);
        req.Content.Headers.ContentLength = info.Length;
        using var res = await SendRawAsync(req, ct);
        return (await res.Content.ReadFromJsonAsync<ItemDto>(Json, ct))!;
    }

    #endregion

    #region Admin

    public Task<List<FolderDto>> AdminFoldersAsync() => SendAsync<List<FolderDto>>(HttpMethod.Get, "api/admin/folders");

    public Task<FolderDto> AdminCreateFolderAsync(string name, string password, string owner) =>
        SendAsync<FolderDto>(HttpMethod.Post, "api/admin/folders", new CreateFolderRequest { Name = name, Password = password, Owner = owner });

    public Task AdminDeleteFolderAsync(string id) => SendAsync(HttpMethod.Delete, $"api/admin/folders/{id}");

    public Task<FolderDto> AdminSetOwnerAsync(string id, string username) =>
        SendAsync<FolderDto>(HttpMethod.Post, $"api/admin/folders/{id}/owner", new SetOwnerRequest { Username = username });

    public Task<List<UserDto>> AdminUsersAsync() => SendAsync<List<UserDto>>(HttpMethod.Get, "api/admin/users");

    public Task<UserDto> AdminCreateUserAsync(string username, string password, string? displayName, bool isAdmin) =>
        SendAsync<UserDto>(HttpMethod.Post, "api/admin/users",
            new AdminUserRequest { Username = username, Password = password, DisplayName = displayName, IsAdmin = isAdmin });

    public Task AdminDeleteUserAsync(string id) => SendAsync(HttpMethod.Delete, $"api/admin/users/{id}");

    public Task<UserDto> AdminResetPasswordAsync(string id, string password) =>
        SendAsync<UserDto>(HttpMethod.Post, $"api/admin/users/{id}/password", new PasswordRequest { Password = password });

    public Task<UserDto> AdminSetAdminAsync(string id, bool isAdmin) =>
        SendAsync<UserDto>(HttpMethod.Post, $"api/admin/users/{id}/admin", new SetAdminRequest { IsAdmin = isAdmin });

    public Task<ServerSettingsDto> AdminSettingsAsync() => SendAsync<ServerSettingsDto>(HttpMethod.Get, "api/admin/settings");

    public Task<ServerUpdateDto> AdminUpdateStatusAsync() => SendAsync<ServerUpdateDto>(HttpMethod.Get, "api/admin/update");

    public Task<ServerUpdateDto> AdminCheckUpdateAsync() => SendAsync<ServerUpdateDto>(HttpMethod.Post, "api/admin/update/check");

    public Task<ServerUpdateDto> AdminInstallUpdateAsync() => SendAsync<ServerUpdateDto>(HttpMethod.Post, "api/admin/update/install");

    public Task<ServerSettingsDto> AdminSaveSettingsAsync(ServerSettingsDto s) =>
        SendAsync<ServerSettingsDto>(HttpMethod.Put, "api/admin/settings", s);

    #endregion

    public void Dispose()
    {
        _http.Dispose();
        _ca.Dispose();
    }

    /// <summary>Lese-Stream, der den Fortschritt meldet.</summary>
    private sealed class ProgressStream(Stream inner, IProgress<long>? progress) : Stream
    {
        private long _read;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => _read; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => Report(inner.Read(buffer, offset, count));
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => Report(await inner.ReadAsync(buffer, ct));
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => Report(await inner.ReadAsync(buffer.AsMemory(offset, count), ct));
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private int Report(int n)
        {
            _read += n;
            progress?.Report(_read);
            return n;
        }
    }
}

/// <summary>Speichert Anmelde-Token der Server verschlüsselt in der Windows-Anmeldeinformationsverwaltung.</summary>
public static class TokenStore
{
    private const int CRED_TYPE_GENERIC = 1;
    private const int CRED_PERSIST_LOCAL_MACHINE = 2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public int Flags;
        public int Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string? UserName;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref CREDENTIAL credential, int flags);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, int type, int flags, out IntPtr credential);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, int type, int flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);

    private static string Target(string serverId) => $"NetworkFolderManager:server:{serverId}{SettingsService.Profile}";

    public static void Save(string serverId, string username, string token)
    {
        var blob = Encoding.UTF8.GetBytes(token);
        var ptr = Marshal.AllocHGlobal(blob.Length);
        try
        {
            Marshal.Copy(blob, 0, ptr, blob.Length);
            var cred = new CREDENTIAL
            {
                Type = CRED_TYPE_GENERIC,
                TargetName = Target(serverId),
                UserName = username,
                CredentialBlob = ptr,
                CredentialBlobSize = blob.Length,
                Persist = CRED_PERSIST_LOCAL_MACHINE,
                Comment = "NetworkFolderManager – Anmeldung am Server"
            };
            if (!CredWrite(ref cred, 0))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    public static string? Load(string serverId)
    {
        if (!CredRead(Target(serverId), CRED_TYPE_GENERIC, 0, out var ptr))
            return null;
        try
        {
            var cred = Marshal.PtrToStructure<CREDENTIAL>(ptr);
            if (cred.CredentialBlobSize <= 0)
                return null;
            var blob = new byte[cred.CredentialBlobSize];
            Marshal.Copy(cred.CredentialBlob, blob, 0, blob.Length);
            return Encoding.UTF8.GetString(blob);
        }
        finally
        {
            CredFree(ptr);
        }
    }

    public static void Delete(string serverId) => CredDelete(Target(serverId), CRED_TYPE_GENERIC, 0);
}
