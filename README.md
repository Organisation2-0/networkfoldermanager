# NetworkFolderManager

[Deutsch](README.de.md) · English

A Windows desktop app (C# / WPF, .NET 8) that makes password-protected network folders easy to use – and adds a serverless, encrypted chat for everyone on your local network who runs the app.

Dark "liquid glass" UI, available in **English and German**. Published by [noobnet.eu](https://github.com/Organisation2-0).

## Features

**Network folders**
- Connect to password-protected SMB shares (optionally persistent), disconnect, and open them in Explorer
- Discover computers and their file shares on the local network; folders of other NetworkFolderManager users show up automatically
- Add shares manually via `\\Server\Share`
- Remember passwords securely in the Windows Credential Manager
- Upload files via dialog or drag & drop – in parallel, with progress and cancel; existing files are never overwritten (`file (1).txt`)

**Your own shared folder**
- Created and shared by the installer (optional) or later from *Settings* (UAC prompt)
- Protected by a dedicated Windows account `NFM-<ShareName>` with a password you choose; only that account has access
- SMB3 encryption enabled for the share
- A Windows notification appears when a new file has fully arrived; clicking it shows the file in Explorer

**Local-network chat (think WhatsApp, but LAN only)**
- Other installations are found automatically (UDP broadcast, port 45821)
- Messages, pictures and files go directly from PC to PC (TCP port 45822+) – no server, no internet
- TLS-encrypted with a per-installation certificate. Contacts are recognised by their key on first contact (security number); if a key changes, the app warns you and sends nothing until you confirm
- Delivery ticks, a queue for offline contacts, "resend"
- Send pictures via 📎, drag & drop or **Ctrl+V**; previews in the chat
- Unread counters, notifications, locally stored history

**General**
- Start with Windows (in the notification area), keep running in the tray, single instance
- **Automatic updates** from GitHub Releases (can be switched off)
- Language: English or German (Settings → Language)
- Error log at `%APPDATA%\NetworkFolderManager\error.log`

## Requirements

- Windows 10 (1809) / Windows 11, 64-bit
- Chat and discovery only work on networks Windows classifies as **Private** (or Domain)
- No .NET installation needed – the release is self-contained

## Installation

Download `NetworkFolderManager-Setup-<version>.exe` from the [Releases page](https://github.com/Organisation2-0/NetworkFolderManager/releases) and run it. The installer asks for the language (English / German) and then offers:

| Option | Effect |
|---|---|
| Create my own network folder … | Creates the folder (default `C:\Netzwerkordner`) and a password-protected, encrypted share, monitored automatically |
| Start with Windows | Autostart in the notification area (`HKCU\…\Run`) |
| Update automatically | Install new versions from GitHub automatically (default: on) |
| Folder for chat files | Default: `Documents\NetworkFolderManager`, can be changed |
| Desktop shortcut | – |

Always: a firewall rule for the chat plus file/printer sharing and network discovery (Private/Domain profiles only).
Uninstalling removes the share, the access account and the firewall rule – files in the folder are kept.

## Where data is stored

| What | Where |
|---|---|
| Settings | `%APPDATA%\NetworkFolderManager\settings.json` (no passwords) |
| Chat history | `%APPDATA%\NetworkFolderManager\chats\` |
| Received chat files | `Documents\NetworkFolderManager\` (changeable) |
| Shared-folder configuration | `%ProgramData%\NetworkFolderManager\machine.json` |
| Setup log | `%ProgramData%\NetworkFolderManager\setup.log` |
| Saved folder passwords | Windows Credential Manager |
| Chat certificate | Current user's certificate store |
| Language / auto-update flag | `HKCU\Software\noobnet.eu\NetworkFolderManager` |

## Command line

| Parameter | Purpose |
|---|---|
| `--minimized` | Start in the notification area (autostart) |
| `--setup-share "<path>" "<name>" ["<passwordfile>"]` | Create folder + access account + encrypted share + firewall rules (admin); the password file is deleted immediately |
| `--setup-firewall` | Firewall rules only (admin) |
| `--remove-share` | Remove share + firewall rule (admin) |

To run several instances on one PC for testing, set the environment variable `NFM_PROFILE=<name>` (separate settings and chat port).

## Building from source

```powershell
# Run in development
cd NetworkFolderManager\NetworkFolderManager
dotnet run

# Installer + portable ZIP (needs Inno Setup 6: winget install JRSoftware.InnoSetup)
powershell -NoProfile -ExecutionPolicy Bypass -File build-installer.ps1
```

Output in `output\`: `NetworkFolderManager-Setup-<version>.exe` and `NetworkFolderManager-<version>-portable.zip`.

## Publishing a release

The app looks for updates in this repository's [Releases](https://github.com/Organisation2-0/NetworkFolderManager/releases).

1. Bump `<Version>` in `NetworkFolderManager/NetworkFolderManager.csproj`.
2. Run `build-installer.ps1`.
3. Create a GitHub release tagged `v<version>` (e.g. `v1.0.1`) and attach:
   - `NetworkFolderManager-Setup-<version>.exe`
   - `NetworkFolderManager-Setup-<version>.sha256`

Installations with auto-update enabled check shortly after start and every 6 hours, download the setup, verify checksum, publisher and version, and install it in update mode (Windows asks for administrator rights). Shares, passwords and settings are preserved and the app restarts afterwards.

## Project layout

```
NetworkFolderManager/
├── build-installer.ps1                  # publish + Inno Setup + portable ZIP
├── installer/NetworkFolderManager.iss   # Inno Setup script
└── NetworkFolderManager/
    ├── App.xaml(.cs)                    # startup, single instance, command line
    ├── MainWindow.xaml(.cs)             # tabs: Folders · Chat · Settings
    ├── Themes/NoobnetTheme.xaml         # UI theme
    ├── Models/                          # NetworkFolder, ChatMessage, Peer
    ├── Services/
    │   ├── NetworkDiscoveryService.cs   # scan, connect/disconnect
    │   ├── FileTransferService.cs       # uploads
    │   ├── FolderMonitorService.cs      # watches the own folder
    │   ├── ShareSetupService.cs         # share/firewall setup (admin)
    │   ├── PeerDiscoveryService.cs      # find other users (UDP)
    │   ├── ChatService.cs               # TLS message/file transfer
    │   ├── ChatStore.cs                 # chat history
    │   ├── UpdateService.cs             # GitHub-based updates
    │   ├── Loc.cs / Loc.Strings.cs      # English/German texts
    │   └── …
    ├── ViewModels/                      # MainWindowViewModel, ChatViewModel
    └── Dialogs/
```

The UI texts live in `Services/Loc.Strings.cs` (German text → English translation); corrections and new languages are welcome as pull requests.

## Security notes

- A share **without** a password is open to everyone on the network – the app and installer warn clearly. Always set a password.
- Chat traffic is TLS-encrypted, but trust is based on first contact (trust on first use). Compare the security number with the other person for sensitive use.
- Only use it on networks you trust (Windows "Private" profile).

## License

[MIT](LICENSE) © 2026 noobnet.eu
