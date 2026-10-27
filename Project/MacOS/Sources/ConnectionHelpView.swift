import SwiftUI

struct ConnectionHelpView: View {
    @Environment(\.dismiss) private var dismiss

    private let items: [(String, String)] = [
        ("先确认同一局域网", "两台设备都要打开飞鸽快传。访客 Wi-Fi、网络隔离、不同子网或双层 NAT 可能阻止设备发现或连接。"),
        ("检查连接信息", "未发现设备时，可在对方 Mac 的侧栏查看当前局域网地址和端口，再使用“手动连接”。地址变化或切换 Wi-Fi 后请刷新设备列表。"),
        ("检查 VPN 与代理", "本应用使用局域网 TCP 和 Bonjour，不通过 HTTP 或 SOCKS 代理传输。VPN、TUN 或透明代理可能改变局域网路由；可检查代理软件是否允许局域网直连。单次连接失败不能确认代理就是原因。"),
        ("检查系统防火墙", "macOS 首次接收连接时可能询问是否允许网络访问。请根据你信任的网络环境决定是否允许，不需要关闭防火墙或修改系统路由。"),
        ("路由器之间的网络", "Bonjour 发现通常只在同一链路内工作。跨子网时可尝试手动连接，但仍需有可达路由、回程路径和防火墙许可；应用不能绕过网络隔离。")
    ]

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            HStack {
                Text("连接排查").font(.title2.bold())
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
        }.padding(22).frame(width: 560, height: 420)
    }
}
