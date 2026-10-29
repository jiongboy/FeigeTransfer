import SwiftUI

/// Connection troubleshooting and usage help, matching the Windows guidance text while using
/// macOS-specific system names. The app never changes proxy, VPN, routing or firewall settings.
struct ConnectionHelpView: View {
    enum Mode { case usage, troubleshooting }

    @Environment(\.dismiss) private var dismiss
    var mode: Mode = .usage

    private let usage: [(String, String)] = [
        ("1 在同一局域网", "在另一台电脑打开飞鸽快传并连接到同一局域网。访客 Wi‑Fi、VPN 或网络隔离可能影响互访。"),
        ("2 发起连接", "从附近设备发起连接；未发现设备时可刷新，或手动输入对方的局域网地址和端口（对方的“偏好设置 → 本机连接信息”）。"),
        ("3 对方同意一次", "对方同意后即可在同一个会话里双向发送文字、图片和文件；断开后重新连接需要再次同意。"),
        ("4 允许网络访问", "macOS 可能询问是否允许接收传入的网络连接。请按你信任的网络环境允许，无需关闭防火墙或改动网络类型。"),
        ("5 代理与 VPN", "普通 HTTP 系统代理不参与文件直连。VPN、TUN 或透明代理可能改变局域网路由；请检查代理软件的局域网绕过/分流规则，确认对方地址可直接访问。"),
        ("6 多路由器", "AP/桥接且同一网段时通常仍可自动发现；跨子网或双层 NAT 时自动发现通常无法穿过路由器，可尝试手动连接对方的局域网地址。"),
        ("7 下级网络", "下级路由器后的 Mac 可尝试主动连接上级局域网的设备；反方向可能被 NAT 阻挡。单位、访客网络或同网段隔离无法由应用绕过。"),
        ("8 刷新设备", "没有发现设备不代表权限被拒绝。切换 Wi‑Fi、插拔网线或更改 VPN 后应用会自动刷新，也可手动刷新；以当前显示的地址和端口为准。")
    ]

    private let troubleshooting: [(String, String)] = [
        ("连接未能建立", "下面是可检查的项目，不表示已检测到 VPN、TUN 或透明代理，也不能确认它们就是原因。"),
        ("代理或 VPN", "在相应软件中检查“绕过局域网 / 局域网直连”规则，确认对方真实局域网地址走直连；不同软件的设置名称可能不同。调整后可刷新设备并重新请求连接。"),
        ("对端状态", "确认对方正在运行飞鸽快传，并使用其“偏好设置 → 本机连接信息”中当前显示的地址和端口；允许应用通过防火墙，无需关闭防火墙。"),
        ("网络结构", "多层路由、双层 NAT、访客网络或设备隔离也可能阻挡连接。可尝试下级网络主动连接上级设备，但仍取决于实际路由和网络策略。"),
        ("应用边界", "应用不会自动更改代理、VPN、路由或防火墙设置。")
    ]

    private var items: [(String, String)] { mode == .usage ? usage : troubleshooting }

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            HStack {
                Text(mode == .usage ? "使用帮助" : "连接排查").font(.title2.bold())
                Spacer()
                Button("完成") { dismiss() }.keyboardShortcut(.cancelAction)
            }
            ScrollView {
                VStack(alignment: .leading, spacing: 16) {
                    ForEach(items, id: \.0) { title, detail in
                        VStack(alignment: .leading, spacing: 5) {
                            Text(title).font(.headline)
                            Text(detail).font(.body).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                        }.frame(maxWidth: .infinity, alignment: .leading)
                    }
                }.padding(.vertical, 4)
            }
            if mode == .usage {
                HStack {
                    Text("系统防火墙的设置位置因 macOS 版本而异：系统设置 → 网络 → 防火墙。")
                        .font(.caption).foregroundStyle(.secondary)
                    Spacer()
                    Button("打开防火墙设置") { openFirewallSettings() }
                }
            }
        }.padding(22).frame(width: 600, height: 520)
    }

    private func openFirewallSettings() {
        guard let url = URL(string: "x-apple.systempreferences:com.apple.preference.security?Firewall") else { return }
        NSWorkspace.shared.open(url)
    }
}
