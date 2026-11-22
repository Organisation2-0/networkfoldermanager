using System.Text;

namespace NetworkFolderManager.Server;

/// <summary>Command line (manage accounts, show info) – works while the server is running.</summary>
public static class Cli
{
    public const string Usage = """
        NetworkFolderManager Server (nfm-server)

        Usage:
          nfm-server [run] [--data <dir>] [--port <port>]   Start the server (default)
          nfm-server info                                   Address, fingerprint, data directory, setup code
          nfm-server admin add <user>                       Create an admin account (asks for the password)
          nfm-server admin grant <user>                     Make an existing account an admin
          nfm-server admin revoke <user>                    Remove admin rights
          nfm-server user list                              List all accounts
          nfm-server user passwd <user>                     Reset a password (must be changed at the next sign-in)
          nfm-server user delete <user>                     Delete an account (its folders are kept)
          nfm-server folder list                            List all folders
          nfm-server set name <name>                        Change the server name
          nfm-server set registration on|off                Allow/forbid self-registration
          nfm-server set port <port>                        Change the HTTPS port (restart required)
          nfm-server set storage <dir>                      Folder storage location (only while the server has no folders!)
          nfm-server set autoupdate on|off                  Automatic updates from GitHub (default: on)
          nfm-server update                                 Check for a new version and install it now

        Options:
          --data <dir>           Data directory (default: see "info"; also via NFM_SERVER_DATA)
          --password-stdin       Read the password from standard input (for scripts)

        Folder contents are encrypted with the respective folder password – not even admins can read them.
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
                    Console.WriteLine($"Server:        {state.ServerName} (version {ServerState.Version})");
                    Console.WriteLine($"Address:       https://<ip-address>:{state.Port}/");
                    Console.WriteLine($"Data:          {paths.DataDir}");
                    Console.WriteLine($"Storage:       {state.StorageRoot}");
                    Console.WriteLine($"Fingerprint:   {state.Certificates.FingerprintShort}");
                    Console.WriteLine($"Registration:  {(state.AllowRegistration ? "open" : "admins only")}");
                    Console.WriteLine($"Auto-update:   {(state.Config.Read(c => c.AutoUpdate) ? "on" : "off")}{(ServerUpdater.Supported ? "" : " (not available for this build)")}");
                    Console.WriteLine($"Accounts:      {state.Users.Read(db => db.Users.Count)} · folders: {state.Folders.Read(db => db.Folders.Count)}");
                    if (state.EnsureSetupCode() is { } code)
                        Console.WriteLine($"Setup code:    {code}   (no admin yet – enter it when registering)");
                    return 0;

                case ["admin", "add", var name]:
                {
                    var pw = ReadNewPassword(passwordStdin);
                    var user = state.CreateUser(name, pw, null, isAdmin: true, mustChange: false);
                    state.EnsureSetupCode();
                    Console.WriteLine($"Admin account \"{user.Username}\" created.");
                    return 0;
                }

                case ["admin", "grant", var name]:
                    state.SetAdmin(RequireUser(state, name).Id, true);
                    state.EnsureSetupCode();
                    Console.WriteLine($"\"{name}\" is now an admin.");
                    return 0;

                case ["admin", "revoke", var name]:
                    state.SetAdmin(RequireUser(state, name).Id, false);
                    Console.WriteLine($"\"{name}\" is no longer an admin.");
                    return 0;

                case ["user", "list"]:
                    foreach (var u in state.Users.Read(db => db.Users.ToList()).OrderBy(u => u.Username))
                        Console.WriteLine($"{u.Username,-32} {(u.IsAdmin ? "admin" : "     ")}  created {u.CreatedAt:yyyy-MM-dd}  last sign-in {u.LastLogin?.ToString("yyyy-MM-dd HH:mm") ?? "–"}");
                    return 0;

                case ["user", "passwd", var name]:
                {
                    var user = RequireUser(state, name);
                    var pw = ReadNewPassword(passwordStdin);
                    state.SetUserPassword(user.Id, pw, mustChange: true);
                    state.Sessions.RevokeUser(user.Id);
                    Console.WriteLine($"Password of \"{user.Username}\" reset (must be changed at the next sign-in).");
                    return 0;
                }

                case ["user", "delete", var name]:
                    state.DeleteUser(RequireUser(state, name).Id);
                    Console.WriteLine($"Account \"{name}\" deleted.");
                    return 0;

                case ["folder", "list"]:
                    foreach (var f in state.Folders.Read(db => db.Folders.ToList()).OrderBy(f => f.Name))
                        Console.WriteLine($"{f.Name,-40} owner: {state.FindUser(f.OwnerId)?.Username ?? "–",-20} {(f.MustChangePassword ? "(not opened yet)" : "")}");
                    return 0;

                case ["set", "name", var value]:
                    state.Config.Update(c => c.Name = value.Trim());
                    Console.WriteLine("Server name saved.");
                    return 0;

                case ["set", "registration", var value]:
                    state.Config.Update(c => c.AllowRegistration = value is "on" or "true" or "1" or "yes");
                    Console.WriteLine($"Self-registration: {(state.AllowRegistration ? "on" : "off")}.");
                    return 0;

                case ["set", "port", var value] when int.TryParse(value, out var port) && port is > 0 and < 65536:
                    state.Config.Update(c => c.Port = port);
                    Console.WriteLine("Port saved – please restart the server.");
                    return 0;

                case ["set", "storage", var value]:
                    if (state.Folders.Read(db => db.Folders.Count) > 0)
                    {
                        Console.Error.WriteLine("There are already folders. Please stop the server and move the storage directory yourself.");
                        return 1;
                    }
                    state.Config.Update(c => c.StoragePath = Path.GetFullPath(value));
                    Console.WriteLine("Storage location saved – please restart the server.");
                    return 0;

                case ["set", "autoupdate", var value]:
                    state.Config.Update(c => c.AutoUpdate = value is "on" or "true" or "1" or "yes");
                    Console.WriteLine($"Automatic updates: {(state.Config.Read(c => c.AutoUpdate) ? "on" : "off")}.");
                    return 0;

                case ["update"]:
                {
                    var updater = new ServerUpdater(state, Microsoft.Extensions.Logging.Abstractions.NullLogger<ServerUpdater>.Instance);
                    var st = updater.CheckAsync().GetAwaiter().GetResult();
                    Console.WriteLine($"Installed: {st.CurrentVersion} · latest: {st.LatestVersion ?? "unknown"}");
                    if (st.State == "error")
                    {
                        Console.Error.WriteLine(st.Message);
                        return 1;
                    }
                    if (!st.UpdateAvailable)
                    {
                        Console.WriteLine("nfm-server is up to date.");
                        return 0;
                    }
                    if (!st.Supported)
                    {
                        Console.Error.WriteLine(st.Message);
                        return 1;
                    }
                    Console.WriteLine($"Installing version {st.LatestVersion}...");
                    st = updater.InstallAsync().GetAwaiter().GetResult();
                    if (st.State is "installed" or "restarting")
                    {
                        Console.WriteLine(OperatingSystem.IsWindows()
                            ? "Done. Restart the service: Restart-Service NFM-Server"
                            : "Done. Restart the service: sudo systemctl restart nfm-server");
                        return 0;
                    }
                    Console.Error.WriteLine(st.Message);
                    return 1;
                }

                default:
                    Console.WriteLine(Usage);
                    return args.Length == 0 || args[0] is "help" or "--help" or "-h" or "/?" ? 0 : 1;
            }
        }
        catch (VaultException ex)
        {
            Console.Error.WriteLine("Error: " + ex.Message);
            return 1;
        }
    }

    private static UserRecord RequireUser(ServerState s, string name) =>
        s.FindUser(name) ?? throw new VaultException(404, "noUser", $"Account \"{name}\" does not exist.");

    private static string ReadNewPassword(bool fromStdin)
    {
        if (fromStdin || Console.IsInputRedirected)
        {
            // Byte-Order-Mark (z.B. von PowerShell) und Zeilenende entfernen
            var line = (Console.In.ReadLine() ?? "").TrimStart('﻿').TrimEnd('\r', '\n');
            if (line.StartsWith("ï»¿", StringComparison.Ordinal)) // BOM als Latin-1 gelesen
                line = line[3..];
            if (Passwords.Validate(line) is { } error)
                throw new VaultException(400, "weakPassword", error);
            return line;
        }

        while (true)
        {
            var pw = ReadHidden("Password: ");
            if (Passwords.Validate(pw) is { } error)
            {
                Console.WriteLine(error);
                continue;
            }
            if (ReadHidden("Repeat password: ") != pw)
            {
                Console.WriteLine("The passwords do not match.");
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
