import AppKit
import ImageIO
import PDFKit
import SwiftUI
import UniformTypeIdentifiers

struct AttachmentPreviewView: View {
    let url: URL
    @Environment(\.dismiss) private var dismiss
    @State private var imageScale = 1.0
    @State private var previewImage: NSImage?
    @State private var textContent: String?
    @State private var docxContent: String?
    @State private var loadError: String?
    @State private var isLoading = true
    @State private var pdfTooLarge = false

    private var fileExtension: String { url.pathExtension.lowercased() }
    private var isImage: Bool { UTType(filenameExtension: fileExtension)?.conforms(to: .image) == true }
    private var isPDF: Bool { fileExtension == "pdf" }
    private var isDOCX: Bool { fileExtension == "docx" }
    private var isText: Bool {
        ["txt", "md", "markdown", "json", "xml", "yaml", "yml", "toml", "csv", "log", "swift", "cs", "c", "h", "cpp", "js", "ts", "py", "sh", "html", "css", "sql", "rs", "go", "java", "kt"].contains(fileExtension)
    }

    var body: some View {
        VStack(spacing: 0) {
            HStack {
                VStack(alignment: .leading, spacing: 3) {
                    Text(url.lastPathComponent).font(.headline).lineLimit(1)
                    Text(previewKind).font(.caption).foregroundStyle(.secondary)
                }
                Spacer()
                if isImage {
                    Button("−") { imageScale = max(0.25, imageScale - 0.25) }.help("缩小")
                    Button("适合窗口") { imageScale = 1 }
                    Button("+") { imageScale = min(4, imageScale + 0.25) }.help("放大")
                }
                Button("在访达中显示") { NSWorkspace.shared.activateFileViewerSelecting([url]) }
                Button("关闭") { dismiss() }.keyboardShortcut(.cancelAction)
            }.padding(14)
            Divider()
            Group {
                if isImage {
                    imagePreview
                } else if isPDF {
                    if pdfTooLarge {
                        previewUnavailable("PDF 文件超过 50 MiB 预览限制。")
                    } else {
                        PDFDocumentPreview(url: url)
                    }
                } else if isText {
                    textPreview
                } else if isDOCX {
                    docxPreview
                } else {
                    previewUnavailable("此格式暂不支持内置预览。可以在访达中打开原文件。文件不会上传或执行。")
                }
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity)
        }
        .frame(minWidth: 680, minHeight: 480)
        .task(id: url) {
            if isImage { await loadImage() }
            else if isText { await loadText() }
            else if isDOCX { await loadDOCX() }
            if isPDF { await checkPDFSize() }
        }
    }

    private var previewKind: String {
        if isImage { "图片预览" }
        else if isPDF { "PDF 文档" }
        else if isText { "文本 / 源码预览（只读）" }
        else if isDOCX { "Word 正文预览（只读）" }
        else { "系统打开" }
    }

    private var imagePreview: some View {
        Group {
            if let image = previewImage {
                ScrollView([.horizontal, .vertical]) {
                    Image(nsImage: image).resizable().scaledToFit().scaleEffect(imageScale)
                        .frame(maxWidth: .infinity, maxHeight: .infinity)
                        .padding(24)
                }
            } else {
                previewUnavailable("无法读取图片。原文件可能已移动或格式不受支持。")
            }
        }
        .background(Color(nsColor: .underPageBackgroundColor))
    }

    @ViewBuilder
    private var textPreview: some View {
        if isLoading {
            ProgressView("正在读取文件…")
        } else if let loadError {
            previewUnavailable(loadError)
        } else if let textContent {
            let lines = textContent.components(separatedBy: .newlines)
            ScrollView([.horizontal, .vertical]) {
                VStack(alignment: .leading, spacing: 0) {
                    ForEach(Array(lines.enumerated()), id: \.offset) { index, line in
                        HStack(alignment: .top, spacing: 16) {
                            Text("\(index + 1)").foregroundStyle(.tertiary).frame(width: 46, alignment: .trailing)
                            Text(line.isEmpty ? " " : line).textSelection(.enabled)
                        }
                        .font(.system(size: 12, design: .monospaced))
                        .frame(maxWidth: .infinity, alignment: .leading)
                    }
                }.padding(16)
            }.background(Color(nsColor: .textBackgroundColor))
        }
    }

    @ViewBuilder
    private var docxPreview: some View {
        if isLoading {
            ProgressView("正在读取 Word 正文…")
        } else if let loadError {
            previewUnavailable(loadError)
        } else if let docxContent {
            let lines = docxContent.components(separatedBy: .newlines)
            ScrollView([.horizontal, .vertical]) {
                VStack(alignment: .leading, spacing: 8) {
                    ForEach(Array(lines.enumerated()), id: \.offset) { _, line in
                        Text(line.isEmpty ? " " : line)
                            .font(.system(size: 14))
                            .textSelection(.enabled)
                            .frame(maxWidth: .infinity, alignment: .leading)
                    }
                }
                .padding(20)
            }
            .background(Color(nsColor: .textBackgroundColor))
        }
    }

    private func previewUnavailable(_ message: String) -> some View {
        VStack(spacing: 10) {
            Image(systemName: "doc.questionmark").font(.system(size: 30)).foregroundStyle(.secondary)
            Text(message).multilineTextAlignment(.center).foregroundStyle(.secondary)
        }.padding(24)
    }

    @MainActor
    private func loadImage() async {
        let result = await Task.detached(priority: .utility) {
            guard let source = CGImageSourceCreateWithURL(url as CFURL, [kCGImageSourceShouldCache: false] as CFDictionary),
                  let image = CGImageSourceCreateThumbnailAtIndex(source, 0, [
                    kCGImageSourceCreateThumbnailFromImageAlways: true,
                    kCGImageSourceCreateThumbnailWithTransform: true,
                    kCGImageSourceThumbnailMaxPixelSize: 4096
                  ] as CFDictionary) else { return PreviewImageResult(image: nil) }
            return PreviewImageResult(image: NSImage(cgImage: image, size: .zero))
        }.value
        previewImage = result.image
    }

    @MainActor
    private func loadText() async {
        isLoading = true
        defer { isLoading = false }
        let result = await Task.detached(priority: .utility) { Self.readText(url) }.value
        textContent = result.content
        loadError = result.error
    }

    @MainActor
    private func loadDOCX() async {
        isLoading = true
        defer { isLoading = false }
        do {
            docxContent = try await Task.detached(priority: .utility) { try DOCXTextExtractor.extract(from: url) }.value
            if docxContent?.isEmpty == true { loadError = "DOCX 文档正文为空。" }
        } catch {
            loadError = error.localizedDescription
        }
    }

    @MainActor
    private func checkPDFSize() async {
        let tooLarge = await Task.detached(priority: .utility) { Self.fileSize(url) > 50 * 1024 * 1024 }.value
        pdfTooLarge = tooLarge
    }

    nonisolated private static func fileSize(_ url: URL) -> Int {
        let attributes = try? FileManager.default.attributesOfItem(atPath: url.path)
        return (attributes?[.size] as? NSNumber)?.intValue ?? 0
    }

    nonisolated private static func readText(_ url: URL) -> (content: String?, error: String?) {
        do {
            guard fileSize(url) <= 2 * 1024 * 1024 else { throw PreviewError.tooLarge }
            let data = try Data(contentsOf: url, options: .mappedIfSafe)
            let decoded = decodeText(data)
            guard decoded.utf8.count <= 8 * 1024 * 1024 else { throw PreviewError.tooLarge }
            guard decoded.components(separatedBy: .newlines).count <= 5000 else { throw PreviewError.tooManyLines }
            return (decoded, nil)
        } catch { return (nil, error.localizedDescription) }
    }

    nonisolated private static func decodeText(_ data: Data) -> String {
        if data.starts(with: [0xFF, 0xFE]), let value = String(data: data.dropFirst(2), encoding: .utf16LittleEndian) { return value }
        if data.starts(with: [0xFE, 0xFF]), let value = String(data: data.dropFirst(2), encoding: .utf16BigEndian) { return value }
        if let value = String(data: data, encoding: .utf8) { return value }
        return String(decoding: data, as: UTF8.self)
    }

    nonisolated enum PreviewError: LocalizedError {
        case tooLarge, tooManyLines
        var errorDescription: String? {
            switch self {
            case .tooLarge: "文本文件超过 2 MiB 预览限制。"
            case .tooManyLines: "文件超过 5000 行预览限制。"
            }
        }
    }
}

private struct PreviewImageResult: @unchecked Sendable {
    let image: NSImage?
}

private struct PDFDocumentPreview: NSViewRepresentable {
    let url: URL
    func makeNSView(context: Context) -> PDFView {
        let view = PDFView()
        view.autoScales = true
        view.displayMode = .singlePageContinuous
        view.displayDirection = .vertical
        view.document = PDFDocument(url: url)
        return view
    }
    func updateNSView(_ view: PDFView, context: Context) {
        if view.document?.documentURL != url { view.document = PDFDocument(url: url) }
    }
}
