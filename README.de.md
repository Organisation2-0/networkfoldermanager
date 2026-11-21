# NetworkFolderManager

Deutsch · [English](README.md)

Version 1.0.0 · Herausgeber: noobnet.eu · https://github.com/Organisation2-0

Eine C#-WPF-Anwendung zum Verwalten von passwortgeschützten Netzwerkordnern – mit eigenem überwachten Netzwerkordner und einem Chat für alle im lokalen Netzwerk, die das Programm installiert haben. Design im Stil von noobnet.eu (Liquid Glass, dunkel, Akzent `#00d4ff`).

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

## Kommandozeile

| Parameter | Zweck |
|---|---|
| `--minimized` | Im Infobereich starten (Autostart) |
| `--setup-share "<Pfad>" "<Name>" ["<Passwortdatei>"]` | Netzwerkordner + Zugangskonto + verschlüsselte Freigabe + Firewall einrichten (Admin); die Passwortdatei wird sofort gelöscht |
| `--setup-firewall` | Nur Firewall-Regeln (Admin) |
| `--remove-share` | Freigabe + Firewall-Regel entfernen (Admin) |

Zum Testen mehrerer Instanzen auf einem PC: Umgebungsvariable `NFM_PROFILE=<Name>` setzen (eigene Einstellungen, eigener Chat-Port).

## Ordnerstruktur

```
NetworkFolderManager/
├── README.md
├── build-installer.ps1                  # Publish + Inno Setup + portables ZIP
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
