using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using NetworkFolderManager.Models;
using NetworkFolderManager.Services;
using NetworkFolderManager.ViewModels;

namespace NetworkFolderManager;

public partial class MainWindow : Window, IDisposable
{
    private readonly MainViewModel _vm = new();
    private bool _allowClose;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;

        _vm.Chat.MessageAdded += (_, _) => ScrollToEnd();
        _vm.Chat.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ChatViewModel.SelectedPeer))
                ScrollToEnd();
        };

        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);

        Activated += (_, _) => UpdateChatVisibility();
        Deactivated += (_, _) => UpdateChatVisibility();
        PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty)
                UpdateChatVisibility();
        };

        Opened += async (_, _) => await _vm.InitializeAsync();
    }

    /// <summary>Fenster wirklich schließen (Beenden im Menü) statt nur ausblenden.</summary>
    public void AllowClose() => _allowClose = true;

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // Schließen blendet das Fenster nur aus – der Chat läuft weiter (Beenden: Menüleistensymbol bzw. Cmd+Q)
        if (!_allowClose && e.CloseReason == WindowCloseReason.WindowClosing)
        {
            e.Cancel = true;
            Hide();
            UpdateChatVisibility();
        }
        base.OnClosing(e);
    }

    public void ShowFromTray()
    {
        if (!IsVisible)
            Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
        UpdateChatVisibility();
    }

    public void Dispose() => _vm.Dispose();

    #region Chat

    private void UpdateChatVisibility()
    {
        _vm.Chat.IsChatVisible = IsVisible && IsActive && ReferenceEquals(MainTabs.SelectedItem, ChatTab);
        _vm.Chat.MarkAsRead();
    }

    private void MainTabs_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (e.Source == MainTabs)
            UpdateChatVisibility();
    }

    private void ScrollToEnd() => Dispatcher.UIThread.Post(() =>
    {
        if (_vm.Chat.SelectedPeer is { Messages.Count: > 0 } peer)
            MessagesList.ScrollIntoView(peer.Messages[^1]);
    }, DispatcherPriority.Background);

    private void MessageBox_KeyDown(object? sender, KeyEventArgs e)
    {
        // Enter = senden, Umschalt+Enter = neue Zeile
        if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            if (_vm.Chat.SendCommand.CanExecute(null))
                _vm.Chat.SendCommand.Execute(null);
            e.Handled = true;
            return;
        }

        // Cmd+V / Strg+V: Bild oder Dateien aus der Zwischenablage senden (reiner Text wird normal eingefügt)
        if (e.Key == Key.V && (e.KeyModifiers.HasFlag(KeyModifiers.Meta) || e.KeyModifiers.HasFlag(KeyModifiers.Control)))
            _ = _vm.Chat.TrySendClipboardImageAsync();
    }

    private void Bubble_Tapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: ChatMessage { FilePath: not null } message } &&
            (message.IsImage || message.IsFile))
            _vm.Chat.OpenAttachmentCommand.Execute(message);
    }

    #endregion

    #region Ordner

    private void ManualPath_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _vm.AddManualFolderCommand.CanExecute(null))
        {
            _vm.AddManualFolderCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void Folders_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (_vm.OpenFolderCommand.CanExecute(null))
            _vm.OpenFolderCommand.Execute(null);
    }

    private void Received_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is ListBox { SelectedItem: NewFileEventArgs file })
            _vm.ShowReceivedFileCommand.Execute(file);
    }

    #endregion

    #region Drag & Drop

    private static bool HasFiles(DragEventArgs e) => e.Data.Contains(DataFormats.Files);

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = HasFiles(e) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        if (!HasFiles(e))
            return;

        var paths = (e.Data.GetFiles() ?? Enumerable.Empty<IStorageItem>())
            .Select(f => f.TryGetLocalPath())
            .Where(p => p != null)
            .Select(p => p!)
            .ToList();
        if (paths.Count == 0)
            return;

        // Chat-Tab → an den Kontakt senden, sonst in den ausgewählten Ordner hochladen
        if (ReferenceEquals(MainTabs.SelectedItem, ChatTab))
            _vm.Chat.SendFiles(paths);
        else
            await _vm.UploadAsync(paths);
        e.Handled = true;
    }

    #endregion
}
