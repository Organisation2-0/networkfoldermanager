namespace NetworkFolderManager.Services;

/// <summary>
/// Der NFM-Server antwortet immer auf Englisch, liefert aber zu jedem Fehler einen festen Code.
/// Hier wird der Code in die Sprache der App übersetzt (unbekannte Codes: englische Servermeldung).
/// </summary>
public static class ServerErrors
{
    private static readonly Dictionary<string, string> German = new()
    {
        ["tooManyAttempts"] = "Zu viele Fehlversuche. Bitte in einigen Minuten erneut versuchen.",
        ["badLogin"] = "Benutzername oder Passwort ist falsch.",
        ["wrongCurrentPassword"] = "Das bisherige Passwort ist falsch.",
        ["wrongPassword"] = "Falsches Ordnerpasswort.",
        ["samePassword"] = "Das neue Passwort muss sich vom bisherigen unterscheiden.",
        ["notFound"] = "Nicht gefunden.",
        ["notOwner"] = "Das darf nur der Besitzer des Ordners.",
        ["ownerMustOpenFirst"] = "Dieser Ordner muss zuerst von seinem Besitzer geöffnet werden.",
        ["mustChangePassword"] = "Dieser Ordner wurde für dich angelegt. Bitte lege beim ersten Öffnen ein eigenes Passwort fest.",
        ["isDir"] = "Das ist ein Ordner.",
        ["noUser"] = "Diesen Benutzer gibt es nicht.",
        ["cannotDeleteSelf"] = "Du kannst dein eigenes Profil hier nicht löschen.",
        ["badServerName"] = "Bitte einen Servernamen (1–64 Zeichen) angeben.",
        ["unauthorized"] = "Bitte anmelden.",
        ["mustChangeAccountPassword"] = "Bitte zuerst ein eigenes Passwort für dein Profil festlegen.",
        ["notAdmin"] = "Nur für Admins.",
        ["csrf"] = "Anfrage abgelehnt.",
        ["badRequest"] = "Ungültige Anfrage.",
        ["io"] = "Speicherfehler auf dem Server.",
        ["folderLocked"] = "Der Ordner ist gesperrt. Bitte erneut das Ordnerpasswort eingeben.",
        ["tooLarge"] = "Anfrage zu groß.",
        ["exists"] = "Ein Element mit diesem Namen existiert bereits.",
        ["noParent"] = "Der übergeordnete Ordner existiert nicht.",
        ["badPath"] = "Ungültiger Pfad oder Name.",
        ["weakPassword"] = "Das Passwort muss mindestens 8 Zeichen lang sein.",
        ["badUsername"] = "Benutzername: 3–32 Zeichen, nur Buchstaben, Ziffern, Punkt, Minus und Unterstrich.",
        ["badName"] = "Ungültiger Ordnername: 1–64 Zeichen, ohne / \\ : * ? \" < > | % # und nicht mit Leerzeichen oder Punkt am Anfang/Ende.",
        ["userExists"] = "Dieser Benutzername ist bereits vergeben.",
        ["badSetupCode"] = "Der Setup-Code ist falsch.",
        ["registrationClosed"] = "Auf diesem Server können keine neuen Profile angelegt werden. Bitte den Admin fragen.",
        ["lastAdmin"] = "Der letzte Admin kann nicht entfernt werden.",
        ["folderExists"] = "Ein Ordner mit diesem Namen existiert auf dem Server bereits.",
        ["badLanguage"] = "Ungültige Sprache.",
    };

    /// <summary>Übersetzte Meldung zum Fehlercode; ohne bekannten Code die (englische) Servermeldung.</summary>
    public static string Message(string? code, string serverMessage)
    {
        if (code == null || !German.TryGetValue(code, out var german))
            return serverMessage;
        // Im englischen Modus die Originalmeldung des Servers (enthält ggf. genauere Angaben)
        return Loc.IsEnglish ? serverMessage : german;
    }
}
