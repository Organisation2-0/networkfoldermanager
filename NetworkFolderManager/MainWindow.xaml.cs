using NetworkFolderManager.Services;
using NetworkFolderManager.ViewModels;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using DataFormats = System.Windows.DataFormats;
using DragDropEffects = System.Windows.DragDropEffects;
using DragEventArgs = System.Windows.DragEventArgs;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace NetworkFolderManager;

/// <summary>
/// Interaktionslogik für MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private bool _trayHintShown;
    private bool _initialized;

    private MainWindowViewModel ViewModel => (MainWindowViewModel)DataContext;

    public MainWindow()
    {
        InitializeComponent();

        SourceInitialized += (_, _) => EnableDarkTitleBar();
        Loaded += async (_, _) => await EnsureInitializedAsync();
        Closing += OnClosing;
        Closed += (_, _) =>
        {
            ViewModel.Dispose();
            System.Windows.Application.Current.Shutdown();
        };
        Activated += (_, _) => UpdateChatVisibility();
        Deactivated += (_, _) => UpdateChatVisibility();
        IsVisibleChanged += (_, _) => UpdateChatVisibility();
        DragOver += OnDragOver;
        Drop += OnDrop;

        NotificationService.RestoreRequested += (_, _) => RestoreFromTray();
        NotificationService.ChatNotificationClicked += (_, peerId) =>
        {
            MainTabs.SelectedItem = ChatTab;
            ViewModel.Chat.SelectPeer(peerId);
            UpdateChatVisibility();
        };

        // Admin-Tab verschwindet beim Abmelden → auf den Server-Tab wechseln
        ViewModel.Servers.AdminAccessChanged += (_, _) =>
        {
            if (!ViewModel.Servers.HasAdminAccess && MainTabs.SelectedItem == AdminTab)
                MainTabs.SelectedItem = ServerTab;
        };

        ViewModel.Chat.MessageAdded += (_, _) =>
            Dispatcher.BeginInvoke(() => MessageScroller.ScrollToEnd(), System.Windows.Threading.DispatcherPriority.Background);
    }

    private async Task EnsureInitializedAsync()
    {
        if (_initialized)
            return;
        _initialized = true;
        await ViewModel.InitializeAsync();
    }

    /// <summary>
    /// Start im Infobereich (Autostart): Fenster nicht anzeigen, aber alles initialisieren.
    /// </summary>
    public async void StartHidden()
    {
        await EnsureInitializedAsync();
    }

    public void RestoreFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
        Topmost = true;  // sicher nach vorne holen
        Topmost = false;
        UpdateChatVisibility();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        // Im Infobereich weiterlaufen, damit Benachrichtigungen und Chat aktiv bleiben
        if (!NotificationService.IsExiting && ViewModel.MinimizeToTray)
        {
            e.Cancel = true;
            Hide();

            if (!_trayHintShown)
            {
                _trayHintShown = true;
                NotificationService.ShowToastNotification(
                    Loc.T("NetworkFolderManager läuft weiter"),
                    Loc.T("Das Programm ist im Infobereich. Rechtsklick auf das Symbol → Beenden."));
            }
        }
    }

    #region Chat

    private bool IsChatTabSelected => MainTabs.SelectedItem == ChatTab;

    private void MainTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source != MainTabs)
            return;
        UpdateChatVisibility();
        if (IsChatTabSelected)
            Dispatcher.BeginInvoke(() => MessageScroller.ScrollToEnd(), System.Windows.Threading.DispatcherPriority.Background);
    }

    private void UpdateChatVisibility()
    {
        if (DataContext is not MainWindowViewModel vm)
            return;
        vm.Chat.IsChatVisible = IsVisible && IsActive && WindowState != WindowState.Minimized && IsChatTabSelected;
        vm.Chat.MarkAsRead();
    }

    private void ChatInput_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var chat = ViewModel.Chat;

        // Enter = senden, Umschalt+Enter = neue Zeile
        if (e.Key == Key.Enter && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            if (chat.SendCommand.CanExecute(null))
                chat.SendCommand.Execute(null);
            e.Handled = true;
            return;
        }

        // Strg+V mit Bild/Dateien in der Zwischenablage → direkt senden
        if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (!System.Windows.Clipboard.ContainsText() &&
                (System.Windows.Clipboard.ContainsImage() || System.Windows.Clipboard.ContainsFileDropList()))
            {
                e.Handled = true;
                _ = chat.TrySendClipboardImageAsync();
            }
        }
    }

    #endregion

    #region Netzwerkordner

    private async void SetupShare_Click(object sender, RoutedEventArgs e)
    {
        var password = SharePasswordBox.Password;
        var confirm = SharePasswordConfirmBox.Password;
        await ViewModel.SetupOwnShareAsync(password, confirm);
        SharePasswordBox.Clear();
        SharePasswordConfirmBox.Clear();
    }

    #endregion

    #region Drag & Drop

    private void OnDragOver(object sender, DragEventArgs e)
    {
        var hasFiles = e.Data.GetDataPresent(DataFormats.FileDrop);
        var serverTab = MainTabs.SelectedItem == ServerTab || MainTabs.SelectedItem == AdminTab;
        var canDrop = serverTab ? false : IsChatTabSelected
            ? ViewModel.Chat.SelectedPeer != null
            : ViewModel.SelectedFolder != null && !ViewModel.IsUploading;

        e.Effects = hasFiles && canDrop ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files ||
            MainTabs.SelectedItem == ServerTab || MainTabs.SelectedItem == AdminTab)
            return;

        if (IsChatTabSelected)
            ViewModel.Chat.SendFiles(files);
        else
            await ViewModel.UploadFilesAsync(files);
    }

    #endregion

    #region Ordnerliste

    private void FolderList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel.SelectedFolder is not { } folder)
            return;

        if (folder.IsConnected)
            ViewModel.OpenInExplorerCommand.Execute(null);
        else
            ViewModel.ConnectToFolderCommand.Execute(null);
    }

    private async void ServerFolderList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d &&
            ItemsControl.ContainerFromElement(ServerFolderList, d) is ListBoxItem { DataContext: ServerFolderItem item })
            await ViewModel.Servers.OpenFolderAsync(item);
    }

    private void ReceivedList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        ViewModel.OpenReceivedFileCommand.Execute(ReceivedList.SelectedItem);
    }

    #endregion

    #region Dunkle Titelleiste

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private void EnableDarkTitleBar()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var on = 1;
            // 20 = DWMWA_USE_IMMERSIVE_DARK_MODE (Win10 20H1+), 19 = ältere Builds
            if (DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int)) != 0)
                DwmSetWindowAttribute(hwnd, 19, ref on, sizeof(int));

            // 35 = DWMWA_CAPTION_COLOR (Win11): schwarz
            var black = 0x00000000;
            DwmSetWindowAttribute(hwnd, 35, ref black, sizeof(int));
        }
        catch
        {
            // ältere Windows-Version
        }
    }

    #endregion
}
