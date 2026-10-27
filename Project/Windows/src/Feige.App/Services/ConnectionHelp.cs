using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Feige.App.Services;

public static class ConnectionHelp
{
    public static async Task ShowNetworkTroubleshootingAsync()
    {
        var content = new StackPanel { Spacing = 14 };
        foreach (string text in new[] {
            "连接未能建立。下面是可检查的项目，不表示已检测到 VPN、TUN 或透明代理，也不能确认它们就是原因。",
            "如果正在使用代理或 VPN：在相应软件中检查“绕过局域网 / 局域网直连”规则，确认对方的真实局域网地址走直连；不同软件的设置名称可能不同。调整后可刷新设备并重新请求连接。",
            "同时检查对方是否正在运行飞鸽快传，并使用其设置中当前显示的地址和端口；允许应用通过防火墙，无需关闭防火墙。",
            "多层路由、双层 NAT、访客网络或设备隔离也可能阻挡连接。可尝试下级网络主动连接上级设备，但仍取决于实际路由和网络策略。",
            "应用不会自动更改代理、VPN、路由或防火墙设置。" })
            content.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap });
        var dialog = new ContentDialog { Title = "连接排查", Content = new ScrollViewer { Content = content, MaxHeight = 450 },
            CloseButtonText = "关闭", DefaultButton = ContentDialogButton.Close };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(dialog, "NetworkTroubleshootingDialog");
        await UiPromptService.Current.ShowAsync(dialog);
    }

    public static async Task ShowAsync()
    {
        var content = new StackPanel { Spacing = 12 };
        foreach (string text in new[] {
            "1. 在另一台电脑打开飞鸽快传，并连接到同一局域网。访客 Wi-Fi、VPN 或网络隔离可能影响互访。",
            "2. 从附近设备发起连接；未发现设备时可刷新或手动输入对方的地址和端口。本机连接信息在设置中。手动连接优先使用局域网 IPv4；fe80:: 地址后的网卡编号须以发起连接的电脑为准。",
            "3. 对方同意一次后，即可双向发送文字、图片和文件；断开后重新连接需要再次同意。",
            "Windows 可能询问防火墙访问权限，请按你信任的网络环境允许此应用。无需关闭防火墙或改动网络类型；受单位策略限制时，可请管理员协助。",
            "普通 HTTP 系统代理不参与文件直连。VPN、TUN 或透明代理可能改变局域网路由；请检查代理软件的局域网绕过/分流规则，并确认对方地址可直接访问。应用不会自行修改代理或 VPN 设置。",
            "多台路由器若使用 AP/桥接并处于同一网段，通常仍可自动发现；跨子网或双层 NAT 时，自动发现通常无法穿过路由器。可尝试手动连接对方的局域网地址，但双方仍需有可达路由、回程路径和相应防火墙许可。",
            "下级路由器后的电脑可尝试主动连接上级局域网的电脑；反方向可能被 NAT 阻挡。若网络由你管理，可考虑同一局域网或 AP/桥接；单位、访客网络或相同网段的隔离无法由应用绕过。不需要关闭防火墙。",
            "没有发现设备不代表权限被拒绝。切换 Wi-Fi、插拔网线或更改 VPN 后可刷新设备；本机连接信息在设置中，以当前地址和端口为准。" })
            content.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap });
        var dialog = new ContentDialog { Title = "使用帮助", Content = new ScrollViewer { Content = content, MaxHeight = 450 },
            PrimaryButtonText = "打开防火墙设置", CloseButtonText = "关闭", DefaultButton = ContentDialogButton.Close };
        if (await UiPromptService.Current.ShowAsync(dialog) == ContentDialogResult.Primary)
            try { await Windows.System.Launcher.LaunchUriAsync(new Uri("windowsdefender://network/")); } catch (Exception ex) { AppLog.Write("connection help: " + ex.GetType().Name); }
    }
}
