import AppKit
import SwiftUI

/// A native text editor keeps input methods, selection, copy and paste intact.
struct ChatComposer: NSViewRepresentable {
    @Binding var text: String
    var enabled: Bool
    var onSend: () -> Void
    var onPasteImage: (NSImage) -> Void = { _ in }

    func makeCoordinator() -> Coordinator { Coordinator(self) }

    func makeNSView(context: Context) -> NSScrollView {
        let scroll = NSScrollView()
        scroll.hasVerticalScroller = true
        scroll.drawsBackground = false
        let editor = MessageTextView()
        editor.isRichText = false
        editor.isAutomaticQuoteSubstitutionEnabled = false
        editor.isAutomaticDashSubstitutionEnabled = false
        editor.isVerticallyResizable = true
        editor.isHorizontallyResizable = false
        editor.autoresizingMask = [.width]
        editor.textContainer?.widthTracksTextView = true
        editor.textContainerInset = NSSize(width: 5, height: 8)
        editor.font = .systemFont(ofSize: 14)
        editor.drawsBackground = false
        editor.setAccessibilityLabel("聊天消息，Enter 发送，Shift 加 Enter 换行")
        editor.delegate = context.coordinator
        editor.onPasteImage = { image, pasteboard in
            guard context.coordinator.parent.enabled else { return }
            context.coordinator.parent.onPasteImage(image)
        }
        scroll.documentView = editor
        return scroll
    }

    func updateNSView(_ scroll: NSScrollView, context: Context) {
        guard let editor = scroll.documentView as? MessageTextView else { return }
        context.coordinator.parent = self
        if editor.string != text { editor.string = text }
        editor.isEditable = enabled
        editor.textColor = .labelColor
        editor.onSend = onSend
        editor.onPasteImage = { image, pasteboard in
            guard context.coordinator.parent.enabled else { return }
            context.coordinator.parent.onPasteImage(image)
        }
    }

    final class Coordinator: NSObject, NSTextViewDelegate {
        var parent: ChatComposer
        init(_ parent: ChatComposer) { self.parent = parent }
        func textDidChange(_ notification: Notification) {
            guard let editor = notification.object as? NSTextView else { return }
            parent.text = editor.string
        }
    }
}

final class MessageTextView: NSTextView {
    var onSend: (() -> Void)?
    var onPasteImage: ((NSImage, NSPasteboard) -> Void)?

    override func paste(_ sender: Any?) {
        paste(from: NSPasteboard.general)
    }

    func paste(from pasteboard: NSPasteboard) {
        guard isEditable,
              let image = pasteboard.readObjects(forClasses: [NSImage.self])?.first as? NSImage else {
            if pasteboard === NSPasteboard.general { super.paste(nil) }
            else if let value = pasteboard.string(forType: .string) { insertText(value, replacementRange: selectedRange()) }
            return
        }
        onPasteImage?(image, pasteboard)
    }

    override func keyDown(with event: NSEvent) {
        if (event.keyCode == 36 || event.keyCode == 76),
           !event.modifierFlags.contains(.shift), !hasMarkedText(), isEditable {
            onSend?()
        } else { super.keyDown(with: event) }
    }
}
