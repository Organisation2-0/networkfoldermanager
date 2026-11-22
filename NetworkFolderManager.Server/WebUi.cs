using System.Reflection;

namespace NetworkFolderManager.Server;

/// <summary>Liefert die eingebettete Web-App (für macOS, Linux, Handy – jeder Browser) und das CA-Zertifikat aus.</summary>
public static class WebUi
{
    private static readonly Dictionary<string, (byte[] Data, string Type)> Files = Load();

    private static Dictionary<string, (byte[], string)> Load()
    {
        var result = new Dictionary<string, (byte[], string)>(StringComparer.OrdinalIgnoreCase);
        var asm = Assembly.GetExecutingAssembly();
        foreach (var name in asm.GetManifestResourceNames().Where(n => n.StartsWith("www/", StringComparison.Ordinal)))
        {
            using var stream = asm.GetManifestResourceStream(name)!;
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            var file = name[4..];
            var type = Path.GetExtension(file).ToLowerInvariant() switch
            {
                ".html" => "text/html; charset=utf-8",
                ".js" => "text/javascript; charset=utf-8",
                ".css" => "text/css; charset=utf-8",
                ".svg" => "image/svg+xml",
                ".png" => "image/png",
                ".webmanifest" => "application/manifest+json",
                _ => "application/octet-stream"
            };
            result[file] = (ms.ToArray(), type);
        }
        return result;
    }

    public static void Map(WebApplication app, ServerState s)
    {
        app.MapGet("/ca.crt", (HttpContext ctx) =>
        {
            ctx.Response.Headers.ContentDisposition = "attachment; filename=\"NFM-Server-CA.crt\"";
            return Results.Bytes(s.Certificates.CaPem(), "application/x-x509-ca-cert");
        });

        app.MapGet("/{file?}", (HttpContext ctx, string? file) =>
        {
            file = string.IsNullOrEmpty(file) ? "index.html" : file;
            if (!Files.TryGetValue(file, out var f))
            {
                // Unbekannte Pfade → App (Client-seitige Navigation)
                if (Path.HasExtension(file) || !Files.TryGetValue("index.html", out f))
                    return Results.NotFound();
            }
            ctx.Response.Headers.CacheControl = file.EndsWith(".html") ? "no-cache" : "public, max-age=300";
            return Results.Bytes(f.Data, f.Type);
        });
    }

    /// <summary>Sicherheits-Header für alle Antworten.</summary>
    public static async Task SecurityHeaders(HttpContext ctx, Func<Task> next)
    {
        var h = ctx.Response.Headers;
        h.XContentTypeOptions = "nosniff";
        h["Referrer-Policy"] = "no-referrer";
        h.XFrameOptions = "DENY";
        h["Strict-Transport-Security"] = "max-age=31536000";
        if (!ctx.Request.Path.StartsWithSegments("/api/folders") && !ctx.Request.Path.StartsWithSegments("/dav"))
            h.ContentSecurityPolicy = "default-src 'self'; img-src 'self' blob: data:; media-src 'self' blob:; " +
                                      "style-src 'self' 'unsafe-inline'; script-src 'self'; connect-src 'self'; " +
                                      "frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
        await next();
    }
}
