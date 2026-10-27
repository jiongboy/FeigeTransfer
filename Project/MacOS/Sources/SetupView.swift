import SwiftUI

struct SetupView: View {
    @EnvironmentObject private var model: TransferModel
    @Environment(\.dismiss) private var dismiss
    let isFirstRun: Bool
    @State private var deviceName = ""
    @State private var saveError: String?
    @State private var firstRunStep = 0
    @FocusState private var isDeviceNameFocused: Bool
    @State private var receiveDirectory = URL(fileURLWithPath: NSHomeDirectory()).appendingPathComponent("Downloads/Feige Received")

    var body: some View {
        VStack(alignment: .leading, spacing: 18) {
            HStack(spacing: 12) {
                Image(systemName: "paperplane.fill")
                    .font(.system(size: 19, weight: .bold))
                    .foregroundStyle(.white)
                    .frame(width: 44, height: 44)
                    .background(LinearGradient(colors: [.indigo, .cyan], startPoint: .topLeading, endPoint: .bottomTrailing), in: RoundedRectangle(cornerRadius: 13))
                VStack(alignment: .leading, spacing: 3) {
                    Text(isFirstRun ? (firstRunStep == 0 ? "欢迎使用飞鸽快传" : "准备安全连接") : "偏好设置").font(.system(size: 20, weight: .bold))
                    Text(isFirstRun ? (firstRunStep == 0 ? "先设置本机名称和文件接收位置。" : "确认身份状态后即可开始连接附近设备。") : "修改本机设备信息和文件接收位置。")
                        .font(.system(size: 12)).foregroundStyle(.secondary)
                }
            }
            Divider()
            if isFirstRun {
                HStack(spacing: 8) {
                    Label("1 本机设置", systemImage: firstRunStep == 0 ? "1.circle.fill" : "checkmark.circle.fill")
                    Image(systemName: "chevron.right").font(.caption2).foregroundStyle(.tertiary)
                    Label("2 准备连接", systemImage: firstRunStep == 1 ? "2.circle.fill" : "2.circle")
                }.font(.caption).foregroundStyle(Color.accentColor)
            }
            if !isFirstRun || firstRunStep == 0 {
            VStack(alignment: .leading, spacing: 7) {
                Text("设备名称").font(.system(size: 12, weight: .semibold))
                TextField("例如：书房 Mac", text: $deviceName)
                    .textFieldStyle(.roundedBorder)
                    .frame(maxWidth: .infinity, minHeight: 30)
                    .focused($isDeviceNameFocused)
                    .accessibilityLabel("设备名称")
                Text("此名称用于让局域网中的其他设备识别本机（1–48 个字符）。").font(.system(size: 11)).foregroundStyle(.secondary)
            }
            VStack(alignment: .leading, spacing: 7) {
                Text("接收文件夹").font(.system(size: 12, weight: .semibold))
                HStack {
                    Image(systemName: "folder").foregroundStyle(.secondary)
                    Text(receiveDirectory.path).font(.system(size: 11, design: .monospaced)).lineLimit(1).truncationMode(.middle)
                    Spacer()
                    Button("选择…") {
                        if let selected = model.chooseReceiveFolder() { receiveDirectory = selected }
                    }
                }
                .padding(10).background(Color.primary.opacity(0.035), in: RoundedRectangle(cornerRadius: 8))
                Text("收到的文件将保存到此文件夹。接收时不会覆盖同名文件。").font(.system(size: 11)).foregroundStyle(.secondary)
            }
            }
            if !isFirstRun || firstRunStep == 1 {
            VStack(alignment: .leading, spacing: 7) {
                Text("本机安全身份").font(.system(size: 12, weight: .semibold))
                Text(model.identityFingerprint)
                    .font(.system(size: 10, design: .monospaced))
                    .textSelection(.enabled)
                    .padding(10).frame(maxWidth: .infinity, alignment: .leading)
                    .background(Color.primary.opacity(0.035), in: RoundedRectangle(cornerRadius: 8))
                Text("设备身份由应用自动管理，连接期间使用加密传输，无需输入验证短码。")
                    .font(.system(size: 11)).foregroundStyle(.secondary)
            }
            if isFirstRun {
                HStack(spacing: 8) {
                    if model.isPreparingIdentity { ProgressView().controlSize(.small) }
                    else { Image(systemName: model.isIdentityReady ? "checkmark.circle.fill" : "exclamationmark.circle.fill") }
                    Text(model.isPreparingIdentity ? "正在验证本机身份…" : model.isIdentityReady ? "本机身份已准备就绪" : "身份尚未准备，请检查系统授权后重试。")
                        .font(.system(size: 12)).foregroundStyle(model.isIdentityReady ? Color.secondary : Color.orange)
                    Spacer()
                    if !model.isPreparingIdentity && !model.isIdentityReady {
                        Button("重试") { Task { await model.prepareIdentityForSetup() } }.buttonStyle(.borderless)
                    }
                }
                if let error = model.identityPreparationError {
                    Text(error).font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                }
            }
            }
            if !isFirstRun && !model.hiddenConversations.isEmpty {
                VStack(alignment: .leading, spacing: 8) {
                    HStack {
                        Text("隐藏的会话").font(.system(size: 12, weight: .semibold))
                        Spacer()
                        Button("全部恢复") { model.restoreAllConversations() }
                            .buttonStyle(.borderless).font(.system(size: 11))
                    }
                    ScrollView {
                        VStack(spacing: 6) {
                            ForEach(model.hiddenConversations) { peer in
                                HStack {
                                    VStack(alignment: .leading, spacing: 2) {
                                        Text(peer.name).font(.system(size: 12, weight: .medium)).lineLimit(1)
                                        Text(peer.preview).font(.caption2).foregroundStyle(.secondary).lineLimit(1)
                                    }
                                    Spacer()
                                    Button("恢复") { model.restoreConversation(peer.id) }
                                        .buttonStyle(.borderless).font(.system(size: 11))
                                }
                            }
                        }
                    }.frame(maxHeight: 140)
                }
            }
            Divider()
            HStack {
                Spacer()
                if !isFirstRun { Button("取消") { dismiss() }.keyboardShortcut(.cancelAction) }
                if isFirstRun {
                    Button("稍后设置") { model.deferFirstRunSetup(); dismiss() }
                    if firstRunStep == 1 {
                        Button("上一步") {
                            do {
                                try model.saveSetupDraft(deviceName: deviceName, receiveDirectory: receiveDirectory, nextStep: 0)
                                firstRunStep = 0
                            } catch { saveError = error.localizedDescription }
                        }
                    }
                }
                Button(primaryTitle, action: handlePrimaryAction)
                    .disabled(deviceName.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty ||
                              (isFirstRun && firstRunStep == 1 && !model.isIdentityReady))
            }
        }
        .padding(24)
        .frame(width: 500)
        .onAppear {
            deviceName = model.settings.deviceName
            receiveDirectory = model.settings.receiveDirectory
            firstRunStep = model.settings.setupStep ?? 0
            isDeviceNameFocused = true
        }
        .alert("无法保存设置", isPresented: Binding(get: { saveError != nil }, set: { if !$0 { saveError = nil } })) {
            Button("好", role: .cancel) { saveError = nil }
        } message: { Text(saveError ?? "") }
    }

    private var primaryTitle: String {
        if !isFirstRun { return "保存" }
        return firstRunStep == 0 ? "继续" : "完成设置并开始"
    }

    private func handlePrimaryAction() {
        do {
            if isFirstRun && firstRunStep == 0 {
                try model.saveSetupDraft(deviceName: deviceName, receiveDirectory: receiveDirectory, nextStep: 1)
                firstRunStep = 1
                Task { await model.prepareIdentityForSetup() }
            } else {
                try model.saveSettings(deviceName: deviceName, receiveDirectory: receiveDirectory)
                Task { await model.start() }
                dismiss()
            }
        } catch {
            saveError = error.localizedDescription
        }
    }
}
