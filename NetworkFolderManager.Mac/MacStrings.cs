namespace NetworkFolderManager;

/// <summary>Englische Übersetzungen für Texte, die nur in der macOS-App vorkommen (deutscher Text → Englisch).</summary>
internal static class MacStrings
{
    public static readonly (string De, string En)[] Pairs =
    {
        ("Ja", "Yes"),
        ("Nein", "No"),
        ("Im Finder öffnen", "Open in Finder"),
        ("smb://Server/Freigabe hinzufügen", "Add smb://Server/Share"),
        ("Dateien auswählen", "Choose files"),
        ("Ungültiger Pfad. Format: smb://Server/Freigabe", "Invalid path. Format: smb://Server/Share"),
        ("Mit macOS starten (in der Menüleiste)", "Start with macOS (in the menu bar)"),
        ("• Beim ersten Start fragt macOS, ob NetworkFolderManager Geräte im lokalen Netzwerk finden darf – bitte erlauben (Systemeinstellungen → Datenschutz & Sicherheit → Lokales Netzwerk). Ohne diese Erlaubnis funktionieren Chat und Erkennung nicht.",
         "• On first launch macOS asks whether NetworkFolderManager may find devices on the local network – please allow it (System Settings → Privacy & Security → Local Network). Without this permission chat and discovery do not work."),
        ("• Netzwerkordner werden über macOS eingebunden; Benutzername und Passwort fragt macOS selbst ab und kann sie im Schlüsselbund merken.",
         "• Network folders are mounted by macOS; macOS asks for the user name and password itself and can remember them in the Keychain."),
        ("• Einen eigenen Ordner für andere freigeben: Systemeinstellungen → Allgemein → Teilen → Dateisharing.",
         "• To share your own folder with others: System Settings → General → Sharing → File Sharing."),
        ("• Nachrichten, Bilder und Dateien im Chat gehen direkt von Mac zu PC bzw. Mac zu Mac im lokalen Netz – ohne Server, TLS-verschlüsselt. Jeder Kontakt wird beim ersten Kontakt an seinem Schlüssel erkannt; ändert sich dieser, warnt das Programm.",
         "• Messages, pictures and files in the chat go directly from Mac to PC or Mac to Mac on the local network – no server, TLS-encrypted. Each contact is recognised by its key on first contact; if it changes, the program warns you."),
        ("• Fehlerprotokoll (bei Problemen bitte mitschicken): ~/Library/Application Support/NetworkFolderManager/error.log",
         "• Error log (please attach it when reporting problems): ~/Library/Application Support/NetworkFolderManager/error.log"),
        ("Dateien zum Hochladen auswählen", "Choose files to upload"),
    };
}
