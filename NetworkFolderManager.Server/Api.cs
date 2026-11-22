using System.Text.Json;
using Microsoft.Net.Http.Headers;
using SameSiteMode = Microsoft.AspNetCore.Http.SameSiteMode;
using NetworkFolderManager.Models;

namespace NetworkFolderManager.Server;

/// <summary>
/// JSON-API unter /api. Anmeldung per "Authorization: Bearer &lt;Token&gt;" (Windows-App) oder Cookie (Web-App).
/// Geöffnete Ordner per Header "X-Folder-Token" bzw. Cookie (nur für den Pfad dieses Ordners).
/// </summary>
public static class Api
{
    private const string SessionCookie = "nfm_session";
    private const string FolderCookie = "nfm_folder";

    public static void Map(WebApplication app, ServerState s)
    {
        var api = app.MapGroup("/api");

        // ---------- Allgemein ----------
        api.MapGet("/info", () => Results.Json(new ServerInfoDto
        {
            Id = s.ServerId,
            Name = s.ServerName,
            Version = ServerState.Version,
            Fingerprint = s.Certificates.Fingerprint,
            AllowRegistration = s.AllowRegistration,
            NeedsSetup = !s.HasAdmin
        }));

        api.MapGet("/servers", (HttpContext ctx) => Run(ctx, s, _ =>
        {
            var list = (s.Discovery?.KnownServers() ?? Array.Empty<ServerAnnouncement>())
                .Select(a => new KnownServerDto
                {
                    Id = a.Id,
                    Name = a.Name,
                    Address = a.Address,
                    Port = a.Port,
                    Fingerprint = a.Fp,
                    Url = $"https://{a.Address}:{a.Port}/"
                }).ToList();
            return Task.FromResult(Results.Json(list));
        }, requireAccount: false));

        // ---------- Anmeldung ----------
        api.MapPost("/auth/register", (HttpContext ctx) => Run(ctx, s, async _ =>
        {
            var req = await Body<RegisterRequest>(ctx);
            var user = s.Register(req);
            return SignIn(ctx, s, user, req.Device);
        }, requireAccount: false));

        api.MapPost("/auth/login", (HttpContext ctx) => Run(ctx, s, async _ =>
        {
            var req = await Body<LoginRequest>(ctx);
            var limiterKey = "login:" + ClientIp(ctx);
            if (s.Limiter.IsBlocked(limiterKey, out var wait))
                throw new VaultException(429, "tooManyAttempts", $"Zu viele Fehlversuche. Bitte in {Math.Ceiling(wait.TotalMinutes)} Minute(n) erneut versuchen.");

            var user = s.FindUser(req.Username?.Trim());
            if (user == null || user.Id == req.Username || !Passwords.Verify(req.Password ?? "", user.PasswordHash))
            {
                s.Limiter.Fail(limiterKey);
                throw new VaultException(401, "badLogin", "Benutzername oder Passwort ist falsch.");
            }
            s.Limiter.Success(limiterKey);
            return SignIn(ctx, s, user, req.Device);
        }, requireAccount: false));

        api.MapPost("/auth/logout", (HttpContext ctx) => Run(ctx, s, _ =>
        {
            s.Sessions.Revoke(SessionToken(ctx));
            ctx.Response.Cookies.Delete(SessionCookie, new CookieOptions { Path = "/", Secure = true, SameSite = SameSiteMode.Strict });
            return Task.FromResult(Results.NoContent());
        }, allowMustChange: true));

        api.MapGet("/auth/me", (HttpContext ctx) => Run(ctx, s, user => Task.FromResult(Results.Json(s.ToDto(user))), allowMustChange: true));

        api.MapPost("/auth/password", (HttpContext ctx) => Run(ctx, s, async user =>
        {
            var req = await Body<ChangePasswordRequest>(ctx);
            if (!Passwords.Verify(req.OldPassword ?? "", user.PasswordHash))
                throw new VaultException(403, "wrongPassword", "Das bisherige Passwort ist falsch.");
            if (req.OldPassword == req.NewPassword)
                throw new VaultException(400, "samePassword", "Das neue Passwort muss sich vom bisherigen unterscheiden.");
            s.SetUserPassword(user.Id, req.NewPassword, mustChange: false);
            s.Sessions.RevokeUser(user.Id, exceptToken: SessionToken(ctx));
            return Results.Json(s.ToDto(s.FindUser(user.Id)!));
        }, allowMustChange: true));

        // ---------- Ordner ----------
        api.MapGet("/folders", (HttpContext ctx) => Run(ctx, s, user =>
        {
            var list = s.Folders.Read(db => db.Folders.ToList())
                .Select(f => s.ToDto(f, user.Id))
                .OrderByDescending(f => f.IsMine).ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return Task.FromResult(Results.Json(list));
        }));

        api.MapPost("/folders", (HttpContext ctx) => Run(ctx, s, async user =>
        {
            var req = await Body<CreateFolderRequest>(ctx);
            var folder = s.CreateFolder(req.Name, req.Password, user, user, byAdminForOther: false);
            return Results.Json(s.ToDto(folder, user.Id), statusCode: 201);
        }));

        api.MapDelete("/folders/{id}", (HttpContext ctx, string id) => Run(ctx, s, async user =>
        {
            var f = s.FindFolder(id) ?? throw new VaultException(404, "notFound", "Ordner nicht gefunden.");
            if (f.OwnerId != user.Id)
                throw new VaultException(403, "notOwner", "Nur der Besitzer kann den Ordner löschen.");
            var req = await Body<PasswordRequest>(ctx);
            if (VaultManager.UnwrapKey(f, req.Password ?? "") == null)
                throw new VaultException(403, "wrongPassword", "Falsches Ordnerpasswort.");
            s.DeleteFolder(f.Id);
            return Results.NoContent();
        }));

        api.MapPost("/folders/{id}/unlock", (HttpContext ctx, string id) => Run(ctx, s, async user =>
        {
            var f = s.FindFolder(id) ?? throw new VaultException(404, "notFound", "Ordner nicht gefunden.");
            var req = await Body<PasswordRequest>(ctx);
            if (f.MustChangePassword && f.OwnerId != user.Id)
                throw new VaultException(403, "notOwner", "Dieser Ordner muss zuerst von seinem Besitzer geöffnet werden.");
            s.Unlock(f, req.Password ?? "", $"folder:{f.Id}:{ClientIp(ctx)}");
            var token = s.Vaults.IssueToken(f, user.Id);
            SetFolderCookie(ctx, f.Id, token);
            return Results.Json(new UnlockResponse
            {
                FolderToken = token,
                ExpiresInSeconds = (int)VaultManager.TokenLifetime.TotalSeconds,
                FolderName = f.Name,
                DavUrl = DavUrl(ctx, f)
            });
        }));

        api.MapPost("/folders/{id}/lock", (HttpContext ctx, string id) => Run(ctx, s, _ =>
        {
            s.Vaults.RevokeToken(FolderToken(ctx));
            ctx.Response.Cookies.Delete(FolderCookie, FolderCookieOptions(id));
            return Task.FromResult(Results.NoContent());
        }));

        api.MapPost("/folders/{id}/password", (HttpContext ctx, string id) => Run(ctx, s, async user =>
        {
            var f = s.FindFolder(id) ?? throw new VaultException(404, "notFound", "Ordner nicht gefunden.");
            if (f.OwnerId != user.Id)
                throw new VaultException(403, "notOwner", "Nur der Besitzer kann das Ordnerpasswort ändern.");
            var req = await Body<ChangePasswordRequest>(ctx);
            f = s.ChangeFolderPassword(f, req.OldPassword ?? "", req.NewPassword ?? "", $"folder:{f.Id}:{ClientIp(ctx)}");

            // Direkt geöffnet weitermachen
            s.Unlock(f, req.NewPassword!, $"folder:{f.Id}:{ClientIp(ctx)}");
            var token = s.Vaults.IssueToken(f, user.Id);
            SetFolderCookie(ctx, f.Id, token);
            return Results.Json(new UnlockResponse
            {
                FolderToken = token,
                ExpiresInSeconds = (int)VaultManager.TokenLifetime.TotalSeconds,
                FolderName = f.Name,
                DavUrl = DavUrl(ctx, f)
            });
        }));

        // ---------- Inhalt eines geöffneten Ordners ----------
        api.MapGet("/folders/{id}/items", (HttpContext ctx, string id, string? path) => RunFolder(ctx, s, id, (_, folder) =>
        {
            var dir = VaultFolder.Normalize(path);
            var items = folder.List(dir).Select(ToItem)
                .OrderByDescending(i => i.IsDir).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();
            return Task.FromResult(Results.Json(items));
        }));

        api.MapGet("/folders/{id}/media", (HttpContext ctx, string id) => RunFolder(ctx, s, id, (_, folder) =>
        {
            var items = folder.AllFiles().Select(ToItem)
                .Where(i => i.Kind is "image" or "video")
                .Where(i => !i.Name.StartsWith("._", StringComparison.Ordinal))
                .OrderByDescending(i => i.Modified).ToList();
            return Task.FromResult(Results.Json(items));
        }));

        api.MapGet("/folders/{id}/stats", (HttpContext ctx, string id) => RunFolder(ctx, s, id, (_, folder) =>
        {
            var files = folder.AllFiles();
            var (_, dirs, bytes) = folder.Stats();
            return Task.FromResult(Results.Json(new FolderStatsDto
            {
                Files = files.Count,
                Folders = dirs,
                Bytes = bytes,
                Photos = files.Count(f => VaultKinds.Kind(f.Name, false) == "image"),
                Videos = files.Count(f => VaultKinds.Kind(f.Name, false) == "video")
            }));
        }));

        api.MapMethods("/folders/{id}/file", new[] { "GET", "HEAD" }, (HttpContext ctx, string id, string? path, int? download) =>
            RunFolder(ctx, s, id, (_, folder) =>
            {
                var p = VaultFolder.Normalize(path);
                var entry = folder.Get(p) ?? throw VaultException.NotFound(p);
                if (entry.Dir)
                    throw new VaultException(400, "isDir", "Das ist ein Ordner.");
                return Task.FromResult(FileResult(ctx, folder, entry, download == 1));
            }));

        api.MapPut("/folders/{id}/file", (HttpContext ctx, string id, string? path, string? mode, long? mtime) =>
            RunFolder(ctx, s, id, async (_, folder) =>
            {
                var p = VaultFolder.Normalize(path);
                DateTime? modified = mtime is > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(mtime.Value).UtcDateTime : null;
                var (entry, created) = await folder.PutAsync(p, ctx.Request.Body,
                    overwrite: mode == "overwrite", autoRename: mode is null or "" or "rename", modified, ctx.RequestAborted);
                return Results.Json(ToItem(entry), statusCode: created ? 201 : 200);
            }));

        api.MapPost("/folders/{id}/mkdir", (HttpContext ctx, string id) => RunFolder(ctx, s, id, async (_, folder) =>
        {
            var req = await Body<PathRequest>(ctx);
            var entry = folder.CreateDirectory(VaultFolder.Normalize(req.Path));
            return Results.Json(ToItem(entry), statusCode: 201);
        }));

        api.MapPost("/folders/{id}/move", (HttpContext ctx, string id) => RunFolder(ctx, s, id, async (_, folder) =>
        {
            var req = await Body<MoveRequest>(ctx);
            var to = VaultFolder.Normalize(req.To);
            folder.Move(VaultFolder.Normalize(req.From), to, req.Overwrite);
            return Results.Json(ToItem(folder.Get(to)!));
        }));

        api.MapPost("/folders/{id}/copy", (HttpContext ctx, string id) => RunFolder(ctx, s, id, async (_, folder) =>
        {
            var req = await Body<MoveRequest>(ctx);
            var to = VaultFolder.Normalize(req.To);
            folder.Copy(VaultFolder.Normalize(req.From), to, req.Overwrite);
            return Results.Json(ToItem(folder.Get(to)!));
        }));

        api.MapPost("/folders/{id}/delete", (HttpContext ctx, string id) => RunFolder(ctx, s, id, async (_, folder) =>
        {
            var req = await Body<PathRequest>(ctx);
            folder.Delete(VaultFolder.Normalize(req.Path));
            return Results.NoContent();
        }));

        // ---------- Admin ----------
        var admin = api.MapGroup("/admin");

        admin.MapGet("/folders", (HttpContext ctx) => Run(ctx, s, user =>
            Task.FromResult(Results.Json(s.Folders.Read(db => db.Folders.ToList())
                .Select(f => s.ToDto(f, user.Id, admin: true))
                .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList())), adminOnly: true));

        admin.MapPost("/folders", (HttpContext ctx) => Run(ctx, s, async user =>
        {
            var req = await Body<CreateFolderRequest>(ctx);
            var owner = s.FindUser(req.Owner?.Trim()) ?? throw new VaultException(404, "noUser", "Diesen Benutzer gibt es nicht.");
            var folder = s.CreateFolder(req.Name, req.Password, owner, user, byAdminForOther: owner.Id != user.Id);
            return Results.Json(s.ToDto(folder, user.Id, admin: true), statusCode: 201);
        }, adminOnly: true));

        admin.MapDelete("/folders/{id}", (HttpContext ctx, string id) => Run(ctx, s, _ =>
        {
            s.DeleteFolder(id);
            return Task.FromResult(Results.NoContent());
        }, adminOnly: true));

        admin.MapPost("/folders/{id}/owner", (HttpContext ctx, string id) => Run(ctx, s, async user =>
        {
            var req = await Body<SetOwnerRequest>(ctx);
            var owner = s.FindUser(req.Username?.Trim()) ?? throw new VaultException(404, "noUser", "Diesen Benutzer gibt es nicht.");
            s.SetFolderOwner(id, owner.Id);
            return Results.Json(s.ToDto(s.FindFolder(id)!, user.Id, admin: true));
        }, adminOnly: true));

        admin.MapGet("/users", (HttpContext ctx) => Run(ctx, s, _ =>
            Task.FromResult(Results.Json(s.Users.Read(db => db.Users.ToList())
                .Select(u => s.ToDto(u, withFolderCount: true))
                .OrderBy(u => u.Username, StringComparer.OrdinalIgnoreCase).ToList())), adminOnly: true));

        admin.MapPost("/users", (HttpContext ctx) => Run(ctx, s, async _ =>
        {
            var req = await Body<AdminUserRequest>(ctx);
            var user = s.CreateUser(req.Username, req.Password, req.DisplayName, req.IsAdmin, mustChange: true);
            return Results.Json(s.ToDto(user, true), statusCode: 201);
        }, adminOnly: true));

        admin.MapDelete("/users/{id}", (HttpContext ctx, string id) => Run(ctx, s, user =>
        {
            if (id == user.Id)
                throw new VaultException(409, "self", "Du kannst dein eigenes Konto hier nicht löschen.");
            s.DeleteUser(id);
            return Task.FromResult(Results.NoContent());
        }, adminOnly: true));

        admin.MapPost("/users/{id}/password", (HttpContext ctx, string id) => Run(ctx, s, async _ =>
        {
            var req = await Body<PasswordRequest>(ctx);
            s.SetUserPassword(id, req.Password, mustChange: true);
            s.Sessions.RevokeUser(id);
            return Results.Json(s.ToDto(s.FindUser(id)!, true));
        }, adminOnly: true));

        admin.MapPost("/users/{id}/admin", (HttpContext ctx, string id) => Run(ctx, s, async _ =>
        {
            var req = await Body<SetAdminRequest>(ctx);
            s.SetAdmin(id, req.IsAdmin);
            return Results.Json(s.ToDto(s.FindUser(id)!, true));
        }, adminOnly: true));

        admin.MapGet("/settings", (HttpContext ctx) => Run(ctx, s, _ =>
            Task.FromResult(Results.Json(new ServerSettingsDto { Name = s.ServerName, AllowRegistration = s.AllowRegistration })), adminOnly: true));

        admin.MapPut("/settings", (HttpContext ctx) => Run(ctx, s, async _ =>
        {
            var req = await Body<ServerSettingsDto>(ctx);
            var name = (req.Name ?? "").Trim();
            if (name.Length is 0 or > 64)
                throw new VaultException(400, "badName", "Bitte einen Servernamen (1–64 Zeichen) angeben.");
            s.Config.Update(c =>
            {
                c.Name = name;
                c.AllowRegistration = req.AllowRegistration;
            });
            return Results.Json(new ServerSettingsDto { Name = s.ServerName, AllowRegistration = s.AllowRegistration });
        }, adminOnly: true));

        api.MapFallback((HttpContext ctx) => Results.Json(new ErrorDto { Error = "Unbekannte API-Adresse.", Code = "notFound" }, statusCode: 404));
    }

    #region Hilfen

    /// <summary>Führt einen Handler aus und schreibt das Ergebnis selbst (so funktionieren auch Handler, die nur HttpContext haben).</summary>
    private static async Task Run(HttpContext ctx, ServerState s, Func<UserRecord, Task<IResult>> action,
        bool requireAccount = true, bool adminOnly = false, bool allowMustChange = false)
    {
        var result = await RunCore(ctx, s, action, requireAccount, adminOnly, allowMustChange);
        await result.ExecuteAsync(ctx);
    }

    private static async Task<IResult> RunCore(HttpContext ctx, ServerState s, Func<UserRecord, Task<IResult>> action,
        bool requireAccount, bool adminOnly, bool allowMustChange)
    {
        try
        {
            // CSRF-Schutz: ändernde Aufrufe nur mit eigenem Header (Browser senden ihn nie von fremden Seiten)
            if (!HttpMethods.IsGet(ctx.Request.Method) && !HttpMethods.IsHead(ctx.Request.Method) &&
                !ctx.Request.Headers.ContainsKey(VaultProtocol.ClientHeader))
                throw new VaultException(403, "csrf", "Anfrage abgelehnt (fehlender Header).");

            UserRecord user = null!;
            if (requireAccount || adminOnly)
            {
                var userId = s.Sessions.Validate(SessionToken(ctx));
                user = s.FindUser(userId) ?? throw new VaultException(401, "unauthorized", "Bitte anmelden.");
                if (user.Id != userId)
                    throw new VaultException(401, "unauthorized", "Bitte anmelden.");
                if (user.MustChangePassword && !allowMustChange)
                    throw new VaultException(403, "mustChangeAccountPassword", "Bitte zuerst ein eigenes Passwort für dein Profil festlegen.");
                if (adminOnly && !user.IsAdmin)
                    throw new VaultException(403, "notAdmin", "Nur für Admins.");
            }
            return await action(user);
        }
        catch (VaultException ex)
        {
            if (ex.Status == 429)
                ctx.Response.Headers.RetryAfter = "60";
            return Results.Json(new ErrorDto { Error = ex.Message, Code = ex.Code }, statusCode: ex.Status);
        }
        catch (JsonException)
        {
            return Results.Json(new ErrorDto { Error = "Ungültige Anfrage.", Code = "badRequest" }, statusCode: 400);
        }
        catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested)
        {
            return Results.Empty;
        }
        catch (IOException ex)
        {
            ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Api").LogWarning(ex, "E/A-Fehler");
            return Results.Json(new ErrorDto { Error = "Speicherfehler auf dem Server: " + ex.Message, Code = "io" }, statusCode: 500);
        }
    }

    private static Task RunFolder(HttpContext ctx, ServerState s, string id, Func<UserRecord, VaultFolder, Task<IResult>> action) =>
        Run(ctx, s, async user =>
        {
            var f = s.FindFolder(id) ?? throw new VaultException(404, "notFound", "Ordner nicht gefunden.");
            var folder = s.Vaults.Resolve(FolderToken(ctx), f, user.Id)
                         ?? throw new VaultException(401, "folderLocked", "Der Ordner ist gesperrt. Bitte erneut das Ordnerpasswort eingeben.");
            return await action(user, folder);
        });

    private static IResult SignIn(HttpContext ctx, ServerState s, UserRecord user, string? device)
    {
        var token = s.Sessions.Create(user.Id, device ?? ctx.Request.Headers.UserAgent.ToString());
        s.TouchLogin(user.Id);
        ctx.Response.Cookies.Append(SessionCookie, token, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Strict,
            Path = "/",
            MaxAge = SessionStore.IdleLifetime
        });
        return Results.Json(new AuthResponse { Token = token, User = s.ToDto(s.FindUser(user.Id)!) });
    }

    private static CookieOptions FolderCookieOptions(string folderId) => new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Strict,
        Path = "/api/folders/" + folderId
    };

    private static void SetFolderCookie(HttpContext ctx, string folderId, string token) =>
        ctx.Response.Cookies.Append(FolderCookie, token, FolderCookieOptions(folderId));

    private static string? SessionToken(HttpContext ctx)
    {
        var auth = ctx.Request.Headers.Authorization.ToString();
        if (auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return auth[7..].Trim();
        return ctx.Request.Cookies[SessionCookie];
    }

    private static string? FolderToken(HttpContext ctx)
    {
        var header = ctx.Request.Headers[VaultProtocol.FolderTokenHeader].ToString();
        return header.Length > 0 ? header : ctx.Request.Cookies[FolderCookie];
    }

    public static string ClientIp(HttpContext ctx) => ctx.Connection.RemoteIpAddress?.MapToIPv4().ToString() ?? "?";

    private static string DavUrl(HttpContext ctx, FolderRecord f) =>
        $"https://{ctx.Request.Host}/dav/{Uri.EscapeDataString(f.Name)}/";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static async Task<T> Body<T>(HttpContext ctx) where T : new()
    {
        if (ctx.Request.ContentLength is > 64 * 1024)
            throw new VaultException(413, "tooLarge", "Anfrage zu groß.");
        return await JsonSerializer.DeserializeAsync<T>(ctx.Request.Body, JsonOptions, ctx.RequestAborted) ?? new T();
    }

    public static ItemDto ToItem(IndexEntry e) => new()
    {
        Name = e.Name,
        Path = e.Path,
        IsDir = e.Dir,
        Size = e.Size,
        Modified = DateTime.SpecifyKind(e.Modified, DateTimeKind.Utc),
        Created = DateTime.SpecifyKind(e.Created, DateTimeKind.Utc),
        ContentType = e.Dir ? "" : VaultKinds.ContentType(e.Name),
        Kind = VaultKinds.Kind(e.Name, e.Dir)
    };

    /// <summary>Datei ausliefern (mit Range-Unterstützung). Unsichere Typen (HTML, SVG, ...) nur als Download.</summary>
    public static IResult FileResult(HttpContext ctx, VaultFolder folder, IndexEntry entry, bool forceDownload)
    {
        var contentType = VaultKinds.ContentType(entry.Name);
        var safeInline = (contentType.StartsWith("image/") && contentType != "image/svg+xml") ||
                         contentType.StartsWith("video/") || contentType.StartsWith("audio/") ||
                         contentType is "application/pdf" or "text/plain";
        ctx.Response.Headers.ContentSecurityPolicy = "sandbox; default-src 'none'; img-src 'self' data:; media-src 'self'; style-src 'unsafe-inline'";
        ctx.Response.Headers.CacheControl = "private, max-age=3600";
        var stream = folder.OpenRead(entry);
        return Results.File(stream,
            safeInline ? contentType : "application/octet-stream",
            forceDownload || !safeInline ? entry.Name : null,
            new DateTimeOffset(DateTime.SpecifyKind(entry.Modified, DateTimeKind.Utc)),
            new EntityTagHeaderValue($"\"{entry.Blob}\""),
            enableRangeProcessing: true);
    }

    #endregion
}
