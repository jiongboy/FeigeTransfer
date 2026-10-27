using Feige.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.Storage.Pickers;
using Windows.System;

namespace Feige.App.Pages;

/// <summary>
/// Legacy and current file transfer history; connection authorization is handled by the conversation service.
/// </summary>
public sealed partial class ReceivePage : Page
{
    private bool _subscribed;

    /// <summary>Creates the page.</summary>
    public ReceivePage()
    {
        InitializeComponent();
    }

    /// <inheritdoc />
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        ReceivedList.ItemsSource = DirectionBox.SelectedIndex == 1 ? AppSession.Current.SentFiles : AppSession.Current.ReceivedFiles;
        Subscribe();
        RefreshState();
    }

    /// <inheritdoc />
    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        Unsubscribe();
        base.OnNavigatedFrom(e);
    }

    private void Subscribe()
    {
        if (_subscribed)
        {
            return;
        }

        _subscribed = true;
        AppSession.Current.StateChanged += OnStateChanged;
        AppSession.Current.ProgressChanged += OnProgressChanged;
        AppSession.Current.ReceivedFiles.CollectionChanged += OnReceivedChanged;
        AppSession.Current.SentFiles.CollectionChanged += OnReceivedChanged;
    }

    private void Unsubscribe()
    {
        if (!_subscribed)
        {
            return;
        }

        _subscribed = false;
        AppSession.Current.StateChanged -= OnStateChanged;
        AppSession.Current.ProgressChanged -= OnProgressChanged;
        AppSession.Current.ReceivedFiles.CollectionChanged -= OnReceivedChanged;
        AppSession.Current.SentFiles.CollectionChanged -= OnReceivedChanged;
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        RefreshState();
    }

    private void OnReceivedChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        UpdateEmptyState();
    }

    private void OnProgressChanged(object? sender, TransferProgress progress)
    {
        ProgressPanel.Visibility = Visibility.Visible;
        ProgressBarControl.Value = progress.Percent;
        ProgressText.Text = $"{progress.Stage} {progress.FileName} · {UiPromptService.FormatSize(progress.Transferred)} / {UiPromptService.FormatSize(progress.Total)}";
    }

    private void RefreshState()
    {
        AppSession session = AppSession.Current;

        FolderText.Text = session.Settings.ReceiveFolder;

        if (!session.IsRunning)
        {
            StateText.Text = "正在启动…";
            EndpointText.Text = string.Empty;
            FingerprintText.Text = string.Empty;
            return;
        }

        StateText.Text = $"已就绪 · 正在监听端口 {session.ListenPort}";
        EndpointText.Text = $"设备名：{session.Device?.DeviceName}　·　端口由系统分配；防火墙须允许应用，手动连接请使用当前端口";
        FingerprintText.Text = "本机指纹：" + (session.Device?.Fingerprint ?? string.Empty);

        UpdateEmptyState();
    }

    private void UpdateEmptyState()
    {
        bool empty = (DirectionBox.SelectedIndex == 1 ? AppSession.Current.SentFiles.Count : AppSession.Current.ReceivedFiles.Count) == 0;
        EmptyHistoryPanel.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        EmptyHint.Text = DirectionBox.SelectedIndex == 1 ? "还没有发送文件。记录保存在本机，重启后仍可查看。" : "还没有收到文件。记录保存在本机，重启后仍可查看。";
        ClearHistoryButton.IsEnabled = !empty;
    }

    private async void OnClearHistoryClick(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "清空传输记录？",
            Content = "只删除记录列表，已经收到的文件仍留在接收文件夹里。",
            PrimaryButtonText = "清空",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };

        if (await UiPromptService.Current.ShowAsync(dialog) == ContentDialogResult.Primary)
        {
            AppSession.Current.ClearHistory();
            UpdateEmptyState();
        }
    }

    private void OnDirectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ReceivedList is null) return;
        ReceivedList.ItemsSource = DirectionBox.SelectedIndex == 1 ? AppSession.Current.SentFiles : AppSession.Current.ReceivedFiles;
        UpdateEmptyState();
    }

    private async void OnChangeFolderClick(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");

        if (App.MainWindow is null)
        {
            return;
        }

        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
        Windows.Storage.StorageFolder? folder = await picker.PickSingleFolderAsync();

        if (folder is null)
        {
            return;
        }

        try { AppSession.Current.UpdateReceiveFolder(folder.Path); RefreshState(); }
        catch (Exception ex) { StateText.Text = ErrorText.Describe(ex); }
    }

    private async void OnOpenFolderClick(object sender, RoutedEventArgs e)
    {
        string folder = AppSession.Current.Settings.ReceiveFolder;

        try
        {
            Directory.CreateDirectory(folder);
            await Launcher.LaunchFolderPathAsync(folder);
        }
        catch (Exception)
        {
            // Opening Explorer is best effort; the path stays visible in the page.
        }
    }

    private async void OnOpenFileClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ReceivedItem item })
        {
            return;
        }

        try
        {
            Windows.Storage.StorageFile file = await Windows.Storage.StorageFile.GetFileFromPathAsync(item.Path);
            await Launcher.LaunchFileAsync(file);
        }
        catch (Exception)
        {
            // The file may have been moved or deleted since it was received.
        }
    }
}
