import AppKit
import XCTest
@testable import FeigeMac

final class ChatInputTests: XCTestCase {
    @MainActor
    func testPasteImageRoutesImageWithoutChangingDraft() throws {
        _ = NSApplication.shared
        let editor = MessageTextView(frame: NSRect(x: 0, y: 0, width: 500, height: 100))
        editor.isRichText = false
        editor.string = "保留草稿"
        let bitmap = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: 2, pixelsHigh: 2,
                                      bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true,
                                      isPlanar: false, colorSpaceName: .deviceRGB,
                                      bytesPerRow: 0, bitsPerPixel: 0)!
        let image = NSImage(size: NSSize(width: 2, height: 2))
        image.addRepresentation(bitmap)
        var pastedImage: NSImage?
        editor.onPasteImage = { image, _ in pastedImage = image }
        let pasteboard = NSPasteboard(name: NSPasteboard.Name(UUID().uuidString))
        pasteboard.clearContents()
        XCTAssertTrue(pasteboard.writeObjects([image]))
        editor.paste(from: pasteboard)

        XCTAssertNotNil(pastedImage)
        XCTAssertEqual(editor.string, "保留草稿")
    }

    @MainActor
    func testPasteWithoutImageUsesNativeBehavior() {
        _ = NSApplication.shared
        let editor = MessageTextView(frame: NSRect(x: 0, y: 0, width: 500, height: 100))
        editor.isRichText = false
        editor.string = "原文"
        let pasteboard = NSPasteboard(name: NSPasteboard.Name(UUID().uuidString))
        pasteboard.clearContents()
        pasteboard.setString(" + 粘贴文本", forType: .string)
        editor.paste(from: pasteboard)

        XCTAssertEqual(editor.string, "原文 + 粘贴文本")
    }

    @MainActor
    func testEnterSendsAndShiftEnterKeepsMultilineDraft() throws {
        _ = NSApplication.shared
        let editor = MessageTextView(frame: NSRect(x: 0, y: 0, width: 500, height: 100))
        editor.isRichText = false
        editor.string = "你好"
        editor.setSelectedRange(NSRange(location: (editor.string as NSString).length, length: 0))
        var sends = 0
        editor.onSend = { sends += 1 }
        let enter = try XCTUnwrap(NSEvent.keyEvent(with: .keyDown, location: .zero, modifierFlags: [], timestamp: 0,
                                                  windowNumber: 0, context: nil, characters: "\r", charactersIgnoringModifiers: "\r", isARepeat: false, keyCode: 36))
        editor.keyDown(with: enter)
        XCTAssertEqual(sends, 1)
        XCTAssertEqual(editor.string, "你好")
        let shiftEnter = try XCTUnwrap(NSEvent.keyEvent(with: .keyDown, location: .zero, modifierFlags: [.shift], timestamp: 0,
                                                       windowNumber: 0, context: nil, characters: "\r", charactersIgnoringModifiers: "\r", isARepeat: false, keyCode: 36))
        editor.keyDown(with: shiftEnter)
        XCTAssertEqual(sends, 1)
        XCTAssertEqual(editor.string, "你好\n")
    }
}
