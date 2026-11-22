using NetworkFolderManager.ViewModels;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DataFormats = System.Windows.DataFormats;
using DragDropEffects = System.Windows.DragDropEffects;
using DragEventArgs = System.Windows.DragEventArgs;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace NetworkFolderManager.Views;

/// <summary>Fenster eines geöffneten (entschlüsselten) Server-Ordners.</summary>
public partial class VaultFolderWindow : Window
{
    private readonly VaultFolderViewModel _vm;
    private bool _videoPlaying;

    public VaultFolderWindow(VaultFolderViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        InitializeComponent();

        SourceInitialized += (_, _) => Dialogs.DarkTitleBar.Apply(this);
        Loaded += async (_, _) =>
        {
            UpdateNav();
            await _vm.InitializeAsync();
        };
        _vm.CloseRequested += (_, _) => Close();
        _vm.PropertyChanged += OnVmPropertyChanged;
        PreviewKeyDown += OnPreviewKeyDown;
        DragOver += (_, e) =>
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) && !_vm.IsUploading ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        };
        Drop += OnDrop;
        Closing += (_, _) => StopVideo();
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(VaultFolderViewModel.IsPhotos):
                UpdateNav();
                break;
            case nameof(VaultFolderViewModel.ViewerVideo):
                if (_vm.ViewerVideo != null)
                {
                    Video.Source = _vm.ViewerVideo;
                    Video.Visibility = Visibility.Visible;
                    Video.Play();
                    _videoPlaying = true;
                }
                else
                {
                    StopVideo();
                }
                break;
            case nameof(VaultFolderViewModel.IsViewerOpen):
                if (_vm.IsViewerOpen)
                    Viewer.Focus();
                break;
        }
    }

    private void UpdateNav()
    {
        NavPhotos.Tag = _vm.IsPhotos ? "on" : null;
        NavFiles.Tag = _vm.IsPhotos ? null : "on";
    }

    private void StopVideo()
    {
        Video.Stop();
        Video.Source = null;
        Video.Visibility = Visibility.Collapsed;
        _videoPlaying = false;
    }

    private void Video_Click(object sender, MouseButtonEventArgs e)
    {
        if (_videoPlaying)
            Video.Pause();
        else
            Video.Play();
        _videoPlaying = !_videoPlaying;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_vm.IsViewerOpen)
            return;
        switch (e.Key)
        {
            case Key.Escape:
                _vm.CloseViewerCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Left:
                _vm.ViewerPreviousCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Right:
                _vm.ViewerNextCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Space when _vm.ViewerVideo != null:
                Video_Click(this, null!);
                e.Handled = true;
                break;
        }
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
            await _vm.UploadPathsAsync(paths);
    }

    private void PhotoList_SizeChanged(object sender, SizeChangedEventArgs e) =>
        _vm.SetPhotoAreaWidth(PhotoList.ActualWidth - 30);

    private void FileList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && ItemsControl.ContainerFromElement((ItemsControl)sender, d) is ListBoxItem { DataContext: VaultItem item })
            _vm.OpenItemCommand.Execute(item);
    }

    private void FileList_KeyDown(object sender, KeyEventArgs e)
    {
        var item = _vm.SelectedItem;
        switch (e.Key)
        {
            case Key.Enter when item != null:
                _vm.OpenItemCommand.Execute(item);
                break;
            case Key.Delete when item != null:
                _vm.DeleteCommand.Execute(item);
                break;
            case Key.F2 when item != null:
                _vm.RenameCommand.Execute(item);
                break;
            case Key.Back:
                _vm.GoUpCommand.Execute(null);
                break;
            case Key.F5:
                _vm.RefreshCommand.Execute(null);
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private void ItemMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { DataContext: VaultItem item } menu)
            return;
        switch (menu.Tag as string)
        {
            case "open": _vm.OpenItemCommand.Execute(item); break;
            case "download": _vm.DownloadCommand.Execute(item); break;
            case "rename": _vm.RenameCommand.Execute(item); break;
            case "delete": _vm.DeleteCommand.Execute(item); break;
        }
    }
}
