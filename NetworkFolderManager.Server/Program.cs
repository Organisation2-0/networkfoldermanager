using Microsoft.AspNetCore.Server.Kestrel.Https;
using NetworkFolderManager.Server;

// ---- Optionen ----
string? dataDir = null;
int? port = null;
var rest = new List<string>();
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--data" when i + 1 < args.Length:
            dataDir = args[++i];
            break;
        case "--port" when i + 1 < args.Length && int.TryParse(args[i + 1], out var p):
            port = p;
            i++;
            break;
        default:
            rest.Add(args[i]);
            break;
    }
}

// ---- Verwaltungsbefehle ----
if (rest.Count > 0 && rest[0] != "run")
    return Cli.Run(rest.ToArray(), dataDir);

// ---- Server ----
var paths = new ServerPaths(dataDir);
var state = new ServerState(paths, port);

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = Array.Empty<string>(),
    ContentRootPath = AppContext.BaseDirectory
});
builder.Host.UseSystemd();
builder.Host.UseWindowsService(o => o.ServiceName = "NFM-Server");
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);

builder.Services.AddSingleton(state);
builder.Services.AddHostedService<DiscoveryService>();
builder.Services.AddHostedService<MaintenanceService>();

using (var bootLog = LoggerFactory.Create(l => l.AddSimpleConsole()))
    state.InitCertificates(bootLog.CreateLogger("TLS"));

builder.WebHost.ConfigureKestrel(k =>
{
    k.AddServerHeader = false;
    k.Limits.MaxRequestBodySize = null; // große Dateien erlauben
    k.Limits.MinRequestBodyDataRate = null;
    k.ListenAnyIP(state.Port, listen => listen.UseHttps(new HttpsConnectionAdapterOptions
    {
        ServerCertificateSelector = (_, _) => state.Certificates.Current
    }));
});

var app = builder.Build();
var log = app.Logger;

app.Use(WebUi.SecurityHeaders);
WebDav.Map(app, state);
Api.Map(app, state);
WebUi.Map(app, state);

app.Lifetime.ApplicationStarted.Register(() =>
{
    log.LogInformation("NetworkFolderManager Server {Version} „{Name}“ läuft auf https://<IP-Adresse>:{Port}/",
        ServerState.Version, state.ServerName, state.Port);
    log.LogInformation("Datenordner: {Dir}", paths.DataDir);
    log.LogInformation("Zertifikat-Fingerabdruck: {Fp}", state.Certificates.FingerprintShort);
    if (state.EnsureSetupCode() is { } code)
        log.LogWarning("Noch kein Admin! Erstes Konto mit Setup-Code {Code} registrieren (oder: nfm-server admin add <Name>).", code);
});

app.Run();
return 0;

/// <summary>Aufräumen alle 60 Sekunden.</summary>
internal sealed class MaintenanceService(ServerState state, ILogger<MaintenanceService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(60), ct);
                state.Maintenance();
                state.EnsureSetupCode();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Wartung fehlgeschlagen");
            }
        }
    }
}
