using NetworkFolderManager.Services;
using System.Windows;

namespace NetworkFolderManager.Dialogs;

/// <summary>Kleines Fenster "Eingehender Anruf" mit Annehmen/Ablehnen. Schließen = Ablehnen.</summary>
public partial class IncomingCallWindow : Window
{
    private bool _answered;

    public event Action<bool>? Answered; // true = angenommen

    public IncomingCallWindow(string name, bool screenShare)
    {
        InitializeComponent();
        NameText.Text = name;
        InitialText.Text = string.IsNullOrWhiteSpace(name) ? "?" : name.Trim()[..1].ToUpperInvariant();
        KindText.Text = screenShare ? Loc.T("🖥 möchte seinen Bildschirm teilen") : Loc.T("📷 Videoanruf");
        Closed += (_, _) => Answer(false);
    }

    private void Answer(bool accepted)
    {
        if (_answered)
            return;
        _answered = true;
        Answered?.Invoke(accepted);
    }

    private void Accept_Click(object sender, RoutedEventArgs e)
    {
        Answer(true);
        Close();
    }

    private void Decline_Click(object sender, RoutedEventArgs e)
    {
        Answer(false);
        Close();
    }
}
