using Windows.Graphics;
using System.Runtime.InteropServices;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.System;
using Feige.App.Pages;
using Feige.App.Services;

namespace Feige.App;

public sealed partial class MainWindow : Window
{
    private bool _exiting;
    public bool IsUpdateExit { get; private set; }
    private bool _initializing = true, _showingSetup, _backgroundNoticePending;
    private InputNonClientPointerSource? _resizeInput;
    private bool _resizeUpdateQueued;
    private ThemeSettings? _themeSettings;
    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        SetInitialWindowBounds();
        _resizeInput = InputNonClientPointerSource.GetForWindowId(AppWindow.Id);
        _resizeInput.RegionsChanged += (_, _) => QueueResizeRegionUpdate();
        ((FrameworkElement)Content).Loaded += (_, _) =>
        {
            Content.XamlRoot.Changed += (_, _) => QueueResizeRegionUpdate();
            UpdateCaptionColors();
            QueueResizeRegionUpdate();
        };
        ((FrameworkElement)Content).ActualThemeChanged += (_, _) => DispatcherQueue.TryEnqueue(UpdateCaptionColors);
        _themeSettings = ThemeSettings.CreateForWindowId(AppWindow.Id);
        _themeSettings.Changed += OnThemeSettingsChanged;
        ((FrameworkElement)Content).SizeChanged += (_, _) => QueueResizeRegionUpdate();
        AppWindow.Changed += (_, _) => QueueResizeRegionUpdate();
        Closed += (_, _) =>
        {
            _exiting = true;
            if (_themeSettings is not null) _themeSettings.Changed -= OnThemeSettingsChanged;
        };
        AppWindow.Closing += (sender, args) =>
        {
            if (_exiting) return;
            args.Cancel = true;
            if (!AppSession.Current.Settings.Setup.Ended || AppSession.Current.Settings.LoadError is not null)
            { _ = ExitAsync(); return; }
            if (NavFrame.Content is SetupPage repairPage) _ = repairPage.SuspendAsync();
            _backgroundNoticePending = !AppSession.Current.Settings.Setup.BackgroundTipSeen;
            if (_backgroundNoticePending)
            {
                SaveTip(s => s with { BackgroundTipSeen = true });
                if (Notifier.Current.IsAvailable)
                    Notifier.Current.Show("飞鸽快传仍在后台运行", "可从开始菜单重开窗口；停止运行请在设置中退出应用。");
            }
            RefreshGuidance();
            AppWindow.Hide();
        };

        AppSession.Current.StateChanged += (_, _) => RefreshGuidance();
        AppUpdates.Current.Changed += (_, _) => RefreshUpdateUi();
        RefreshUpdateUi();
        _initializing = false;
    }

    private void RefreshUpdateUi()
    {
        if (_showingSetup || UpdateAvailableBar is null) return;
        var updates = AppUpdates.Current;
        bool busy = updates.IsDownloading || updates.IsInstalling || updates.IsWaitingToInstall;
        UpdateAvailableBar.IsOpen = updates.BannerVisible || busy;
        UpdateAvailableBar.Message = busy ? updates.Status
            : updates.Candidate is { } release
                ? $"版本 {release.Version} 可用，点击“更新”直接下载并安装。"
                : "";
        UpdateActionButton.IsEnabled = !busy;
        UpdateActionButton.Content = updates.IsDownloading ? "下载中…" : busy ? "安装中…" : "更新";
        if (updates.InstallNotice is { } notice)
        {
            UpdateResultBar.Severity = updates.InstallNoticeIsSuccess ? InfoBarSeverity.Success : InfoBarSeverity.Warning;
            UpdateResultBar.Title = updates.InstallNoticeIsSuccess ? "更新完成" : "更新未确认";
            UpdateResultBar.Message = notice;
            UpdateResultBar.IsOpen = true;
        }
        else UpdateResultBar.IsOpen = false;
    }

    public async Task InitializeExperienceAsync()
    {
        var session = AppSession.Current;
        if (session.Settings.LoadError is not null) { ShowSettingsRecovery(); return; }
        if (!session.Settings.Setup.Ended) { ShowSetup(); return; }
        ShowMessages();
        await session.LoadLocalHistoryAsync();
        if (session.Settings.NotificationsEnabled) Notifier.Current.Initialize();
        // A deliberate deferral survives restarts; only the user's Continue action resumes initialization.
        if (!session.Settings.Setup.Deferred) await session.StartAsync();
        RefreshGuidance();
    }

    public void ShowMessages()
    {
        _showingSetup = false; NavView.IsPaneVisible = NavView.IsSettingsVisible = true;
        MessagesNavItem.IsSelected = true;
        if (NavFrame.Content is not SendPage) NavFrame.Navigate(typeof(SendPage));
        NavFrame.BackStack.Clear(); RefreshGuidance(); RefreshUpdateUi();
    }
    public void ContinueSetup() { if (!AppSession.Current.IsStarting && !AppSession.Current.IsRunning) ShowSetup(); }
    private void ShowSetup()
    {
        _showingSetup = true; NavView.IsPaneVisible = NavView.IsSettingsVisible = false;
        GuidancePanel.Visibility = Visibility.Collapsed;
        NavFrame.Navigate(typeof(SetupPage)); NavFrame.BackStack.Clear();
    }
    private void ShowSettingsRecovery()
    {
        _showingSetup = true; NavView.IsPaneVisible = NavView.IsSettingsVisible = false;
        GuidancePanel.Visibility = Visibility.Collapsed;
        var stack = new StackPanel { Spacing = 16, MaxWidth = 620, Padding = new Thickness(28) };
        stack.Children.Add(new TextBlock { Text = "已有资料需要处理", FontSize = 26, TextWrapping = TextWrapping.Wrap });
        stack.Children.Add(new TextBlock { Text = AppSession.Current.Settings.LoadError, TextWrapping = TextWrapping.Wrap });
        var retry = new Button { Content = "重试读取" };
        retry.Click += async (_, _) => { AppSession.Current.ReloadSettings(); await InitializeExperienceAsync(); };
        var folder = new Button { Content = "打开资料目录" };
        folder.Click += async (_, _) => { try { await Windows.System.Launcher.LaunchFolderPathAsync(AppSettings.AppDataFolder); } catch { } };
        stack.Children.Add(retry); stack.Children.Add(folder);
        NavFrame.Content = new Page { Content = new ScrollViewer { Content = stack } };
    }
    private void RefreshGuidance()
    {
        if (_initializing || _showingSetup || _exiting) return;
        var session = AppSession.Current; var setup = session.Settings.Setup;
        PendingSetupBar.IsOpen = !session.IsRunning && !session.IsStarting;
        LegacyReviewBar.IsOpen = setup.LegacyReviewPending;
        FirstConnectionBar.IsOpen = session.ActiveFingerprint is not null && !setup.ConnectionTipSeen;
        FirstTransferBar.IsOpen = session.FirstTransferNoticePending;
        NotificationInvitationBar.IsOpen = session.NotificationInvitationPending;
        BackgroundNoticeBar.IsOpen = _backgroundNoticePending;
        GuidancePanel.Visibility = new[] { PendingSetupBar, LegacyReviewBar, FirstConnectionBar, FirstTransferBar, NotificationInvitationBar, BackgroundNoticeBar }.Any(bar => bar.IsOpen)
            ? Visibility.Visible : Visibility.Collapsed;
    }
    private void SaveTip(Func<Feige.Core.Storage.SetupProgress, Feige.Core.Storage.SetupProgress> update)
    {
        try { AppSession.Current.UpdateProgress(update); }
        catch (Exception ex) { PendingSetupBar.Message = "提示状态未保存：" + ErrorText.Describe(ex); PendingSetupBar.IsOpen = true; }
    }
    private void OnContinueSetupClick(object sender, RoutedEventArgs e) { if (!AppSession.Current.IsStarting && !AppSession.Current.IsRunning) ShowSetup(); }
    private void OnReviewSettingsClick(object sender, RoutedEventArgs e)
    { SaveTip(s => s with { LegacyReviewPending = false }); NavView.SelectedItem = NavView.SettingsItem; }
    private void OnLegacyReviewClose(InfoBar sender, object args) => SaveTip(s => s with { LegacyReviewPending = false });
    private void OnConnectionTipClose(InfoBar sender, object args) => SaveTip(s => s with { ConnectionTipSeen = true });
    private void OnTransferTipClose(InfoBar sender, object args) => AppSession.Current.DismissTransferNotice();
    private void OnNotificationInvitationClose(InfoBar sender, object args) => AppSession.Current.DismissNotificationInvitation();
    private void OnBackgroundNoticeClose(InfoBar sender, object args) { _backgroundNoticePending = false; RefreshGuidance(); }
    private void OnEnableNotificationsClick(object sender, RoutedEventArgs e)
    { try { AppSession.Current.EnableNotifications(); } catch (Exception ex) { NotificationInvitationBar.Message = ErrorText.Describe(ex); } }

    private void SetInitialWindowBounds()
    {
        // AppWindow sizes are physical pixels. Leave room around the window even on high-DPI displays.
        double scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        int width = Math.Min((int)(1220 * scale), Math.Max(320, area.Width - (int)(48 * scale)));
        int height = Math.Min((int)(820 * scale), Math.Max(320, area.Height - (int)(48 * scale)));
        AppWindow.MoveAndResize(new RectInt32(area.X + (area.Width - width) / 2, area.Y + (area.Height - height) / 2, width, height));
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint window);

    private void OnShellSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (NavView is null) return;
        bool expanded = e.NewSize.Width >= 1050;
        NavView.PaneDisplayMode = expanded ? NavigationViewPaneDisplayMode.Left : NavigationViewPaneDisplayMode.LeftCompact;
        NavView.IsPaneOpen = expanded;
    }

    private void OnThemeSettingsChanged(ThemeSettings sender, object args)
    {
        if (!_exiting) DispatcherQueue.TryEnqueue(UpdateCaptionColors);
    }

    private void UpdateCaptionColors()
    {
        if (_exiting || Content.XamlRoot is null || !AppWindowTitleBar.IsCustomizationSupported()) return;
        bool contrast = _themeSettings?.HighContrast == true;
        bool dark = ((FrameworkElement)Content).ActualTheme == ElementTheme.Dark;
        var titleBar = AppWindow.TitleBar;
        var foreground = dark ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black;
        titleBar.ButtonForegroundColor = contrast ? null : foreground;
        titleBar.ButtonHoverForegroundColor = contrast ? null : foreground;
        titleBar.ButtonPressedForegroundColor = contrast ? null : foreground;
        titleBar.ButtonInactiveForegroundColor = contrast ? null : Windows.UI.Color.FromArgb(255, 140, 145, 155);
        titleBar.ButtonBackgroundColor = contrast ? null : Microsoft.UI.Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = contrast ? null : Microsoft.UI.Colors.Transparent;
        titleBar.ButtonHoverBackgroundColor = contrast ? null : dark
            ? Windows.UI.Color.FromArgb(255, 55, 59, 68) : Windows.UI.Color.FromArgb(255, 221, 225, 232);
        titleBar.ButtonPressedBackgroundColor = contrast ? null : dark
            ? Windows.UI.Color.FromArgb(255, 70, 76, 87) : Windows.UI.Color.FromArgb(255, 208, 213, 222);
    }

    private void QueueResizeRegionUpdate()
    {
        if (_exiting || _resizeUpdateQueued) return;
        _resizeUpdateQueued = true;
        // The TitleBar control also updates non-client regions during layout. Apply ours afterwards.
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            _resizeUpdateQueued = false;
            if (!_exiting) UpdateResizeRegions();
        })) _resizeUpdateQueued = false;
    }

    private void UpdateResizeRegions()
    {
        if (_resizeInput is null || Content.XamlRoot is not { } root) return;
        NonClientRegionKind[] borders = [NonClientRegionKind.TopBorder, NonClientRegionKind.BottomBorder,
            NonClientRegionKind.LeftBorder, NonClientRegionKind.RightBorder];
        if (AppWindow.Presenter is not OverlappedPresenter { IsResizable: true, State: OverlappedPresenterState.Restored })
        {
            foreach (var border in borders)
                if (_resizeInput.GetRegionRects(border) is { Length: > 0 }) _resizeInput.ClearRegionRects(border);
            return;
        }

        // Non-client regions use physical pixels; keep the hit target equally usable at every DPI.
        var size = AppWindow.ClientSize;
        int grip = Math.Min((int)Math.Ceiling(10 * root.RasterizationScale), Math.Min(size.Width, size.Height) / 2);
        if (grip <= 0) return;
        SetResizeRegion(NonClientRegionKind.TopBorder, new RectInt32(0, 0, size.Width, grip));
        SetResizeRegion(NonClientRegionKind.BottomBorder, new RectInt32(0, size.Height - grip, size.Width, grip));
        SetResizeRegion(NonClientRegionKind.LeftBorder, new RectInt32(0, 0, grip, size.Height));
        SetResizeRegion(NonClientRegionKind.RightBorder, new RectInt32(size.Width - grip, 0, grip, size.Height));
    }

    private void SetResizeRegion(NonClientRegionKind kind, RectInt32 rect)
    {
        var existing = _resizeInput!.GetRegionRects(kind);
        if (existing is { Length: 1 } && existing[0].X == rect.X && existing[0].Y == rect.Y &&
            existing[0].Width == rect.Width && existing[0].Height == rect.Height) return;
        _resizeInput.SetRegionRects(kind, [rect]);
    }

    public async Task ExitAsync()
    {
        if (_exiting) return;
        _exiting = true;
        AppUpdates.Current.Stop();
        if (NavFrame.Content is SetupPage setupPage) setupPage.CancelForExit();
        try { await AppSession.Current.StopAsync(); }
        catch (Exception ex) { AppLog.Write("exit: " + ex.GetType().Name); }
        finally { Notifier.Current.Shutdown(); Close(); Application.Current.Exit(); }
    }

    public Task<bool> PrepareForUpdateAsync()
    {
        if (NavFrame.Content is SendPage page) page.CaptureDraft();
        return AppSession.Current.PrepareForUpdateAsync();
    }
    public async Task ExitForUpdateAsync(string commitPath)
    {
        _exiting = IsUpdateExit = true;
        try
        {
            await AppSession.Current.StopAsync();
            File.WriteAllText(commitPath, "commit");
            Notifier.Current.Shutdown(); Close(); Application.Current.Exit();
        }
        catch { _exiting = IsUpdateExit = false; throw; }
    }
    private async void OnViewUpdateClick(object sender, RoutedEventArgs e)
    {
        if (AppUpdates.Current.Candidate is not { } release || AppUpdates.Current.IsDownloading || AppUpdates.Current.IsInstalling) return;
        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(new TextBlock { Text = $"版本 {release.Version} · 构建 {release.Build} · {UiPromptService.FormatSize(release.Size)}", TextWrapping = TextWrapping.Wrap });
        content.Children.Add(new TextBlock { Text = release.Summary, TextWrapping = TextWrapping.Wrap, MaxHeight = 260 });
        content.Children.Add(new TextBlock { Text = "下载完成后应用会关闭并自动安装；安装结束会自动重新打开。", TextWrapping = TextWrapping.Wrap, Opacity = 0.75 });
        var dialog = new ContentDialog
        {
            Title = "发现新版本",
            Content = new ScrollViewer { Content = content, MaxHeight = 340 },
            PrimaryButtonText = "下载并安装",
            SecondaryButtonText = "查看详情",
            CloseButtonText = "稍后",
            DefaultButton = ContentDialogButton.Primary,
        };
        ContentDialogResult result;
        try { result = await UiPromptService.Current.ShowAsync(dialog); }
        catch (Exception ex) { AppLog.Write($"update: dialog error={ex.GetType().Name}"); return; }
        if (result == ContentDialogResult.Secondary) { NavView.SelectedItem = NavView.SettingsItem; return; }
        if (result == ContentDialogResult.Primary) await AppUpdates.Current.DownloadAndInstallAsync(this);
    }
    private void OnUpdateLater(InfoBar sender, object args) => AppUpdates.Current.Later();
    private void OnUpdateResultClose(InfoBar sender, object args) => AppUpdates.Current.DismissInstallNotice();

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (_initializing || _showingSetup || NavFrame is null) return;
        if (args.IsSettingsSelected)
        {
            NavFrame.Navigate(typeof(SettingsPage));
        }
        else if (args.SelectedItem is NavigationViewItem item)
        {
            switch (item.Tag)
            {
                case "send":
                    NavFrame.Navigate(typeof(SendPage));
                    break;
                case "receive":
                    NavFrame.Navigate(typeof(ReceivePage));
                    break;
                default:
                    throw new InvalidOperationException($"Unknown navigation item tag: {item.Tag}");
            }
        }
    }
}
