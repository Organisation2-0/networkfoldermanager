# NetworkFolderManager

Deutsch · [English](README.md)

Version 1.1.2 · Herausgeber: noobnet.eu · https://github.com/Organisation2-0

Eine C#-Anwendung für **Windows** (WPF) und **macOS** (Avalonia) zum Verwalten von passwortgeschützten Netzwerkordnern – mit eigenem überwachten Netzwerkordner und einem Chat für alle im lokalen Netzwerk, die das Programm installiert haben. Design im Stil von noobnet.eu (Liquid Glass, dunkel, Akzent `#00d4ff`).

## Funktionen

**Ordner**
- **Passwortgeschützte Netzwerkordner öffnen** – Freigaben mit Benutzername/Passwort verbinden (optional dauerhaft), trennen und im Explorer öffnen
- **Netzwerkordner erkennen** – Rechner im Netzwerk scannen und deren Datei-Freigaben auflisten; Ordner anderer NetworkFolderManager-Nutzer erscheinen automatisch
- **Manuelle Ordner** – `\\Server\Freigabe` direkt eingeben (wird gespeichert)
- **Passwörter merken** – einmal eingeben, verschlüsselt in der Windows-Anmeldeinformationsverwaltung gespeichert; Ordner verbinden sich danach automatisch („Passwort vergessen“ löscht es wieder)
- **Dateien hochladen** – per Dialog oder Drag & Drop, parallel, mit Fortschritt und Abbrechen; nichts wird überschrieben (`Datei (1).txt`)

**Eigener Netzwerkordner**
- Wird (wenn gewählt) vom Installer angelegt, im Netzwerk freigegeben und automatisch überwacht – oder später unter *Einstellungen* (UAC-Abfrage)
- **Passwortschutz:** eigenes Windows-Konto `NFM-<Freigabename>` mit selbst gewähltem Passwort, nur dieses Konto hat Zugriff. Ohne Passwort warnen Installer und Programm deutlich (gefährlich!)
- **SMB3-Verschlüsselung** der Freigabe ist aktiv
- Windows-Benachrichtigung, sobald eine neue Datei vollständig angekommen ist; Klick zeigt sie im Explorer

**Chat (wie WhatsApp, nur im lokalen Netz)**
- Andere Installationen werden automatisch gefunden (UDP-Broadcast, Port 45821)
- Nachrichten, Bilder und Dateien direkt von PC zu PC (Port 45822+), ohne Server/Internet
- **TLS-verschlüsselt** mit eigenem Zertifikat pro Installation; Kontakte werden beim ersten Kontakt an ihrem Schlüssel erkannt (Sicherheitsnummer). Ändert sich der Schlüssel, warnt das Programm und sendet bis zur Bestätigung nichts
- Zustellhäkchen ✓✓, Warteschlange für Offline-Kontakte (wird zugestellt, sobald sie online sind), „Erneut senden“
- Bilder per 📎, Drag & Drop oder **Strg+V** aus der Zwischenablage; Vorschau im Chat
- Ungelesen-Zähler, Benachrichtigungen, Verlauf wird lokal gespeichert

**Server-Ordner (NFM-Server)** – siehe [eigener Abschnitt](#nfm-server-verschlüsselte-server-ordner)
- Geräte mit dem **NFM-Server** (`nfm-server`, ohne Oberfläche, Windows/Linux/macOS) werden im Netzwerk automatisch gefunden
- **Profile** liegen auf dem Server – Anmeldung von jedem Gerät (Windows-App oder Browser)
- Eigene, **passwortgeschützte Ordner** anlegen und öffnen; Inhalte werden mit dem Ordnerpasswort **verschlüsselt** gespeichert
- Ordnerfenster im iCloud-Stil: **Fotos** (nach Monaten), **Dateien** (Symbole/Liste), Bildbetrachter, Drag & Drop
- **Als Netzlaufwerk einbinden** (WebDAV) – unter Windows per Klick, unter macOS im Finder
- **Admin-Bereich** (Tab *Admin*, nur für Admins sichtbar): Ordner löschen, Ordner für andere anlegen (Besitzer muss beim ersten Öffnen ein eigenes Passwort setzen), Benutzer verwalten – **Inhalte kann der Admin nicht sehen**
- **macOS / iPhone / Linux:** Web-App direkt vom Server (`https://<Server>:45830`), im Safari „Zum Dock hinzufügen“

**Allgemein**
- Autostart mit Windows (im Infobereich), Weiterlaufen im Tray, nur eine Instanz
- **Automatische Updates** über GitHub-Releases (im Installer und unter *Einstellungen → Updates* schaltbar)
- Sprache: Deutsch oder English (Einstellungen → Sprache)
- Fehlerprotokoll unter `%APPDATA%\NetworkFolderManager\error.log`

## Updates veröffentlichen

Das Programm sucht unter **https://github.com/Organisation2-0/NetworkFolderManager/releases** nach neuen Versionen (öffentliches Repository nötig).

1. Version in `NetworkFolderManager/NetworkFolderManager.csproj` erhöhen (`<Version>`).
2. `build-installer.ps1` ausführen.
3. Auf GitHub ein Release mit Tag `v<Version>` (z.B. `v1.0.2`) anlegen und diese beiden Dateien aus `output\` anhängen:
   - `NetworkFolderManager-Setup-<Version>.exe`
   - `NetworkFolderManager-Setup-<Version>.sha256`

Installationen mit eingeschalteten Auto-Updates prüfen kurz nach dem Start und danach alle 6 Stunden, laden das Setup herunter, prüfen Prüfsumme, Herausgeber und Version und installieren es im Update-Modus (Windows fragt nach Administratorrechten). Netzwerkordner, Passwort und Einstellungen bleiben dabei erhalten; das Programm startet danach neu.

## Installation

`output\NetworkFolderManager-Setup-<Version>.exe` ausführen. Zuerst wird die Sprache gewählt (**Deutsch / English**), danach fragt der Installer nach:

| Option | Wirkung |
|---|---|
| Eigenen Netzwerkordner erstellen … | Ordner (Standard `C:\Netzwerkordner`) + passwortgeschützte, verschlüsselte Freigabe anlegen, automatisch überwachen |
| Mit Windows starten | Autostart im Infobereich (`HKCU\…\Run`) |
| Automatisch aktualisieren | Neue Versionen von GitHub automatisch installieren (Standard: an) |
| Ordner für Chat-Dateien | Standard: neuer Ordner `Dokumente\NetworkFolderManager` des Benutzers, frei wählbar |
| Desktop-Verknüpfung | – |

Immer: Firewall-Regel für den Chat sowie Datei-/Druckerfreigabe und Netzwerkerkennung (nur Profile *Privat*/*Domäne*).
Bei der Deinstallation werden Freigabe, Zugangskonto und Firewall-Regel entfernt – die Dateien im Ordner bleiben erhalten.

> **Hinweis:** Chat und Erkennung funktionieren nur in Netzwerken, die in Windows als **Privat** eingestuft sind.

## macOS

`NetworkFolderManager-<Version>-mac-arm64.zip` (Apple Silicon) bzw. `-mac-x64.zip` (Intel) von der [Releases-Seite](https://github.com/Organisation2-0/NetworkFolderManager/releases) laden, entpacken und `NetworkFolderManager.app` in den Ordner *Programme* ziehen.

Die App ist nicht von Apple notarisiert: beim ersten Start **Rechtsklick → Öffnen**, oder einmalig im Terminal:

```sh
xattr -cr /Applications/NetworkFolderManager.app
codesign --force --deep --sign - /Applications/NetworkFolderManager.app   # nur nötig, wenn die App auf Apple Silicon "beendet" wird
```

Danach den Zugriff auf das **lokale Netzwerk** erlauben, wenn macOS fragt (Systemeinstellungen → Datenschutz & Sicherheit → Lokales Netzwerk) – Chat und Erkennung brauchen das.

| Funktion | macOS |
|---|---|
| Chat (Text, Bilder, Dateien, TLS, Sicherheitsnummern) | ✔ gleiches Protokoll wie unter Windows – Mac und PC können miteinander chatten |
| Netzwerkordner | Freigaben anderer Nutzer erscheinen automatisch; `smb://Server/Freigabe` manuell hinzufügen. *Verbinden* bindet die Freigabe über macOS ein (macOS fragt Benutzername/Passwort ab und kann sie im Schlüsselbund merken), *Im Finder öffnen*, Upload per Drag & Drop |
| Benachrichtigung bei neuen Dateien im eigenen Ordner | ✔ |
| Eigene Freigabe | Über Systemeinstellungen → Allgemein → Teilen → Dateisharing (legt die App nicht an) |
| Menüleistensymbol, Autostart, Deutsch/English | ✔ (Fenster schließen lässt den Chat weiterlaufen; Beenden über das Menüleistensymbol oder ⌘Q) |
| Updates | Zeigt neue Versionen an; Download auf der Releases-Seite |

Daten liegen in `~/Library/Application Support/NetworkFolderManager/` (Einstellungen, Chatverläufe, Zertifikat, `error.log`); empfangene Chat-Dateien in `~/Documents/NetworkFolderManager/`.

Selbst bauen: `powershell -NoProfile -ExecutionPolicy Bypass -File build-mac.ps1` (unter Windows oder macOS; .NET 8 SDK und Python 3) – unsigniert, siehe Hinweis oben. Auf einem Mac geht auch `dotnet run --project NetworkFolderManager.Mac`. Der GitHub-Workflow *Build macOS app* baut, signiert ad-hoc und hängt die ZIPs auf Wunsch an ein Release.

## NFM-Server: verschlüsselte Server-Ordner

Ein kleiner Dienst ohne Oberfläche, der auf einem beliebigen Gerät im Netzwerk läuft (Linux-Server, Raspberry Pi, NAS mit Linux, Windows-PC, Mac). Er stellt bereit:

| | |
|---|---|
| **Profile** | Benutzerkonten pro Server (Anmeldung von jedem Gerät). Selbst-Registrierung abschaltbar |
| **Ordner** | Jeder Ordner hat ein eigenes Passwort. Wer es kennt, kann ihn öffnen |
| **Verschlüsselung** | AES-256-GCM pro Datei, Inhaltsverzeichnis (auch Dateinamen) verschlüsselt. Schlüssel = aus dem Ordnerpasswort abgeleitet (PBKDF2, 310 000 Runden) – **weder Admin noch wer die Festplatte hat, kann die Daten lesen** |
| **Admin** | Ordner anlegen (für andere), Besitzer ändern, löschen; Benutzer anlegen/löschen, Passwort zurücksetzen, Admin-Rechte. Ein vom Admin angelegter Ordner kann erst geöffnet werden, wenn der Besitzer ein eigenes Passwort gesetzt hat – dabei wird ein **neuer Schlüssel** erzeugt |
| **Zugriff** | HTTPS-API (Windows-App), Web-App (Browser), WebDAV (Netzlaufwerk) – alles über Port **45830/TCP**; Erkennung über **45823/UDP** |
| **TLS** | Eigene Zertifizierungsstelle pro Server, per *Name Constraints* auf lokale Namen/private IPs beschränkt (kann keine fremden Websites ausstellen). Die Windows-App prüft sie fest per Fingerabdruck |

### Installation

`build-server.ps1` erzeugt in `output\` je eine ZIP für `linux-x64`, `linux-arm64` (Raspberry Pi 64 Bit), `win-x64`, `osx-arm64`, `osx-x64` (eigenständig, keine .NET-Installation nötig).

**Linux** (systemd):
```sh
unzip nfm-server-1.1.2-linux-x64.zip -d nfm && cd nfm
sudo sh install-linux.sh            # Dienst + Konto "nfm" + Firewall (ufw/firewalld)
sudo nfm-server admin add julian    # Admin-Konto anlegen (fragt nach dem Passwort)
```
Daten: `/var/lib/nfm-server` · Protokoll: `journalctl -u nfm-server`

**Windows:** ZIP entpacken, als Administrator `install-windows.ps1` ausführen (Dienst *NFM-Server*, Firewall nur Privat/Domäne). Daten: `%ProgramData%\NFM-Server`.

**macOS / zum Testen:** `./nfm-server` im Terminal starten (Daten: `~/Library/Application Support/NFM-Server`).

**Erster Admin ohne Kommandozeile:** Solange es keinen Admin gibt, zeigt der Server einen **Setup-Code** im Protokoll (und bei `nfm-server info`). Wer sich mit diesem Code registriert (Web-App oder Windows-App), wird Admin.

### Verwaltung per Kommandozeile

| Befehl | Zweck |
|---|---|
| `nfm-server info` | Adresse, Fingerabdruck, Datenordner, Setup-Code |
| `nfm-server admin add <Name>` / `admin grant <Name>` / `admin revoke <Name>` | Admin anlegen / Rechte geben / entziehen |
| `nfm-server user list` / `user passwd <Name>` / `user delete <Name>` | Konten anzeigen / Passwort zurücksetzen / löschen |
| `nfm-server folder list` | Ordner anzeigen (nur Namen und Besitzer) |
| `nfm-server set name <Name>` / `set registration on\|off` / `set port <Port>` | Einstellungen |
| `nfm-server set autoupdate on\|off` / `nfm-server update` | Automatische Updates ein/aus (Standard: an) / jetzt aktualisieren |

Optionen: `--data <Ordner>` (oder `NFM_SERVER_DATA`), `--port <Port>`, `--password-stdin`.

### Nutzung

- **Windows-App:** Tab **Server** → Server wird automatisch gefunden (sonst IP eintragen) → *Anmelden* oder *Profil anlegen* → *Neuer Ordner* / Doppelklick zum Öffnen. Im Ordnerfenster: **Als Netzlaufwerk verbinden** (einmalig UAC: Zertifikat vertrauen + WebDAV-Dienst für Dateien bis 4 GB).
- **Admin:** Ist ein Admin-Profil angemeldet, erscheint oben der Tab **Admin**.
- **macOS, iPhone, Linux:** im Browser `https://<Server-IP>:45830` öffnen. Für eine Anwendung im Dock: Safari → *Ablage → Zum Dock hinzufügen*.
- **Netzlaufwerk auf dem Mac:** Zertifikat (Link in der Web-App, `/ca.crt`) per Doppelklick in den Schlüsselbund, dort *Immer vertrauen*. Dann Finder → *Gehe zu → Mit Server verbinden* (⌘K) → `https://<Server-IP>:45830/dav/<Ordnername>/` · Name: Ordnername · Passwort: Ordnerpasswort.

**Automatische Updates** (Standard: an): Der Server prüft kurz nach dem Start und dann alle 6 Stunden die GitHub-Releases, lädt das passende Paket, prüft die SHA-256-Prüfsumme, tauscht das Programm aus und startet als Dienst neu – Konten, Ordner und Daten bleiben erhalten. Abschaltbar per `nfm-server set autoupdate off` oder im Admin-Bereich (dort auch *Jetzt prüfen* / *Jetzt aktualisieren*). Server mit Version 1.1.1 oder älter müssen einmal manuell aktualisiert werden (Installationsskript erneut ausführen).

**Sprache:** Der Server selbst (Kommandozeile, Protokoll, Installationsskripte) ist komplett auf Englisch. Web-App und Windows-App lassen sich auf Deutsch oder Englisch umstellen; die Wahl wird im Profil gespeichert und gilt nach der Anmeldung auf allen Geräten (Windows-App: nach einem Neustart).

> **Wichtig:** Ein vergessenes Ordnerpasswort kann niemand zurücksetzen – auch der Admin nicht. Der Ordner kann dann nur gelöscht werden.

## Speicherorte

| Was | Wo |
|---|---|
| Einstellungen | `%APPDATA%\NetworkFolderManager\settings.json` (keine Passwörter) |
| Chatverläufe | `%APPDATA%\NetworkFolderManager\chats\` |
| Empfangene Chat-Dateien | `Dokumente\NetworkFolderManager\` (im Installer und unter *Einstellungen → Chat-Dateien* wählbar) |
| Netzwerkordner-Konfiguration | `%ProgramData%\NetworkFolderManager\machine.json` |
| Setup-Protokoll | `%ProgramData%\NetworkFolderManager\setup.log` |
| Gespeicherte Ordner-Passwörter | Windows-Anmeldeinformationsverwaltung |
| Chat-Zertifikat | Zertifikatspeicher des Benutzers (`certmgr.msc` → Eigene Zertifikate) |
| Bekannte NFM-Server | `settings.json` (Adresse, Fingerabdruck, Server-Zertifikat) |
| Anmeldungen an NFM-Servern | Windows-Anmeldeinformationsverwaltung (`NetworkFolderManager:server:…`) |

## Kommandozeile

| Parameter | Zweck |
|---|---|
| `--minimized` | Im Infobereich starten (Autostart) |
| `--setup-share "<Pfad>" "<Name>" ["<Passwortdatei>"]` | Netzwerkordner + Zugangskonto + verschlüsselte Freigabe + Firewall einrichten (Admin); die Passwortdatei wird sofort gelöscht |
| `--setup-firewall` | Nur Firewall-Regeln (Admin) |
| `--remove-share` | Freigabe + Firewall-Regel entfernen (Admin) |
| `--setup-webdav "<Zertifikat>" <Fingerabdruck>` | NFM-Server-Zertifikat vertrauen + WebDAV-Client einrichten (Admin, für Netzlaufwerke) |

Zum Testen mehrerer Instanzen auf einem PC: Umgebungsvariable `NFM_PROFILE=<Name>` setzen (eigene Einstellungen, eigener Chat-Port).

## Ordnerstruktur

```
NetworkFolderManager/
├── README.md
├── build-installer.ps1                  # Publish + Inno Setup + portables ZIP
├── build-server.ps1                     # NFM-Server für Linux/Windows/macOS
├── NetworkFolderManager.Server/         # nfm-server (ASP.NET Core, ohne Oberfläche)
│   ├── Api.cs · WebDav.cs · WebUi.cs    # HTTPS-API, WebDAV, Web-App
│   ├── VaultFolder.cs · BlobStorage.cs  # verschlüsselte Ablage
│   ├── ServerState.cs · Cli.cs          # Konten/Ordner/Admin, Kommandozeile
│   ├── wwwroot/                         # Web-App (macOS, iPhone, Linux)
│   └── deploy/                          # systemd-Dienst, Installationsskripte
├── installer/NetworkFolderManager.iss   # Inno-Setup-Skript
└── NetworkFolderManager/
    ├── App.xaml / .cs                   # Start, Einzelinstanz, Kommandozeile
    ├── MainWindow.xaml / .cs            # Tabs: Ordner · Chat · Einstellungen
    ├── Themes/NoobnetTheme.xaml         # noobnet.eu-Design
    ├── Converters/
    ├── Models/                          # NetworkFolder, ChatMessage, Peer
    ├── Services/
    │   ├── NetworkDiscoveryService.cs   # Scan, Verbinden/Trennen
    │   ├── FileTransferService.cs       # Upload
    │   ├── FolderMonitorService.cs      # Überwachung
    │   ├── ShareSetupService.cs         # Freigabe/Firewall (Admin)
    │   ├── PeerDiscoveryService.cs      # Nutzer im Netzwerk finden
    │   ├── ChatService.cs               # Nachrichten/Dateien übertragen
    │   ├── ChatStore.cs                 # Chatverlauf speichern
    │   ├── AutostartService.cs
    │   ├── NotificationService.cs       # Tray + Benachrichtigungen
    │   └── SettingsService.cs
    ├── ViewModels/                      # MainWindowViewModel, ChatViewModel
    └── Dialogs/PasswordDialog.xaml / .cs
```

## Bauen

```powershell
# Entwickeln
cd NetworkFolderManager\NetworkFolderManager
dotnet run

# Installer (benötigt Inno Setup 6: winget install JRSoftware.InnoSetup)
cd NetworkFolderManager
powershell -NoProfile -ExecutionPolicy Bypass -File build-installer.ps1
```

Ergebnis in `output\`: `NetworkFolderManager-Setup-<Version>.exe` und `NetworkFolderManager-<Version>-portable.zip` (eigenständig, keine .NET-Installation nötig).

## Lizenz

[MIT](LICENSE) © 2026 noobnet.eu
