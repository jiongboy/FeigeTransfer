import SwiftUI

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
            TextField("对端局域网 IP 地址", text: $host).textFieldStyle(.roundedBorder)
            TextField("对端端口", text: $port).textFieldStyle(.roundedBorder)
            Text("对方同意连接后，即可选择文件发送。地址和端口可在对端的附近设备页面查看。")
                .font(.caption).foregroundStyle(.secondary)
            if let validationMessage { Text(validationMessage).font(.caption).foregroundStyle(.red) }
            HStack {
                Button("取消") { dismiss() }.keyboardShortcut(.cancelAction)
                Spacer()
                Button("请求连接") {
                    do { onConnect(try model.manualDevice(host: host, port: port)) }
                    catch { validationMessage = error.localizedDescription }
                }.disabled(host.isEmpty || port.isEmpty).keyboardShortcut(.defaultAction)
            }
        }.padding(24).frame(width: 440)
    }
}
