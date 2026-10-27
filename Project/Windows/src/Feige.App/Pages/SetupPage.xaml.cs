using Feige.App.Services;
using Feige.Core.Storage;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.Storage.Pickers;

namespace Feige.App.Pages;

public sealed partial class SetupPage : Page
{
    private AppSession Session => AppSession.Current;
    private CancellationTokenSource? _attempt;
    private string _folder = "";
    private int _step;
    private bool _active, _transitioning, _keepServices, _repair, _preparationAttempted;

    public SetupPage() { InitializeComponent(); }
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e); _active = true;
        _repair = Session.Settings.Setup.Ended;
        SetupNameBox.Text = Session.Settings.DeviceName;
        _folder = Session.Settings.ReceiveFolder; UpdateFolder();
        SetupUpdatesSwitch.IsOn = Session.Settings.AutoCheckUpdates;
        Session.StateChanged += OnStateChanged;
        ShowStep(Session.Settings.Setup.Step);
        if (_step == 2) _ = PrepareAsync();
    }
    protected override async void OnNavigatedFrom(NavigationEventArgs e)
    {
        _active = false; _attempt?.Cancel(); Session.StateChanged -= OnStateChanged;
        base.OnNavigatedFrom(e);
        if (!_keepServices)
            try { await Session.PausePreparationAsync(); } catch (Exception ex) { AppLog.Write("setup pause: " + ex.GetType().Name); }
    }

    private void ShowStep(int step)
    {
        _step = step; _preparationAttempted = false;
        StepText.Text = step == 1 ? "1/2 本机设置" : _repair ? "继续设置 · 准备连接" : "2/2 准备连接";
        Heading.Text = step == 1 ? "欢迎使用飞鸽快传" : "准备连接";
        Introduction.Text = step == 1 ? "同一局域网内，轻松互传文字、图片和文件。无需注册，对方同意连接后即可开始。"
            : "飞鸽快传需要识别本机并访问局域网。接下来可能出现系统授权，请按实际需要选择。";
        LocalSettingsPanel.Visibility = step == 1 ? Visibility.Visible : Visibility.Collapsed;
        ConnectionPanel.Visibility = step == 2 ? Visibility.Visible : Visibility.Collapsed;
        SetupBackButton.Visibility = SetupLaterButton.Visibility = step == 2 ? Visibility.Visible : Visibility.Collapsed;
        SetupLaterButton.Content = _repair ? "返回主界面" : "稍后设置";
        SetupErrorBar.IsOpen = false; SetupScroller.ChangeView(null, 0, null);
        RefreshState();
    }
    private void OnStateChanged(object? sender, EventArgs e) => RefreshState();
    private void RefreshState()
    {
        if (!_active) return;
        IdentityStateText.Text = "本机身份：" + Session.IdentityStatus;
        FolderStateText.Text = "接收位置：" + Session.FolderStatus;
        NetworkStateText.Text = "局域网连接：" + Session.NetworkStatus;
        SetupBusyRing.IsActive = _step == 2 && Session.IsStarting;
        SetupBusyRing.Visibility = SetupBusyRing.IsActive ? Visibility.Visible : Visibility.Collapsed;
        SetupPrimaryButton.Content = _step == 1 ? "保存并继续" : Session.IsRunning ? "开始使用" : Session.IsStarting ? "正在准备…" : "重试";
        SetupPrimaryButton.IsEnabled = !_transitioning && (_step == 1 || !Session.IsStarting);
        if (_step == 2)
        {
            if (Session.IsStarting || Session.IsRunning || !_preparationAttempted) SetupErrorBar.IsOpen = false;
            else { SetupErrorBar.Message = Session.Status; SetupErrorBar.IsOpen = true; }
        }
    }
    private void SetTransition(bool busy)
    {
        _transitioning = busy;
        SetupBackButton.IsEnabled = SetupLaterButton.IsEnabled = !busy;
        SetupNameBox.IsEnabled = SetupChangeFolderButton.IsEnabled = SetupUpdatesSwitch.IsEnabled = !busy; RefreshState();
    }
    private async Task PrepareAsync()
    {
        _attempt?.Cancel(); _attempt?.Dispose(); _attempt = new(); _preparationAttempted = true;
        try { await Session.StartAsync(_attempt.Token); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (_active) ShowError(ex); }
        if (_active) RefreshState();
    }
    public void CancelForExit() { _active = false; _attempt?.Cancel(); }
    public async Task SuspendAsync()
    {
        try { await PauseAsync(); if (_active) RefreshState(); }
        catch (Exception ex) { if (_active) ShowError(ex); }
    }
    private async Task PauseAsync()
    {
        _attempt?.Cancel();
        await Session.PausePreparationAsync();
    }
    private async void OnPrimaryClick(object sender, RoutedEventArgs e)
    {
        if (_transitioning) return;
        if (_step == 2)
        {
            if (!Session.IsRunning) { await PrepareAsync(); return; }
            try { Session.EndGuide(false); _keepServices = true; ((MainWindow)App.MainWindow!).ShowMessages(); }
            catch (Exception ex) { ShowError(ex); }
            return;
        }
        SetupNameError.IsOpen = SetupFolderError.IsOpen = SetupErrorBar.IsOpen = false;
        string name;
        try { name = SetupValidation.DeviceName(SetupNameBox.Text); }
        catch (Exception ex) { SetupNameError.Message = ex.Message; SetupNameError.IsOpen = true; SetupNameBox.Focus(FocusState.Programmatic); return; }
        SetTransition(true);
        try
        {
            try { await Task.Run(() => SetupValidation.ReceiveFolder(_folder)); }
            catch (Exception ex) { if (_active) { SetupFolderError.Message = ErrorText.Describe(ex); SetupFolderError.IsOpen = true; } return; }
            if (!_active) return;
            Session.SaveGuideSettings(name, _folder, SetupUpdatesSwitch.IsOn);
            ShowStep(2); SetTransition(false); await PrepareAsync();
        }
        catch (Exception ex) { if (_active) ShowError(ex); }
        finally { if (_active) SetTransition(false); }
    }
    private async void OnBackClick(object sender, RoutedEventArgs e)
    {
        if (_transitioning) return;
        SetTransition(true);
        try { await PauseAsync(); if (_active) ShowStep(1); }
        catch (Exception ex) { if (_active) ShowError(ex); }
        finally { if (_active) SetTransition(false); }
    }
    private async void OnLaterClick(object sender, RoutedEventArgs e)
    {
        if (_transitioning) return;
        SetTransition(true);
        try
        {
            await PauseAsync(); if (!_active) return;
            Session.EndGuide(true); _keepServices = true;
            ((MainWindow)App.MainWindow!).ShowMessages();
        }
        catch (Exception ex) { if (_active) ShowError(ex); }
        finally { if (_active) SetTransition(false); }
    }
    private async void OnChangeFolderClick(object sender, RoutedEventArgs e)
    {
        if (_transitioning || App.MainWindow is null) return;
        SetTransition(true);
        try
        {
            var picker = new FolderPicker(); picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
            var folder = await picker.PickSingleFolderAsync();
            if (_active && folder is not null) { _folder = folder.Path; UpdateFolder(); SetupFolderError.IsOpen = false; }
            if (_active) SetupChangeFolderButton.Focus(FocusState.Programmatic);
        }
        catch (Exception ex) { if (_active) ShowError(ex); }
        finally { if (_active) SetTransition(false); }
    }
    private void UpdateFolder()
    {
        SetupFolderText.Text = string.IsNullOrEmpty(_folder) ? "系统下载目录不可用，请选择接收文件夹。" : _folder;
        ToolTipService.SetToolTip(SetupFolderText, _folder);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(SetupFolderText, _folder);
    }
    private async void OnConnectionHelpClick(object sender, RoutedEventArgs e) => await ConnectionHelp.ShowAsync();
    private void ShowError(Exception ex) { SetupErrorBar.Message = ErrorText.Describe(ex); SetupErrorBar.IsOpen = true; }
}
