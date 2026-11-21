using NetworkFolderManager.Services;
using NetworkFolderManager.Models;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;
using Window = System.Windows.Window;

namespace NetworkFolderManager.Dialogs;

/// <summary>
/// Abfrage von Benutzername und Passwort für einen Netzwerkordner.
/// </summary>
public partial class PasswordDialog : Window
{
    public string Username => UsernameTextBox.Text.Trim();
    public string Password => PasswordBox.Password;
    public bool Remember => RememberCheckBox.IsChecked == true;
    public bool SavePassword => SavePasswordCheckBox.IsChecked == true;

    public PasswordDialog(NetworkFolder folder, string? lastUsername = null)
    {
        InitializeComponent();
        FolderLabel.Text = Loc.T("Ordner: {0}", folder.Path);

        Loaded += (_, _) =>
        {
            if (string.IsNullOrEmpty(lastUsername))
            {
                UsernameTextBox.Focus();
            }
            else
            {
                UsernameTextBox.Text = lastUsername;
                PasswordBox.Focus();
            }
        };
    }

    private void Connect_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(UsernameTextBox.Text))
        {
            MessageBox.Show(Loc.T("Bitte geben Sie einen Benutzernamen ein."), Loc.T("Eingabefehler"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
            UsernameTextBox.Focus();
            return;
        }

        DialogResult = true;
    }

    private void Cancel_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
