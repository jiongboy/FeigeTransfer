import AppKit
import ImageIO
import SwiftUI

struct ChatMessageView: View {
    let message: ChatMessage
    let canRetry: Bool
    var retry: () -> Void
    private var outgoing: Bool { message.direction == .sent }
    private let accent = Color.accentColor
    @State private var previewURL: URL?
    @State private var isPreviewPresented = false

    var body: some View {
        HStack(alignment: .top, spacing: 0) {
            if outgoing { Spacer(minLength: 80) }
            VStack(alignment: outgoing ? .trailing : .leading, spacing: 6) {
                Text("\(outgoing ? "我" : message.peerName) · \(message.date.formatted(date: .omitted, time: .shortened))")
                    .font(.caption2).foregroundStyle(.secondary)
                if message.kind == .text { textBubble }
                else { attachment }
                HStack(spacing: 8) {
                    if message.status == .failed {
                        VStack(alignment: outgoing ? .trailing : .leading, spacing: 3) {
                            Label(outgoing ? "未发送完成" : "接收未完成", systemImage: "exclamationmark.circle").foregroundStyle(.red)
                            if let reason = message.failureReason, !reason.isEmpty {
                                Text(reason).font(.caption2).foregroundStyle(.secondary).textSelection(.enabled)
                            }
                        }
                        if outgoing { Button("重试", action: retry).buttonStyle(.borderless).disabled(!canRetry) }
                    } else {
                        Text(statusLabel).foregroundStyle(.secondary)
                    }
                }.font(.caption2)
            }.frame(maxWidth: 420, alignment: outgoing ? .trailing : .leading)
            if !outgoing { Spacer(minLength: 80) }
        }
        .frame(maxWidth: .infinity, alignment: outgoing ? .trailing : .leading)
        .sheet(isPresented: $isPreviewPresented) {
            if let previewURL { AttachmentPreviewView(url: previewURL) }
        }
    }

    private var textBubble: some View {
        Text(message.text ?? "")
            .textSelection(.enabled)
            .font(.system(size: 14))
            .padding(.horizontal, 15).padding(.vertical, 11)
            .background(outgoing ? accent.opacity(0.13) : Color(nsColor: .controlBackgroundColor), in: RoundedRectangle(cornerRadius: 12))
            .contextMenu {
                Button("复制文字") {
                    NSPasteboard.general.clearContents()
                    NSPasteboard.general.setString(message.text ?? "", forType: .string)
                }
            }
    }

    private var attachment: some View {
        VStack(alignment: .leading, spacing: 12) {
            if message.kind == .image, let path = message.localPath,
               message.status == .delivered || message.status == .received {
                Button { NSWorkspace.shared.open(URL(fileURLWithPath: path)) } label: {
                    ChatImageThumbnail(path: path)
                }.buttonStyle(.plain).help("打开原图")
            }
            HStack(spacing: 12) {
                Image(systemName: message.kind == .image ? "photo" : "doc.fill")
                    .font(.system(size: 22)).foregroundStyle(accent)
                    .frame(width: 40, height: 44)
                    .background(accent.opacity(0.07), in: RoundedRectangle(cornerRadius: 8))
                VStack(alignment: .leading, spacing: 4) {
                    Text(message.fileName ?? "文件").font(.system(size: 13, weight: .medium)).lineLimit(2)
                    if let size = message.size {
                        Text(ByteCountFormatter.string(fromByteCount: size, countStyle: .file))
                            .font(.caption2).foregroundStyle(.secondary)
                    }
                }
                Spacer(minLength: 0)
            }
            if message.isPending {
                ProgressView(value: message.progress)
                Text(message.status == .queued ? "等待发送" : "\(outgoing ? "发送中" : "接收中") · \(Int(message.progress * 100))%")
                    .font(.caption2).foregroundStyle(.secondary)
            } else if let path = message.localPath, message.status != .failed {
                Divider()
                HStack {
                    Button("预览") { previewURL = URL(fileURLWithPath: path); isPreviewPresented = true }
                    Button(message.kind == .image ? "打开原图" : "打开文件") { NSWorkspace.shared.open(URL(fileURLWithPath: path)) }
                    Spacer()
                    Button("在访达中显示") { NSWorkspace.shared.activateFileViewerSelecting([URL(fileURLWithPath: path)]) }
                }.font(.caption).buttonStyle(.borderless)
            }
        }
        .padding(14).frame(width: 310, alignment: .leading)
        .background(outgoing ? accent.opacity(0.10) : Color(nsColor: .controlBackgroundColor), in: RoundedRectangle(cornerRadius: 12))
    }

    private var statusLabel: String {
        switch message.status {
        case .queued: "等待发送"
        case .sending: message.direction == .sent ? "发送中…" : "接收中…"
        case .delivered: "已送达"
        case .received: "已接收"
        case .failed: "未完成"
        }
    }
}

private struct ChatImageThumbnail: View {
    let path: String
    @State private var thumbnail: NSImage?
    @State private var unavailable = false

    var body: some View {
        Group {
            if let thumbnail {
                Image(nsImage: thumbnail).resizable().scaledToFit()
                    .frame(maxWidth: 282, maxHeight: 220)
            } else {
                Label(unavailable ? "无法预览，点击打开原图" : "正在加载图片…", systemImage: "photo")
                    .font(.caption).foregroundStyle(.secondary).frame(width: 282, height: 90)
            }
        }
        .clipShape(RoundedRectangle(cornerRadius: 7))
        .accessibilityLabel("图片：\(URL(fileURLWithPath: path).lastPathComponent)")
        .task(id: path) {
            let url = URL(fileURLWithPath: path)
            let result = await Task.detached(priority: .utility) {
                guard let source = CGImageSourceCreateWithURL(url as CFURL, [kCGImageSourceShouldCache: false] as CFDictionary),
                      let image = CGImageSourceCreateThumbnailAtIndex(source, 0, [
                        kCGImageSourceCreateThumbnailFromImageAlways: true,
                        kCGImageSourceCreateThumbnailWithTransform: true,
                        kCGImageSourceThumbnailMaxPixelSize: 640
                      ] as CFDictionary) else { return ThumbnailResult(image: nil) }
                return ThumbnailResult(image: NSImage(cgImage: image, size: .zero))
            }.value
            guard !Task.isCancelled else { return }
            thumbnail = result.image
            unavailable = result.image == nil
        }
    }
}

private struct ThumbnailResult: @unchecked Sendable { let image: NSImage? }
