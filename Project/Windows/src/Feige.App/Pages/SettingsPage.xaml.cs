using Feige.App.Services;
using Feige.Core.Storage;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Media;
using Windows.Storage.Pickers;

namespace Feige.App.Pages;

/// <summary>Device name, receive folder, and the local identity that peers verify.</summary>
public sealed partial class SettingsPage : Page
{
    private bool _refreshingPreferences;
    /// <summary>Creates the page.</summary>
    public SettingsPage()
    {
        InitializeComponent();
    }

    /// <inheritdoc />
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        AppSession.Current.StateChanged += OnConnectionStateChanged;
        AppUpdates.Current.Changed += OnUpdatesChanged;
        Refresh();
    }

    /// <inheritdoc />
    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        AppSession.Current.StateChanged -= OnConnectionStateChanged;
        AppUpdates.Current.Changed -= OnUpdatesChanged;
        base.OnNavigatedFrom(e);
    }

    private void OnConnectionStateChanged(object? sender, EventArgs e) { RefreshConnectionInfo(); RefreshHiddenConversations(); }

    private void RefreshConnectionInfo()
    {
        AppSession session = AppSession.Current;
        ConnectionStatusText.Text = session.IsRunning ? session.DiscoveryIssue is null ? "局域网已就绪" : "直连已就绪，自动发现暂不可用" : session.IsStarting ? "正在启动…" : "尚未就绪";
        LocalAddressText.Text = session.IsRunning
            ? string.Join("\n", (session.Discovery?.Advertiser.Addresses ?? Feige.Core.Discovery.FeigeService.LocalAddresses()).Select(address => $"{address} · 端口 {session.ListenPort}"))
            : "安全连接尚未准备好，就绪后会显示本机地址和端口。";
        FingerprintText.Text = session.Device?.Fingerprint ?? "正在生成本机身份…";
    }

    private void Refresh()
    {
        AppSession session = AppSession.Current;

        DeviceNameBox.Text = session.Device?.DeviceName ?? session.Settings.DeviceName;
        FolderText.Text = session.Settings.ReceiveFolder;
        RefreshConnectionInfo();
        RefreshHiddenConversations();

        NotifyStatusText.Text = Notifier.Current.Status;
        TestNotifyButton.IsEnabled = Notifier.Current.IsAvailable;
        EnableNotifyButton.IsEnabled = !Notifier.Current.IsAvailable;
        _refreshingPreferences = true;
        AutoUpdatesSwitch.IsOn = session.Settings.AutoCheckUpdates;
        _refreshingPreferences = false;
        RefreshUpdates();

        int count = session.ReceivedFiles.Count + session.SentFiles.Count;
        HistoryText.Text = count == 0
            ? "还没有记录。"
            : $"已保存 {count} 条记录（接收 {session.ReceivedFiles.Count} 条、发送 {session.SentFiles.Count} 条），只保留最近 {TransferHistory.MaxEntries} 条。";
        ClearHistoryButton.IsEnabled = count > 0;
    }

    private void RefreshHiddenConversations()
    {
        var hidden = AppSession.Current.Conversations.Where(AppSession.Current.IsConversationHidden).OrderBy(c => c.Name).ToArray();
        HiddenConversationList.ItemsSource = hidden;
        HiddenConversationList.Visibility = hidden.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        HiddenConversationsEmpty.Visibility = hidden.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        RestoreAllConversationsButton.IsEnabled = hidden.Length > 0;
    }

    private async void OnRestoreConversationClick(object sender, RoutedEventArgs e)
    {
        ConversationItem? conversation = null;
        for (DependencyObject? node = sender as DependencyObject; node is not null && !ReferenceEquals(node, HiddenConversationList); node = VisualTreeHelper.GetParent(node))
            if (node is ListViewItem container) { conversation = HiddenConversationList.ItemFromContainer(container) as ConversationItem; break; }
        if (conversation is null) return;
        try { await AppSession.Current.SetConversationHiddenAsync(conversation, false); HiddenConversationsBar.IsOpen = false; }
        catch (Exception ex) { HiddenConversationsBar.Message = ErrorText.Describe(ex); HiddenConversationsBar.IsOpen = true; }
    }

    private async void OnRestoreAllConversationsClick(object sender, RoutedEventArgs e)
    {
        try { await AppSession.Current.RestoreAllHiddenConversationsAsync(); HiddenConversationsBar.IsOpen = false; }
        catch (Exception ex) { HiddenConversationsBar.Message = ErrorText.Describe(ex); HiddenConversationsBar.IsOpen = true; }
    }

    private async void OnExitAppClick(object sender, RoutedEventArgs e)
    {
        if (App.MainWindow is not MainWindow window) return;
        ExitAppButton.IsEnabled = false;
        await window.ExitAsync();
    }

    private async void OnUsageHelpClick(object sender, RoutedEventArgs e) => await ConnectionHelp.ShowAsync();
    private void OnAutoUpdatesToggled(object sender, RoutedEventArgs e)
    {
        if (_refreshingPreferences || AutoUpdatesSwitch is null || UpdatesPreferenceBar is null) return;
        try { AppSession.Current.Settings.SaveAutoCheckUpdates(AutoUpdatesSwitch.IsOn); AppUpdates.Current.PreferencesChanged(); UpdatesPreferenceBar.IsOpen = false; }
        catch (Exception ex)
        {
            _refreshingPreferences = true; AutoUpdatesSwitch.IsOn = AppSession.Current.Settings.AutoCheckUpdates; _refreshingPreferences = false;
            UpdatesPreferenceBar.Message = ErrorText.Describe(ex); UpdatesPreferenceBar.IsOpen = true;
        }
    }
    private void OnUpdatesChanged(object? sender, EventArgs e) => RefreshUpdates();
    private void RefreshUpdates()
    {
        var updates = AppUpdates.Current;
        CurrentVersionText.Text = updates.VersionText;
        UpdateStatusText.Text = updates.Status;
        CheckUpdatesButton.IsEnabled = updates.CanCheck && !updates.IsChecking;
        CheckUpdatesProgress.IsActive = updates.IsChecking;
        CheckUpdatesProgress.Visibility = updates.IsChecking ? Visibility.Visible : Visibility.Collapsed;
        UpdateReleaseText.Text = updates.ReleaseText;
        UpdateReleaseText.Visibility = updates.Candidate is null ? Visibility.Collapsed : Visibility.Visible;
        DownloadUpdateButton.Visibility = updates.Candidate is not null && !updates.CanInstall ? Visibility.Visible : Visibility.Collapsed;
        DownloadUpdateButton.IsEnabled = updates.CanDownload;
        InstallUpdateButton.Visibility = updates.CanInstall ? Visibility.Visible : Visibility.Collapsed;
        SkipUpdateButton.Visibility = updates.Candidate is not null && !updates.IsDownloading && !updates.IsWaitingToInstall && !updates.IsInstalling ? Visibility.Visible : Visibility.Collapsed;
        UpdateNotesButton.Visibility = updates.Candidate is null ? Visibility.Collapsed : Visibility.Visible;
        CancelUpdateButton.Visibility = updates.IsDownloading || updates.IsWaitingToInstall ? Visibility.Visible : Visibility.Collapsed;
        CancelUpdateButton.Content = updates.IsWaitingToInstall ? "取消等待安装" : "取消下载";
        UpdateDownloadProgress.Visibility = UpdateDownloadText.Visibility = updates.IsDownloading ? Visibility.Visible : Visibility.Collapsed;
        UpdateDownloadProgress.Value = updates.Candidate is { Size: > 0 } release ? updates.Downloaded * 100.0 / release.Size : 0;
        UpdateDownloadText.Text = updates.Candidate is { } candidate ? $"{UiPromptService.FormatSize(updates.Downloaded)} / {UiPromptService.FormatSize(candidate.Size)}" : "";
    }
    private async void OnCheckUpdatesClick(object sender, RoutedEventArgs e) => await AppUpdates.Current.CheckAsync();
    private async void OnDownloadUpdateClick(object sender, RoutedEventArgs e)
    {
        var updates = AppUpdates.Current;
        if (updates.Candidate is not { } release || !updates.CanDownload) return;
        var dialog = new ContentDialog { Title = $"下载 {release.Version}？", Content = $"下载大小：{UiPromptService.FormatSize(release.Size)}。下载并验证后，安装前还会再次询问。",
            PrimaryButtonText = "下载更新", CloseButtonText = "稍后", DefaultButton = ContentDialogButton.Close };
        try { if (await UiPromptService.Current.ShowAsync(dialog) == ContentDialogResult.Primary) await updates.DownloadAsync(); }
        catch (Exception ex) { ShowUpdateError(ex); }
    }
    private async void OnInstallUpdateClick(object sender, RoutedEventArgs e)
    {
        if (!AppUpdates.Current.CanInstall || App.MainWindow is not MainWindow window) return;
        var dialog = new ContentDialog { Title = "安装更新并重启？", Content = "将保存草稿和记录，等待当前文字及文件收发完成后退出并安装。重新启动后需要重新同意连接。",
            PrimaryButtonText = AppSession.Current.HasUpdateWork ? "收发完成后安装" : "安装并重启", CloseButtonText = "稍后", DefaultButton = ContentDialogButton.Close };
        try { if (await UiPromptService.Current.ShowAsync(dialog) == ContentDialogResult.Primary) await AppUpdates.Current.InstallWhenReadyAsync(window); }
        catch (Exception ex) { ShowUpdateError(ex); }
    }
    private void OnCancelUpdateClick(object sender, RoutedEventArgs e) => AppUpdates.Current.Cancel();
    private void OnSkipUpdateClick(object sender, RoutedEventArgs e)
    { try { AppUpdates.Current.Skip(); } catch (Exception ex) { ShowUpdateError(ex); } }
    private async void OnUpdateNotesClick(object sender, RoutedEventArgs e)
    {
        if (AppUpdates.Current.Candidate is not { } release) return;
        try { await Windows.System.Launcher.LaunchUriAsync(new Uri(release.NotesUrl)); }
        catch (Exception ex) { ShowUpdateError(ex); }
    }
    private void ShowUpdateError(Exception error) { UpdatesPreferenceBar.Message = ErrorText.Describe(error); UpdatesPreferenceBar.IsOpen = true; }
    private void OnEnableNotifyClick(object sender, RoutedEventArgs e)
    {
        try { AppSession.Current.EnableNotifications(); Refresh(); }
        catch (Exception ex) { NotifyBar.Severity = InfoBarSeverity.Error; NotifyBar.Message = ErrorText.Describe(ex); NotifyBar.IsOpen = true; }
    }

    private void OnTestNotifyClick(object sender, RoutedEventArgs e)
    {
        Notifier.Current.Show("飞鸽快传测试通知", "能看到这条说明系统通知工作正常。");

        NotifyBar.Severity = Notifier.Current.IsAvailable ? InfoBarSeverity.Success : InfoBarSeverity.Warning;
        NotifyBar.Message = Notifier.Current.IsAvailable ? "已发送，请查看右下角通知中心。" : Notifier.Current.Status;
        NotifyBar.IsOpen = true;
    }

    private async void OnClearHistoryClick(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "清空传输记录？",
            Content = "只删除记录列表，已经收发的文件仍留在磁盘上。",
            PrimaryButtonText = "清空",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };

        if (await UiPromptService.Current.ShowAsync(dialog) == ContentDialogResult.Primary)
        {
            AppSession.Current.ClearHistory();
            Refresh();
        }
    }

    private async void OnOpenAppDataClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await Windows.System.Launcher.LaunchFolderPathAsync(AppSettings.AppDataFolder);
        }
        catch (Exception)
        {
            // Explorer may refuse; best effort only.
        }
    }

    private async void OnSaveNameClick(object sender, RoutedEventArgs e)
    {
        string name = DeviceNameBox.Text.Trim();

        if (name.Length == 0)
        {
            ShowNameBar("设备名不能为空。", InfoBarSeverity.Error);
            return;
        }

        try
        {
            await AppSession.Current.UpdateDeviceNameAsync(name);
            Refresh();
            ShowNameBar("已保存并重新广播。", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowNameBar(ErrorText.Describe(ex), InfoBarSeverity.Error);
        }
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

        try { AppSession.Current.UpdateReceiveFolder(folder.Path); Refresh(); FolderBar.IsOpen = false; }
        catch (Exception ex) { FolderBar.Message = ErrorText.Describe(ex); FolderBar.IsOpen = true; }
    }

    private void ShowNameBar(string message, InfoBarSeverity severity)
    {
        NameBar.Severity = severity;
        NameBar.Message = message;
        NameBar.IsOpen = true;
    }
}
