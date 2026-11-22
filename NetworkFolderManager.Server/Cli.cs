using System.Text;

namespace NetworkFolderManager.Server;

/// <summary>Kommandozeilen-Befehle (Konten verwalten, Infos) – funktionieren auch, während der Server läuft.</summary>
public static class Cli
{
    public const string Usage = """
        NetworkFolderManager Server (nfm-server)

        Aufruf:
          nfm-server [run] [--data <Ordner>] [--port <Port>]   Server starten (Standard)
          nfm-server info                                     Adresse, Fingerabdruck, Datenordner, Setup-Code
          nfm-server admin add <Benutzer>                     Admin-Konto anlegen (fragt nach dem Passwort)
          nfm-server admin grant <Benutzer>                   Bestehendes Konto zum Admin machen
          nfm-server admin revoke <Benutzer>                  Admin-Rechte entziehen
          nfm-server user list                                Alle Konten anzeigen
          nfm-server user passwd <Benutzer>                   Passwort zurücksetzen (muss bei Anmeldung geändert werden)
          nfm-server user delete <Benutzer>                   Konto löschen (Ordner bleiben erhalten)
          nfm-server folder list                              Alle Ordner anzeigen
          nfm-server set name <Name>                          Servernamen ändern
          nfm-server set registration on|off                  Selbst-Registrierung erlauben/verbieten
          nfm-server set port <Port>                          HTTPS-Port ändern (Neustart nötig)
          nfm-server set storage <Ordner>                     Ablageort der Ordner (nur bei leerem Server!)

        Optionen:
          --data <Ordner>        Datenordner (Standard: siehe "info"; auch über NFM_SERVER_DATA)
          --password-stdin       Passwort aus der Standardeingabe lesen (für Skripte)

        Ordnerinhalte sind mit dem jeweiligen Ordnerpasswort verschlüsselt – auch Admins können sie nicht lesen.
        """;

    public static int Run(string[] args, string? dataDir)
    {
        var paths = new ServerPaths(dataDir);
        var state = new ServerState(paths);
        var passwordStdin = args.Contains("--password-stdin");
        args = args.Where(a => a != "--password-stdin").ToArray();

        try
        {
            switch (args)
            {
                case ["info"]:
                    state.InitCertificates(null);
                    Console.WriteLine($"Server:        {state.ServerName} (Version {ServerState.Version})");
                    Console.WriteLine($"Adresse:       https://<IP-Adresse>:{state.Port}/");
                    Console.WriteLine($"Datenordner:   {paths.DataDir}");
                    Console.WriteLine($"Ordnerablage:  {state.StorageRoot}");
                    Console.WriteLine($"Fingerabdruck: {state.Certificates.FingerprintShort}");
                    Console.WriteLine($"Registrierung: {(state.AllowRegistration ? "erlaubt" : "nur durch Admin")}");
                    Console.WriteLine($"Konten:        {state.Users.Read(db => db.Users.Count)} · Ordner: {state.Folders.Read(db => db.Folders.Count)}");
                    if (state.EnsureSetupCode() is { } code)
                        Console.WriteLine($"Setup-Code:    {code}   (noch kein Admin – beim Registrieren angeben)");
                    return 0;

                case ["admin", "add", var name]:
                {
                    var pw = ReadNewPassword(passwordStdin);
                    var user = state.CreateUser(name, pw, null, isAdmin: true, mustChange: false);
                    state.EnsureSetupCode();
                    Console.WriteLine($"Admin-Konto „{user.Username}“ angelegt.");
                    return 0;
                }

                case ["admin", "grant", var name]:
                    state.SetAdmin(RequireUser(state, name).Id, true);
                    state.EnsureSetupCode();
                    Console.WriteLine($"„{name}“ ist jetzt Admin.");
                    return 0;

                case ["admin", "revoke", var name]:
                    state.SetAdmin(RequireUser(state, name).Id, false);
                    Console.WriteLine($"„{name}“ ist kein Admin mehr.");
                    return 0;

                case ["user", "list"]:
                    foreach (var u in state.Users.Read(db => db.Users.ToList()).OrderBy(u => u.Username))
                        Console.WriteLine($"{u.Username,-32} {(u.IsAdmin ? "Admin" : "     ")}  angelegt {u.CreatedAt:yyyy-MM-dd}  letzte Anmeldung {u.LastLogin?.ToString("yyyy-MM-dd HH:mm") ?? "–"}");
                    return 0;

                case ["user", "passwd", var name]:
                {
                    var user = RequireUser(state, name);
                    var pw = ReadNewPassword(passwordStdin);
                    state.SetUserPassword(user.Id, pw, mustChange: true);
                    state.Sessions.RevokeUser(user.Id);
                    Console.WriteLine($"Passwort von „{user.Username}“ zurückgesetzt (muss bei der nächsten Anmeldung geändert werden).");
                    return 0;
                }

                case ["user", "delete", var name]:
                    state.DeleteUser(RequireUser(state, name).Id);
                    Console.WriteLine($"Konto „{name}“ gelöscht.");
                    return 0;

                case ["folder", "list"]:
                    foreach (var f in state.Folders.Read(db => db.Folders.ToList()).OrderBy(f => f.Name))
                        Console.WriteLine($"{f.Name,-40} Besitzer: {state.FindUser(f.OwnerId)?.Username ?? "–",-20} {(f.MustChangePassword ? "(noch nicht geöffnet)" : "")}");
                    return 0;

                case ["set", "name", var value]:
                    state.Config.Update(c => c.Name = value.Trim());
                    Console.WriteLine("Servername gespeichert.");
                    return 0;

                case ["set", "registration", var value]:
                    state.Config.Update(c => c.AllowRegistration = value is "on" or "an" or "true" or "1");
                    Console.WriteLine($"Selbst-Registrierung: {(state.AllowRegistration ? "erlaubt" : "aus")}.");
                    return 0;

                case ["set", "port", var value] when int.TryParse(value, out var port) && port is > 0 and < 65536:
                    state.Config.Update(c => c.Port = port);
                    Console.WriteLine("Port gespeichert – bitte den Server neu starten.");
                    return 0;

                case ["set", "storage", var value]:
                    if (state.Folders.Read(db => db.Folders.Count) > 0)
                    {
                        Console.Error.WriteLine("Es gibt bereits Ordner. Bitte den Server stoppen und den Ordner selbst verschieben.");
                        return 1;
                    }
                    state.Config.Update(c => c.StoragePath = Path.GetFullPath(value));
                    Console.WriteLine("Ablageort gespeichert – bitte den Server neu starten.");
                    return 0;

                default:
                    Console.WriteLine(Usage);
                    return args.Length == 0 || args[0] is "help" or "--help" or "-h" or "/?" ? 0 : 1;
            }
        }
        catch (VaultException ex)
        {
            Console.Error.WriteLine("Fehler: " + ex.Message);
            return 1;
        }
    }

    private static UserRecord RequireUser(ServerState s, string name) =>
        s.FindUser(name) ?? throw new VaultException(404, "noUser", $"Konto „{name}“ gibt es nicht.");

    private static string ReadNewPassword(bool fromStdin)
    {
        if (fromStdin || Console.IsInputRedirected)
            return Console.In.ReadLine() ?? "";

        while (true)
        {
            var pw = ReadHidden("Passwort: ");
            if (Passwords.Validate(pw) is { } error)
            {
                Console.WriteLine(error);
                continue;
            }
            if (ReadHidden("Passwort wiederholen: ") != pw)
            {
                Console.WriteLine("Die Passwörter stimmen nicht überein.");
                continue;
            }
            return pw;
        }
    }

    private static string ReadHidden(string prompt)
    {
        Console.Write(prompt);
        var sb = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
                break;
            if (key.Key == ConsoleKey.Backspace)
            {
                if (sb.Length > 0)
                    sb.Length--;
                continue;
            }
            if (!char.IsControl(key.KeyChar))
                sb.Append(key.KeyChar);
        }
        Console.WriteLine();
        return sb.ToString();
    }
}
