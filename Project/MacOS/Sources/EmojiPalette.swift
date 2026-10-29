import AppKit
import SwiftUI

/// A compact emoji palette in the composer toolbar.
///
/// The system picker (⌃⌘Space) requires "Show emoji and symbols in the menu bar" to be
/// switched on; this palette always works and inserts at the current selection.
enum EmojiPalette {
    static let groups: [(String, [String])] = [
        ("常用", ["😀", "😃", "😄", "😁", "😆", "😅", "😂", "🙂", "🙃", "😉", "😊", "😍", "🥰", "😘", "😋", "😎", "🤔", "😴", "😭", "😡"]),
        ("手势", ["👋", "👍", "👎", "👌", "✌️", "🤞", "🤟", "🤘", "👏", "🙌", "🫶", "💪", "🙏", "🤝"]),
        ("爱心", ["❤️", "🧡", "💛", "💚", "💙", "💜", "🖤", "🤍", "💔", "💕", "💖", "💗", "💞", "💘"]),
        ("庆祝", ["🎉", "🎊", "🎂", "🎁", "🥳", "🏆", "✨", "🌟", "💯", "🔥", "🍾", "🎈"]),
        ("动物", ["🐱", "🐶", "🐼", "🐸", "🐵", "🦊", "🐻", "🐰", "🐯", "🐨", "🦄", "🐣", "🐧", "🦋"]),
        ("日常", ["☀️", "🌙", "⭐", "⚡", "☕", "🍵", "🍰", "🍕", "🍉", "🌹", "🌻", "🌈", "🚀", "🔔"])
    ]

    /// Inserts the emoji at the end of the draft (the composer keeps the caret during input).
    static func append(_ emoji: String, to text: String) -> String { text + emoji }
}

struct EmojiPaletteView: View {
    var onPick: (String) -> Void

    var body: some View {
        ScrollView {
            LazyVStack(alignment: .leading, spacing: 10) {
                ForEach(EmojiPalette.groups, id: \.0) { title, emojis in
                    Text(title).font(.caption2).foregroundStyle(.secondary)
                    LazyVGrid(columns: Array(repeating: GridItem(.fixed(32), spacing: 2), count: 6), spacing: 2) {
                        ForEach(emojis, id: \.self) { emoji in
                            Button { onPick(emoji) } label: {
                                Text(emoji).font(.system(size: 20)).frame(width: 32, height: 32)
                            }
                            .buttonStyle(.plain)
                            .help("插入 \(emoji)")
                        }
                    }
                }
            }.padding(10)
        }
        .frame(width: 232, height: 320)
    }
}
