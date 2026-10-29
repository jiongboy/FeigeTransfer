import SwiftUI

/// Manual connection form. Accepts a private/local address and an explicit port, mirroring the
/// Windows manual-connect dialog; anything else is rejected before a socket is opened.
struct SendView: View {
    @EnvironmentObject private var model: TransferModel
    @Environment(\.dismiss) private var dismiss
    let onConnect: (LANDevice) -> Void
    @State private var host = ""
    @State private var port = ""
    @State private var validationMessage: String?

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            Text("手动连接设备").font(.title2.bold())
            VStack(alignment: .leading, spacing: 6) {
                Text("局域网 IP 地址").font(.system(size: 12, weight: .semibold))
                TextField("192.168.1.20 或 fd00::1", text: $host).textFieldStyle(.roundedBorder)
                    .accessibilityLabel("局域网 IP 地址")
            }
            VStack(alignment: .leading, spacing: 6) {
                Text("端口").font(.system(size: 12, weight: .semibold))
                TextField("对方连接信息中显示的端口", text: $port).textFieldStyle(.roundedBorder)
                    .accessibilityLabel("端口")
            }
            Text("对方同意连接后，即可在同一个会话里发送文字、图片和文件。地址和端口可在对方“偏好设置 → 本机连接信息”中查看。")
                .font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            if let validationMessage {
                Text(validationMessage).font(.caption).foregroundStyle(.red).fixedSize(horizontal: false, vertical: true)
            }
            HStack {
                Button("取消") { dismiss() }.keyboardShortcut(.cancelAction)
                Spacer()
                Button("请求连接") {
                    do { onConnect(try model.manualDevice(host: host, port: port)) }
                    catch { validationMessage = error.localizedDescription }
                }
                .disabled(host.trimmingCharacters(in: .whitespaces).isEmpty || port.trimmingCharacters(in: .whitespaces).isEmpty)
                .keyboardShortcut(.defaultAction)
            }
        }.padding(24).frame(width: 460)
    }
}
