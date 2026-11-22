using Microsoft.Win32;
using System.Diagnostics;
using System.DirectoryServices.AccountManagement;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetworkFolderManager.Services;

/// <summary>
/// Erstellt/entfernt den eigenen Netzwerkordner (Ordner + Zugangskonto + SMB-Freigabe + Firewall).
/// Benötigt Administratorrechte – wird vom Installer bzw. per UAC aufgerufen:
///   --setup-share "Pfad" "Name" ["Passwortdatei"]
///   --remove-share
/// Das Passwort wird über eine temporäre Datei übergeben (nicht auf der Kommandozeile sichtbar)
/// und diese sofort gelöscht.
/// </summary>
public static class ShareSetupService
{
    public const string FirewallRuleName = "NetworkFolderManager";
    private const string AccountDescription = "NetworkFolderManager – Zugang zum freigegebenen Ordner";

    static ShareSetupService()
    {
        // OEM-Codepages (z.B. 850) für die Ausgabe von net.exe/netsh.exe
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    private static readonly string MachineDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "NetworkFolderManager");

    private static readonly string MachineFile = Path.Combine(MachineDir, "machine.json");
    private static readonly string LogFile = Path.Combine(MachineDir, "setup.log");

    public static MachineConfig? LoadMachineConfig()
    {
        try
        {
            if (File.Exists(MachineFile))
                return JsonSerializer.Deserialize<MachineConfig>(File.ReadAllText(MachineFile));
        }
        catch
        {
            // ignorieren
        }
        return null;
    }

    /// <summary>
    /// Startet die App erneut mit Admin-Rechten (UAC-Abfrage), um die Freigabe anzulegen/zu ändern.
    /// </summary>
    /// <param name="password">Passwort für den Zugang; leer = ungeschützt</param>
    public static async Task<bool> RunElevatedSetupAsync(string path, string shareName, string password)
    {
        // Passwort über eine nur für den Benutzer lesbare Temp-Datei übergeben
        var pwFile = Path.Combine(Path.GetTempPath(), $"nfm-{Guid.NewGuid():N}.tmp");
        await File.WriteAllTextAsync(pwFile, password, new UTF8Encoding(false));

        var psi = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = true,
            Verb = "runas",
            Arguments = $"--setup-share \"{path.TrimEnd('\\')}\" \"{shareName}\" \"{pwFile}\""
        };

        try
        {
            using var p = Process.Start(psi);
            if (p == null)
                return false;
            await p.WaitForExitAsync();
            return p.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false; // UAC abgelehnt
        }
        finally
        {
            try { File.Delete(pwFile); } catch { /* ignorieren */ }
        }
    }

    /// <summary>
    /// Legt Ordner, Zugangskonto, NTFS-Rechte, verschlüsselte SMB-Freigabe und Firewall-Regeln an.
    /// Kann erneut aufgerufen werden, um das Passwort zu ändern oder den Schutz ein-/auszuschalten.
    /// </summary>
    public static int SetupShare(string path, string shareName, string? passwordFile)
    {
        try
        {
            var password = ReadAndDeletePasswordFile(passwordFile);
            Log($"Setup: Pfad='{path}', Freigabe='{shareName}', Passwortschutz={(password.Length > 0 ? "ja" : "NEIN")}");

            path = Path.GetFullPath(path.Trim().Trim('"'));
            shareName = SanitizeShareName(shareName);
            var old = LoadMachineConfig();

            // 1. Ordner anlegen
            Directory.CreateDirectory(path);

            // 2. Zugangskonto (bei Passwort) bzw. Entfernen eines alten Kontos
            string? accessUser = null;
            if (password.Length > 0)
            {
                accessUser = AccountNameFor(shareName);
                CreateOrUpdateAccount(accessUser, password);
            }
            if (old?.AccessUser is { } oldUser && !string.Equals(oldUser, accessUser, StringComparison.OrdinalIgnoreCase))
                DeleteAccount(oldUser);

            // 3. NTFS-Rechte
            var grantee = accessUser != null
                ? new NTAccount(Environment.MachineName, accessUser).Translate(typeof(SecurityIdentifier))
                : new SecurityIdentifier(WellKnownSidType.WorldSid, null);
            SetFolderPermissions(path, (SecurityIdentifier)grantee);

            // 4. SMB-Freigabe: nur das Zugangskonto (bzw. ungeschützt "Jeder") + Administratoren
            var granteeName = accessUser != null
                ? $@"{Environment.MachineName}\{accessUser}"
                : new SecurityIdentifier(WellKnownSidType.WorldSid, null).Translate(typeof(NTAccount)).Value;
            var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)
                .Translate(typeof(NTAccount)).Value;

            if (old != null && !string.Equals(old.ShareName, shareName, StringComparison.OrdinalIgnoreCase))
                RunTool("net", $"share \"{old.ShareName}\" /delete /y", ignoreErrors: true);
            RunTool("net", $"share \"{shareName}\" /delete /y", ignoreErrors: true);

            var (code, output) = RunTool("net",
                $"share \"{shareName}={path}\" \"/GRANT:{granteeName},CHANGE\" \"/GRANT:{admins},FULL\" \"/REMARK:NetworkFolderManager\"");
            if (code != 0)
                throw new InvalidOperationException(Loc.T("Freigabe konnte nicht erstellt werden: {0}", output));

            // 5. SMB3-Verschlüsselung für diese Freigabe einschalten
            var escaped = shareName.Replace("'", "''");
            var (encCode, _) = RunTool("powershell.exe",
                $"-NoProfile -NonInteractive -Command \"Set-SmbShare -Name '{escaped}' -EncryptData $true -Force\"",
                ignoreErrors: true);

            // 6. Firewall
            ConfigureFirewall();

            // 7. Konfiguration speichern (ohne Passwort)
            Directory.CreateDirectory(MachineDir);
            File.WriteAllText(MachineFile, JsonSerializer.Serialize(
                new MachineConfig
                {
                    SharePath = path,
                    ShareName = shareName,
                    AccessUser = accessUser,
                    Encrypted = encCode == 0
                },
                new JsonSerializerOptions { WriteIndented = true }));

            Log("Setup erfolgreich.");
            return 0;
        }
        catch (Exception ex)
        {
            Log($"FEHLER: {ex}");
            return 1;
        }
    }

    /// <summary>
    /// Nur die Firewall-Regeln anlegen (Installer ohne eigenen Netzwerkordner).
    /// </summary>
    public static int SetupFirewallOnly()
    {
        try
        {
            ConfigureFirewall();
            return 0;
        }
        catch (Exception ex)
        {
            Log($"FEHLER Firewall: {ex}");
            return 1;
        }
    }

    /// <summary>
    /// Entfernt Freigabe, Zugangskonto und Firewall-Regel (Deinstallation). Die Dateien bleiben erhalten.
    /// </summary>
    public static int RemoveShare()
    {
        try
        {
            var config = LoadMachineConfig();
            if (config != null && !string.IsNullOrWhiteSpace(config.ShareName))
                RunTool("net", $"share \"{config.ShareName}\" /delete /y", ignoreErrors: true);
            if (config?.AccessUser is { } user)
                DeleteAccount(user);

            RunTool("netsh", $"advfirewall firewall delete rule name=\"{FirewallRuleName}\"", ignoreErrors: true);

            if (File.Exists(MachineFile))
                File.Delete(MachineFile);

            Log("Freigabe entfernt.");
            return 0;
        }
        catch (Exception ex)
        {
            Log($"FEHLER beim Entfernen: {ex}");
            return 1;
        }
    }

    #region Zugangskonto

    /// <summary>Kontoname "NFM-&lt;Freigabe&gt;" (max. 20 Zeichen, nur unkritische Zeichen).</summary>
    private static string AccountNameFor(string shareName)
    {
        var clean = new string(shareName.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray());
        if (clean.Length == 0)
            clean = "Ordner";
        var name = "NFM-" + clean;
        return name.Length > 20 ? name[..20] : name;
    }

    private static void CreateOrUpdateAccount(string name, string password)
    {
        using var ctx = new PrincipalContext(ContextType.Machine);
        var user = UserPrincipal.FindByIdentity(ctx, IdentityType.SamAccountName, name);
        var isNew = user == null;
        user ??= new UserPrincipal(ctx) { SamAccountName = name };

        using (user)
        {
            user.DisplayName = "NetworkFolderManager";
            user.Description = AccountDescription;
            user.PasswordNeverExpires = true;
            user.UserCannotChangePassword = true;
            user.Enabled = true;
            if (isNew)
            {
                user.SetPassword(password);
                user.Save();
            }
            else
            {
                user.Save();
                user.SetPassword(password);
            }
        }

        // Konto nicht auf dem Windows-Anmeldebildschirm anzeigen
        using var key = Registry.LocalMachine.CreateSubKey(
            @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon\SpecialAccounts\UserList");
        key.SetValue(name, 0, RegistryValueKind.DWord);

        Log($"Zugangskonto '{name}' {(isNew ? "angelegt" : "aktualisiert")}.");
    }

    private static void DeleteAccount(string name)
    {
        try
        {
            using var ctx = new PrincipalContext(ContextType.Machine);
            using var user = UserPrincipal.FindByIdentity(ctx, IdentityType.SamAccountName, name);
            // Nur von uns angelegte Konten löschen
            if (user != null && user.Description == AccountDescription)
            {
                user.Delete();
                Log($"Zugangskonto '{name}' gelöscht.");
            }

            using var key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon\SpecialAccounts\UserList", writable: true);
            key?.DeleteValue(name, throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            Log($"Konto '{name}' konnte nicht gelöscht werden: {ex.Message}");
        }
    }

    private static void SetFolderPermissions(string path, SecurityIdentifier grantee)
    {
        var dirInfo = new DirectoryInfo(path);
        var security = dirInfo.GetAccessControl();

        // Frühere explizite Freigaben dieser App entfernen
        foreach (var sid in new[]
                 {
                     new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                     new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null)
                 })
        {
            security.PurgeAccessRules(sid);
        }

        security.AddAccessRule(new FileSystemAccessRule(
            grantee,
            FileSystemRights.Modify,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        dirInfo.SetAccessControl(security);
    }

    private static string ReadAndDeletePasswordFile(string? file)
    {
        if (string.IsNullOrWhiteSpace(file) || !File.Exists(file))
            return string.Empty;
        try
        {
            return File.ReadAllText(file).TrimEnd('\r', '\n');
        }
        finally
        {
            try { File.Delete(file); } catch { /* ignorieren */ }
        }
    }

    #endregion

    private static void ConfigureFirewall()
    {
        foreach (var profile in new[] { "private", "domain" })
        {
            // Gruppen über Ressourcen-IDs → funktioniert unabhängig von der Windows-Sprache
            RunTool("netsh", $"advfirewall firewall set rule group=\"@FirewallAPI.dll,-28502\" profile={profile} new enable=Yes", ignoreErrors: true);
            RunTool("netsh", $"advfirewall firewall set rule group=\"@FirewallAPI.dll,-32752\" profile={profile} new enable=Yes", ignoreErrors: true);
        }

        var exe = Environment.ProcessPath!;
        RunTool("netsh", $"advfirewall firewall delete rule name=\"{FirewallRuleName}\"", ignoreErrors: true);
        RunTool("netsh",
            $"advfirewall firewall add rule name=\"{FirewallRuleName}\" dir=in action=allow program=\"{exe}\" enable=yes profile=private,domain",
            ignoreErrors: true);
    }

    private static string SanitizeShareName(string name)
    {
        var invalid = "\\/[]:|<>+=;,?*\"'";
        var clean = new string(name.Trim().Where(c => !invalid.Contains(c) && !char.IsControl(c)).ToArray());
        if (string.IsNullOrWhiteSpace(clean))
            clean = "Netzwerkordner";
        return clean.Length > 80 ? clean[..80] : clean;
    }

    private static (int ExitCode, string Output) RunTool(string file, string args, bool ignoreErrors = false)
    {
        var oem = Encoding.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
        var psi = new ProcessStartInfo(file, args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = oem,
            StandardErrorEncoding = oem
        };

        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();

        var output = (stdout + stderr).Trim();
        Log($"> {file} {args}  → {p.ExitCode}{(ignoreErrors ? " (ignoriert)" : "")} {output}");
        return (p.ExitCode, output);
    }

    private static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(MachineDir);
            File.AppendAllText(LogFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
        }
        catch
        {
            // ignorieren
        }
    }
}
