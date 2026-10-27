using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;
using Microsoft.Windows.AppNotifications;

namespace Feige.App.Services;

/// <summary>
/// Desktop toasts for arrivals, completions and failures.
/// </summary>
/// <remarks>
/// <para>MSIX uses its package identity with ToastNotificationManager. EXE installs register
/// AppNotificationManager for desktop activation. Registration can fail on self-contained
/// Windows App SDK 2.5.1 builds (upstream issue #6774); in-window status remains available.</para>
/// <para>
/// Notifications are a nicety, never a dependency: when the platform refuses, <see cref="IsAvailable"/> stays false
/// and the app carries on with only in-window status text.
/// </para>
/// </remarks>
public sealed class Notifier
{
    private const string GenericTemplate = "ToastGeneric";

    private ToastNotifier? _notifier;
    private bool _desktop;

    private Notifier()
    {
    }

    /// <summary>Process-wide notifier.</summary>
    public static Notifier Current { get; } = new();

    /// <summary>True once a toast notifier could be obtained.</summary>
    public bool IsAvailable { get; private set; }
    public static bool IsSupported => AppEnvironment.IsPackaged || OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041);

    /// <summary>Short user-facing description of the current state, shown on the settings page.</summary>
    public string Status { get; private set; } = "系统通知未启用，窗口内会显示收发状态。";

    /// <summary>Prepares the notifier. Safe to call once at start-up; never throws.</summary>
    public void Initialize()
    {
        if (IsAvailable)
        {
            return;
        }

        try
        {
            if (AppEnvironment.IsPackaged) _notifier = ToastNotificationManager.CreateToastNotifier();
            else
            {
                AppNotificationManager.Default.NotificationInvoked += (_, _) => App.MainWindow?.DispatcherQueue.TryEnqueue(App.ShowMainWindow);
                AppNotificationManager.Default.Register("飞鸽快传", new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico")));
                _desktop = true;
            }
            IsAvailable = true;
            Status = "通知已启用：文件收发完成会弹出系统提示。";
        }
        catch (Exception ex)
        {
            AppLog.Write("notification initialization: " + ex.GetType().Name + ": " + ex.Message);
            _notifier = null;
            IsAvailable = false;
            Status = "系统通知暂不可用，收发状态会显示在窗口内。";
        }
    }

    /// <summary>Shows a two-line toast. Silently does nothing when notifications are unavailable.</summary>
    /// <param name="title">First line, bold in the toast.</param>
    /// <param name="body">Second line; keep it short.</param>
    public void Show(string title, string body)
    {
        if (!IsAvailable || (!_desktop && _notifier is null) || string.IsNullOrWhiteSpace(title))
        {
            return;
        }

        try
        {
            // Built through the DOM rather than string concatenation so file names containing <, & or quotes cannot
            // break the payload.
            var document = new XmlDocument();

            XmlElement toast = document.CreateElement("toast");
            XmlElement visual = document.CreateElement("visual");
            XmlElement binding = document.CreateElement("binding");

            binding.SetAttribute("template", GenericTemplate);
            AppendText(document, binding, title);
            AppendText(document, binding, body ?? string.Empty);

            visual.AppendChild(binding);
            toast.AppendChild(visual);
            document.AppendChild(toast);

            if (_desktop) AppNotificationManager.Default.Show(new AppNotification(document.GetXml()));
            else _notifier!.Show(new ToastNotification(document));
        }
        catch (Exception ex)
        {
            // A toast that fails to show must never surface as a transfer error.
            AppLog.Write("toast failed: " + ex.Message);
        }
    }

    public void Shutdown()
    {
        if (_desktop)
            try { AppNotificationManager.Default.Unregister(); } catch (Exception ex) { AppLog.Write("notification unregister: " + ex.GetType().Name); }
    }

    public static void RemoveDesktopRegistration()
    {
        if (AppEnvironment.IsPackaged) return;
        try { AppNotificationManager.Default.UnregisterAll(); }
        catch (Exception ex) { AppLog.Write("notification uninstall: " + ex.GetType().Name); }
    }

    private static void AppendText(XmlDocument document, XmlElement parent, string value)
    {
        XmlElement text = document.CreateElement("text");
        text.InnerText = value;
        parent.AppendChild(text);
    }
}
