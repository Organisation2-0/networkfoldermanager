# NetworkFolderManager

[Deutsch](README.de.md) · English

A desktop app for **Windows** (C# / WPF, .NET 8) and **macOS** (C# / Avalonia) that makes password-protected network folders easy to use – and adds a serverless, encrypted chat for everyone on your local network who runs the app. Windows and Mac installations chat with each other.

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

**Server folders (NFM server)** – see [below](#nfm-server-encrypted-server-folders)
- Devices running the headless **NFM server** (`nfm-server`, Windows/Linux/macOS) are discovered automatically
- **Profiles** live on the server – sign in from any device; own **password-protected, encrypted folders**
- iCloud-style folder window (Photos by month, Files, viewer), **mount as network drive** (WebDAV) on Windows and macOS
- **Admin** tab (only for admins): delete folders, create folders for others (owner must set their own password on first open), manage users – **the admin cannot see the contents**
- **macOS / iPhone / Linux:** web app served by the server (`https://<server>:45830`)

**General**
- Start with Windows (in the notification area), keep running in the tray, single instance
- **Automatic updates** from GitHub Releases (can be switched off)
- Language: English or German (Settings → Language)
- Error log at `%APPDATA%\NetworkFolderManager\error.log`

## Requirements

- Windows 10 (1809) / Windows 11, 64-bit – or macOS 12 or newer (Apple Silicon or Intel)
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

## macOS

Download `NetworkFolderManager-<version>-mac-arm64.zip` (Apple Silicon) or `-mac-x64.zip` (Intel) from the [Releases page](https://github.com/Organisation2-0/NetworkFolderManager/releases), unzip it and drag `NetworkFolderManager.app` to *Applications*.

The app is not notarized by Apple. On first launch use **right-click → Open**, or run once in Terminal:

```sh
xattr -cr /Applications/NetworkFolderManager.app
codesign --force --deep --sign - /Applications/NetworkFolderManager.app   # only needed if the app is "killed" on Apple Silicon
```

Then allow **Local Network** access when macOS asks (System Settings → Privacy & Security → Local Network) – chat and discovery need it.

| Feature | macOS |
|---|---|
| Chat (text, pictures, files, TLS, security numbers) | ✔ same protocol as Windows – Mac and PC can chat with each other |
| Network folders | Shares announced by other users appear automatically; add `smb://server/share` manually. *Connect* mounts the share through macOS (it asks for user name/password and can store them in the Keychain), *Open* shows it in Finder, uploads by drag & drop |
| Own folder notifications | ✔ pick a folder, get a notification for new files |
| Own shared folder | Set up via System Settings → General → Sharing → File Sharing (not created by the app) |
| Menu-bar icon, start at login, English/German | ✔ (closing the window keeps the chat running; quit from the menu-bar icon or ⌘Q) |
| Automatic updates | Shows when a new version is available; download from the Releases page |

Data lives in `~/Library/Application Support/NetworkFolderManager/` (settings, chat history, certificate, `error.log`); received chat files go to `~/Documents/NetworkFolderManager/`.

Build it yourself:

```powershell
# On Windows or macOS (needs .NET 8 SDK and Python 3) – unsigned, see note above
powershell -NoProfile -ExecutionPolicy Bypass -File build-mac.ps1
```

On a Mac you can instead run `dotnet run --project NetworkFolderManager.Mac`. The GitHub workflow *Build macOS app* builds, ad-hoc signs and (optionally) attaches the ZIPs to a release.

## NFM Server: encrypted server folders

A small headless service that runs on any device in the network (Linux server, Raspberry Pi, Windows PC, Mac):

| | |
|---|---|
| **Profiles** | User accounts per server – sign in from any device (Windows app or browser). Self-registration can be turned off |
| **Folders** | Every folder has its own password. Anyone who knows it can open the folder |
| **Encryption** | AES-256-GCM per file, the index (including file names) is encrypted too. Key derived from the folder password (PBKDF2, 310,000 rounds) – **neither the admin nor anyone with the disk can read the data** |
| **Admin** | Create folders (also for others), change owner, delete; create/delete users, reset passwords, grant admin. A folder created by the admin can only be opened after its owner has set their own password – a **new key** is generated at that moment |
| **Access** | HTTPS API (Windows app), web app (browser), WebDAV (network drive) – all on port **45830/TCP**; discovery on **45823/UDP** |
| **TLS** | Own certificate authority per server, restricted by *name constraints* to local names/private IPs (cannot issue certificates for other websites). The Windows app pins it by fingerprint |

**Install:** `build-server.ps1` creates ZIPs in `output\` for `linux-x64`, `linux-arm64`, `win-x64`, `osx-arm64`, `osx-x64` (self-contained).

```sh
# Linux (systemd)
unzip nfm-server-1.1.0-linux-x64.zip -d nfm && cd nfm
sudo sh install-linux.sh            # service + "nfm" account + firewall (ufw/firewalld)
sudo nfm-server admin add julian    # create the admin account
```
Windows: unzip and run `install-windows.ps1` as administrator (service *NFM-Server*). macOS / testing: `./nfm-server`.
Until an admin exists, the server prints a **setup code** (log and `nfm-server info`); whoever registers with it becomes admin.

CLI: `nfm-server info` · `admin add|grant|revoke <name>` · `user list|passwd|delete <name>` · `folder list` · `set name|registration|port …` · options `--data <dir>`, `--port <port>`, `--password-stdin`.

**Usage**
- **Windows app:** tab **Server** → the server is found automatically (or enter its IP) → *Sign in* / *Create profile* → *New folder* / double-click to open (iCloud-style window with Photos, Files, viewer, drag & drop). **Connect as network drive** sets up WebDAV (one UAC prompt per server: trust the certificate, allow files up to 4 GB).
- **Admin:** when an admin profile is signed in, the **Admin** tab appears in the main navigation.
- **macOS, iPhone, Linux:** open `https://<server-ip>:45830` in a browser (Safari: *File → Add to Dock*). Network drive in Finder: install the certificate (`/ca.crt`, *Always Trust*), then *Go → Connect to Server* (⌘K) → `https://<server-ip>:45830/dav/<folder>/`, name = folder name, password = folder password.

> A forgotten folder password cannot be reset by anyone – not even the admin. The folder can only be deleted.

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
| Known NFM servers | `settings.json` (address, fingerprint, server certificate) |
| NFM server sign-ins | Windows Credential Manager (`NetworkFolderManager:server:…`) |
| Language / auto-update flag | `HKCU\Software\noobnet.eu\NetworkFolderManager` |

## Command line

| Parameter | Purpose |
|---|---|
| `--minimized` | Start in the notification area (autostart) |
| `--setup-share "<path>" "<name>" ["<passwordfile>"]` | Create folder + access account + encrypted share + firewall rules (admin); the password file is deleted immediately |
| `--setup-firewall` | Firewall rules only (admin) |
| `--remove-share` | Remove share + firewall rule (admin) |
| `--setup-webdav "<certificate>" <fingerprint>` | Trust an NFM server certificate + configure the WebDAV client (admin, for network drives) |

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
├── build-installer.ps1                  # Windows: publish + Inno Setup + portable ZIP
├── build-server.ps1                     # NFM server for Linux/Windows/macOS
├── NetworkFolderManager.Server/         # nfm-server (ASP.NET Core, headless): API, WebDAV, web app, encrypted storage
├── build-mac.ps1                        # macOS: publish + .app bundle (ZIP)
├── installer/NetworkFolderManager.iss   # Inno Setup script
├── NetworkFolderManager.Mac/            # macOS app (Avalonia); shares chat/discovery code with the Windows app
├── .github/workflows/build-mac.yml      # signed macOS build on a macOS runner
└── NetworkFolderManager/                # Windows app (WPF)
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
