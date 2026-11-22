using System.Globalization;
using System.Net;
using System.Text;
using System.Xml.Linq;

namespace NetworkFolderManager.Server;

/// <summary>
/// WebDAV unter /dav/&lt;Ordnername&gt;/ – damit lässt sich jeder Server-Ordner als Netzlaufwerk einbinden:
///   Windows: net use Z: https://server:45830/dav/Ordner  (Explorer → „Netzlaufwerk verbinden“)
///   macOS:   Finder → Gehe zu → Mit Server verbinden → https://server:45830/dav/Ordner
/// Anmeldung: beliebiger Benutzername (z.B. der Ordnername) + Ordnerpasswort.
/// </summary>
public static class WebDav
{
    private static readonly XNamespace D = "DAV:";

    public static void Map(WebApplication app, ServerState s)
    {
        app.Map("/dav", branch => branch.Run(ctx => HandleAsync(ctx, s)));
    }

    private static async Task HandleAsync(HttpContext ctx, ServerState s)
    {
        var method = ctx.Request.Method.ToUpperInvariant();
        ctx.Response.Headers["DAV"] = "1, 2";
        ctx.Response.Headers["MS-Author-Via"] = "DAV";

        if (method == "OPTIONS")
        {
            ctx.Response.Headers.Allow = "OPTIONS, GET, HEAD, PUT, DELETE, PROPFIND, PROPPATCH, MKCOL, COPY, MOVE, LOCK, UNLOCK";
            ctx.Response.StatusCode = 200;
            return;
        }

        // Rohen (noch kodierten) Pfad verwenden, damit Namen mit "%" korrekt bleiben
        var raw = ctx.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpRequestFeature>()?.RawTarget
                  ?? (ctx.Request.PathBase + ctx.Request.Path).ToString();
        if (raw.StartsWith("http", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(raw, UriKind.Absolute, out var abs))
            raw = abs.AbsolutePath;
        var segments = raw.Split('?')[0].Split('/', StringSplitOptions.RemoveEmptyEntries).Skip(1).ToArray();
        if (segments.Length == 0)
        {
            await RootAsync(ctx, method);
            return;
        }

        var folderName = Uri.UnescapeDataString(segments[0]);
        var record = s.FindFolder(folderName);
        if (record == null || !string.Equals(record.Name, folderName, StringComparison.OrdinalIgnoreCase))
        {
            ctx.Response.StatusCode = 404;
            return;
        }

        var folder = Authenticate(ctx, s, record);
        if (folder == null)
            return;

        string path;
        try
        {
            path = VaultFolder.Normalize(string.Join('/', segments.Skip(1).Select(Uri.UnescapeDataString)));
        }
        catch (VaultException)
        {
            ctx.Response.StatusCode = 400;
            return;
        }

        try
        {
            switch (method)
            {
                case "PROPFIND": await PropFindAsync(ctx, s, record, folder, path); break;
                case "PROPPATCH": await PropPatchAsync(ctx, record, folder, path); break;
                case "GET":
                case "HEAD": await GetAsync(ctx, folder, path); break;
                case "PUT": await PutAsync(ctx, folder, path); break;
                case "DELETE": folder.Delete(path); ctx.Response.StatusCode = 204; break;
                case "MKCOL": await MkColAsync(ctx, folder, path); break;
                case "MOVE":
                case "COPY": Transfer(ctx, record, folder, path, method == "COPY"); break;
                case "LOCK": await LockAsync(ctx, record, folder, path); break;
                case "UNLOCK": ctx.Response.StatusCode = 204; break;
                default: ctx.Response.StatusCode = 405; break;
            }
        }
        catch (VaultException ex)
        {
            ctx.Response.StatusCode = ex.Code switch
            {
                "notFound" => 404,
                "exists" => method is "MKCOL" ? 405 : 412,
                "noParent" => 409,
                _ => ex.Status
            };
        }
        catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested)
        {
            // Client hat abgebrochen
        }
    }

    /// <summary>Basic-Anmeldung mit dem Ordnerpasswort (Benutzername beliebig).</summary>
    private static VaultFolder? Authenticate(HttpContext ctx, ServerState s, FolderRecord record)
    {
        string? password = null;
        var auth = ctx.Request.Headers.Authorization.ToString();
        if (auth.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(auth[6..].Trim()));
                var colon = decoded.IndexOf(':');
                if (colon >= 0)
                    password = decoded[(colon + 1)..];
            }
            catch (FormatException)
            {
                // ungültig
            }
        }

        if (password != null)
        {
            var cached = s.Vaults.DavFromCache(record, password);
            if (cached != null)
                return cached;

            try
            {
                var folder = s.Unlock(record, password, $"dav:{record.Id}:{Api.ClientIp(ctx)}");
                s.Vaults.RememberDav(record, password);
                return folder;
            }
            catch (VaultException ex) when (ex.Code is "mustChangePassword")
            {
                ctx.Response.StatusCode = 403;
                return null;
            }
            catch (VaultException ex) when (ex.Code is "tooManyAttempts")
            {
                ctx.Response.StatusCode = 429;
                ctx.Response.Headers.RetryAfter = "300";
                return null;
            }
            catch (VaultException)
            {
                // falsches Passwort → erneut fragen
            }
        }

        ctx.Response.StatusCode = 401;
        ctx.Response.Headers.WWWAuthenticate = $"Basic realm=\"NetworkFolderManager - {record.Name}\", charset=\"UTF-8\"";
        return null;
    }

    private static async Task RootAsync(HttpContext ctx, string method)
    {
        if (method == "PROPFIND")
        {
            var xml = new StringBuilder();
            xml.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?><D:multistatus xmlns:D=\"DAV:\">");
            xml.Append("<D:response><D:href>/dav/</D:href><D:propstat><D:prop><D:displayname>dav</D:displayname>")
               .Append("<D:resourcetype><D:collection/></D:resourcetype></D:prop><D:status>HTTP/1.1 200 OK</D:status></D:propstat></D:response>");
            xml.Append("</D:multistatus>");
            await WriteXmlAsync(ctx, 207, xml.ToString());
            return;
        }
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "text/plain; charset=utf-8";
        await ctx.Response.WriteAsync("NetworkFolderManager WebDAV: /dav/<Ordnername>/");
    }

    private static string Href(FolderRecord record, string path, bool dir)
    {
        var sb = new StringBuilder("/dav/").Append(Uri.EscapeDataString(record.Name));
        foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
            sb.Append('/').Append(Uri.EscapeDataString(part));
        if (dir)
            sb.Append('/');
        return sb.ToString();
    }

    private static async Task PropFindAsync(HttpContext ctx, ServerState s, FolderRecord record, VaultFolder folder, string path)
    {
        var entry = folder.Get(path);
        if (entry == null)
        {
            ctx.Response.StatusCode = 404;
            return;
        }

        var depth = ctx.Request.Headers["Depth"].ToString().Trim();
        var entries = new List<IndexEntry> { entry };
        if (entry.Dir && depth != "0")
            entries.AddRange(folder.List(path));

        var xml = new StringBuilder();
        xml.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?><D:multistatus xmlns:D=\"DAV:\">");
        foreach (var e in entries)
        {
            var name = e.Path == "/" ? record.Name : e.Name;
            xml.Append("<D:response><D:href>").Append(Esc(Href(record, e.Path, e.Dir))).Append("</D:href><D:propstat><D:prop>");
            xml.Append("<D:displayname>").Append(Esc(name)).Append("</D:displayname>");
            if (e.Dir)
            {
                xml.Append("<D:resourcetype><D:collection/></D:resourcetype>");
            }
            else
            {
                xml.Append("<D:resourcetype/>");
                xml.Append("<D:getcontentlength>").Append(e.Size.ToString(CultureInfo.InvariantCulture)).Append("</D:getcontentlength>");
                xml.Append("<D:getcontenttype>").Append(Esc(Models.VaultKinds.ContentType(e.Name))).Append("</D:getcontenttype>");
                xml.Append("<D:getetag>\"").Append(e.Blob).Append("\"</D:getetag>");
            }
            xml.Append("<D:getlastmodified>").Append(DateTime.SpecifyKind(e.Modified, DateTimeKind.Utc).ToString("R", CultureInfo.InvariantCulture)).Append("</D:getlastmodified>");
            xml.Append("<D:creationdate>").Append(DateTime.SpecifyKind(e.Created, DateTimeKind.Utc).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)).Append("</D:creationdate>");
            xml.Append("<D:supportedlock><D:lockentry><D:lockscope><D:exclusive/></D:lockscope><D:locktype><D:write/></D:locktype></D:lockentry></D:supportedlock>");
            xml.Append("<D:lockdiscovery/>");
            if (e.Path == "/")
            {
                var (free, used) = Quota(s, folder);
                xml.Append("<D:quota-available-bytes>").Append(free).Append("</D:quota-available-bytes>");
                xml.Append("<D:quota-used-bytes>").Append(used).Append("</D:quota-used-bytes>");
            }
            xml.Append("</D:prop><D:status>HTTP/1.1 200 OK</D:status></D:propstat></D:response>");
        }
        xml.Append("</D:multistatus>");
        await WriteXmlAsync(ctx, 207, xml.ToString());
    }

    private static (long Free, long Used) Quota(ServerState s, VaultFolder folder)
    {
        long free = 0;
        try
        {
            free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(s.StorageRoot))!).AvailableFreeSpace;
        }
        catch
        {
            // unbekannt
        }
        return (free, folder.Stats().Bytes);
    }

    private static async Task PropPatchAsync(HttpContext ctx, FolderRecord record, VaultFolder folder, string path)
    {
        var entry = folder.Get(path);
        if (entry == null)
        {
            ctx.Response.StatusCode = 404;
            return;
        }

        var props = new List<XName>();
        var doc = await ReadXmlAsync(ctx);
        if (doc?.Root != null)
        {
            foreach (var prop in doc.Root.Descendants(D + "prop").SelectMany(p => p.Elements()))
            {
                props.Add(prop.Name);
                // Änderungszeit übernehmen (Windows: Win32LastModifiedTime, sonst getlastmodified)
                if (prop.Name.LocalName is "Win32LastModifiedTime" or "getlastmodified" &&
                    DateTime.TryParse(prop.Value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t))
                    folder.Touch(path, t);
            }
        }

        var xml = new StringBuilder();
        xml.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?><D:multistatus xmlns:D=\"DAV:\"><D:response><D:href>")
           .Append(Esc(Href(record, path, entry.Dir))).Append("</D:href><D:propstat><D:prop>");
        var i = 0;
        foreach (var p in props)
        {
            if (p.Namespace == D)
                xml.Append("<D:").Append(p.LocalName).Append("/>");
            else
                xml.Append("<x").Append(i).Append(':').Append(p.LocalName).Append(" xmlns:x").Append(i).Append("=\"").Append(Esc(p.NamespaceName)).Append("\"/>");
            i++;
        }
        xml.Append("</D:prop><D:status>HTTP/1.1 200 OK</D:status></D:propstat></D:response></D:multistatus>");
        await WriteXmlAsync(ctx, 207, xml.ToString());
    }

    private static async Task GetAsync(HttpContext ctx, VaultFolder folder, string path)
    {
        var entry = folder.Get(path);
        if (entry == null)
        {
            ctx.Response.StatusCode = 404;
            return;
        }
        if (entry.Dir)
        {
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "text/plain; charset=utf-8";
            if (ctx.Request.Method != "HEAD")
                await ctx.Response.WriteAsync(string.Join("\n", folder.List(path).Select(e => e.Name + (e.Dir ? "/" : ""))));
            return;
        }
        await Api.FileResult(ctx, folder, entry, forceDownload: false).ExecuteAsync(ctx);
    }

    private static async Task PutAsync(HttpContext ctx, VaultFolder folder, string path)
    {
        if (folder.Get(path) is { Dir: true })
        {
            ctx.Response.StatusCode = 405;
            return;
        }
        var (_, created) = await folder.PutAsync(path, ctx.Request.Body, overwrite: true, autoRename: false, null, ctx.RequestAborted);
        ctx.Response.StatusCode = created ? 201 : 204;
    }

    private static async Task MkColAsync(HttpContext ctx, VaultFolder folder, string path)
    {
        if (ctx.Request.ContentLength > 0)
        {
            ctx.Response.StatusCode = 415;
            return;
        }
        folder.CreateDirectory(path);
        ctx.Response.StatusCode = 201;
        await Task.CompletedTask;
    }

    private static void Transfer(HttpContext ctx, FolderRecord record, VaultFolder folder, string path, bool copy)
    {
        var destination = ctx.Request.Headers["Destination"].ToString();
        if (!Uri.TryCreate(destination, UriKind.RelativeOrAbsolute, out var uri))
        {
            ctx.Response.StatusCode = 400;
            return;
        }
        var absPath = uri.IsAbsoluteUri ? uri.AbsolutePath : destination.Split('?')[0];
        var parts = absPath.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToList();
        if (parts.Count < 2 || parts[0] != "dav" || !string.Equals(parts[1], record.Name, StringComparison.OrdinalIgnoreCase))
        {
            ctx.Response.StatusCode = 502; // anderer Ordner/Server wird nicht unterstützt
            return;
        }

        var target = VaultFolder.Normalize(string.Join('/', parts.Skip(2)));
        var overwrite = !ctx.Request.Headers["Overwrite"].ToString().Trim().Equals("F", StringComparison.OrdinalIgnoreCase);
        if (folder.Get(path) == null)
        {
            ctx.Response.StatusCode = 404;
            return;
        }
        var created = copy ? folder.Copy(path, target, overwrite) : folder.Move(path, target, overwrite);
        ctx.Response.StatusCode = created ? 201 : 204;
    }

    /// <summary>
    /// Einfache (exklusive, nicht erzwungene) Sperren – Windows und der macOS-Finder brauchen LOCK,
    /// um Laufwerke beschreibbar einzubinden.
    /// </summary>
    private static async Task LockAsync(HttpContext ctx, FolderRecord record, VaultFolder folder, string path)
    {
        var doc = await ReadXmlAsync(ctx);
        var token = "opaquelocktoken:" + Guid.NewGuid();
        var ifHeader = ctx.Request.Headers["If"].ToString();
        if (doc == null && ifHeader.Contains("opaquelocktoken:", StringComparison.Ordinal))
        {
            // Sperre verlängern
            var start = ifHeader.IndexOf("opaquelocktoken:", StringComparison.Ordinal);
            var end = ifHeader.IndexOf('>', start);
            if (end > start)
                token = ifHeader[start..end];
        }

        var status = 200;
        var entry = folder.Get(path);
        if (entry == null)
        {
            // "Lock-null": leere Datei anlegen
            using var empty = new MemoryStream();
            entry = (await folder.PutAsync(path, empty, overwrite: false, autoRename: false, null, ctx.RequestAborted)).Entry;
            status = 201;
        }

        var owner = doc?.Root?.Element(D + "owner");
        var xml = new StringBuilder();
        xml.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?><D:prop xmlns:D=\"DAV:\"><D:lockdiscovery><D:activelock>")
           .Append("<D:locktype><D:write/></D:locktype><D:lockscope><D:exclusive/></D:lockscope><D:depth>0</D:depth>");
        if (owner != null)
            xml.Append("<D:owner>").Append(Esc(owner.Value)).Append("</D:owner>");
        xml.Append("<D:timeout>Second-3600</D:timeout><D:locktoken><D:href>").Append(token).Append("</D:href></D:locktoken>")
           .Append("<D:lockroot><D:href>").Append(Esc(Href(record, path, entry.Dir))).Append("</D:href></D:lockroot>")
           .Append("</D:activelock></D:lockdiscovery></D:prop>");
        ctx.Response.Headers["Lock-Token"] = $"<{token}>";
        await WriteXmlAsync(ctx, status, xml.ToString());
    }

    private static async Task<XDocument?> ReadXmlAsync(HttpContext ctx)
    {
        if (ctx.Request.ContentLength is 0 or > 1024 * 1024)
            return null;
        using var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8);
        var text = await reader.ReadToEndAsync(ctx.RequestAborted);
        if (string.IsNullOrWhiteSpace(text) || text.Length > 1024 * 1024)
            return null;
        try
        {
            return XDocument.Parse(text);
        }
        catch
        {
            return null;
        }
    }

    private static async Task WriteXmlAsync(HttpContext ctx, int status, string xml)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/xml; charset=utf-8";
        var bytes = Encoding.UTF8.GetBytes(xml);
        ctx.Response.ContentLength = bytes.Length;
        await ctx.Response.Body.WriteAsync(bytes, ctx.RequestAborted);
    }

    private static string Esc(string s) => WebUtility.HtmlEncode(s);
}
