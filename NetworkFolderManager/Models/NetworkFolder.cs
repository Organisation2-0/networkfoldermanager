using CommunityToolkit.Mvvm.ComponentModel;

namespace NetworkFolderManager.Models;

/// <summary>
/// Repräsentiert einen freigegebenen Netzwerkordner.
/// Passwörter werden bewusst nicht gespeichert – Windows verwaltet die Sitzung.
/// </summary>
public partial class NetworkFolder : ObservableObject
{
    /// <summary>UNC-Pfad: \\Computer\Freigabe</summary>
    [ObservableProperty]
    private string _path = string.Empty;

    /// <summary>Angezeigter Name: Computer\Freigabe</summary>
    [ObservableProperty]
    private string _displayName = string.Empty;

    /// <summary>Beschreibung/Kommentar der Freigabe</summary>
    [ObservableProperty]
    private string _description = string.Empty;

    /// <summary>Computername des Servers</summary>
    [ObservableProperty]
    private string _computerName = string.Empty;

    /// <summary>Freigabename</summary>
    [ObservableProperty]
    private string _shareName = string.Empty;

    /// <summary>Benutzername der aktuellen Verbindung</summary>
    [ObservableProperty]
    private string _username = string.Empty;

    /// <summary>Ob eine aktive Verbindung besteht (bzw. der Ordner erreichbar ist)</summary>
    [ObservableProperty]
    private bool _isConnected;

    /// <summary>Ob dieser Ordner der eigene (überwachte) Ordner ist</summary>
    [ObservableProperty]
    private bool _isOwnFolder;

    /// <summary>Ob für den Server ein Passwort in der Windows-Anmeldeinformationsverwaltung liegt</summary>
    [ObservableProperty]
    private bool _hasSavedCredentials;

    /// <summary>Vorgeschlagener Benutzername (z.B. vom NetworkFolderManager des anderen PCs gemeldet)</summary>
    [ObservableProperty]
    private string? _suggestedUsername;

    /// <summary>Ob der Ordner manuell hinzugefügt wurde (wird gespeichert)</summary>
    [ObservableProperty]
    private bool _isManual;

    public override string ToString() => DisplayName;
}
