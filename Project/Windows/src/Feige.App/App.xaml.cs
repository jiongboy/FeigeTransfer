using Feige.App.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace Feige.App;

public partial class App : Application
{
    public static Window? MainWindow { get; private set; }
    private AppInstance? _instance;
    private DispatcherQueue? _dispatcher;
    private Mutex? _installationMutex;
    public App()
    {
        UnhandledException += (_, args) => AppLog.Write("UI unhandled: " + args.Exception.GetType().Name + ": " + args.Exception.Message);
        AppLog.Write("launch: initializing application");
        InitializeComponent();
    }
    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        if (Environment.GetCommandLineArgs().Contains("--unregister-notifications"))
        {
            Notifier.RemoveDesktopRegistration(); Exit(); return;
        }
        AppLog.Write("launch: preparing single instance");
        _instance = AppInstance.FindOrRegisterForKey("Feige.Windows.Main");
        if (!_instance.IsCurrent)
        {
            await _instance.RedirectActivationToAsync(AppInstance.GetCurrent().GetActivatedEventArgs());
            Exit(); return;
        }
        _instance.Activated += (_, _) => _dispatcher?.TryEnqueue(ShowMainWindow);
        if (!AppEnvironment.IsPackaged) _installationMutex = new Mutex(false, @"Local\Songli.FeigeTransfer");
        AppLog.Write("launch: creating window");
        var window = new MainWindow(); MainWindow = window; window.Activate();
        var dispatcher = DispatcherQueue.GetForCurrentThread();
        _dispatcher = dispatcher;
        UiPromptService.Current.Initialize(dispatcher, () => (window.Content as FrameworkElement)?.XamlRoot);
        AppSession.Current.Initialize(dispatcher);
        // Optional notification registration waits for an explicit preference, never the welcome page.
        AppLog.Write($"application: packaged={AppEnvironment.IsPackaged} architecture={System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture} version={GetType().Assembly.GetName().Version}");
        await window.InitializeExperienceAsync();
        AppUpdates.Current.Initialize(dispatcher);
        window.Closed += (_, _) => AppUpdates.Current.Stop(preserveHandoff: window.IsUpdateExit);
    }
    public static void ShowMainWindow()
    {
        if (MainWindow is not { } window) return;
        window.AppWindow.Show();
        if (window.AppWindow.Presenter is OverlappedPresenter presenter && presenter.State == OverlappedPresenterState.Minimized) presenter.Restore();
        window.Activate();
    }
}
