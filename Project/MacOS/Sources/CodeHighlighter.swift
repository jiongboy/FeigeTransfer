import Foundation

/// Minimal syntax highlighting shared, rule for rule, with the Windows client so both previews
/// colour the same tokens. This is a tokenizer for display only: it never executes or parses the
/// source beyond these four groups, and unparseable input simply renders as plain text.
enum CodeHighlighter {
    /// `plain` is text between matches and must keep the normal foreground colour.
    enum TokenKind { case plain, comment, string, number, keyword }

    struct Token: Equatable {
        let kind: TokenKind
        let text: String
    }

    /// Same cap as Windows: above it the text is shown unhighlighted to keep scrolling responsive.
    static let maximumCharacters = 20_000

    private static let pattern = #"""
    (?<comment>//[^\n]*|/\*[\s\S]*?\*/|#[^\n]*|<!--[\s\S]*?-->)|(?<text>"(?:\\.|[^"\\\r\n])*"|'(?:\\.|[^'\\\r\n])*'|`(?:\\.|[^`\\])*`)|(?<number>\b\d+(?:\.\d+)?\b)|(?<keyword>\b(?:using|namespace|public|private|protected|internal|static|class|struct|record|interface|enum|void|var|let|const|func|function|def|return|if|else|for|foreach|while|switch|case|break|continue|new|async|await|try|catch|finally|throw|import|from|export|default|true|false|null|None|True|False|self|this|in|is|as|int|string|bool|double|extends|implements)\b)
    """#

    private static let regex: NSRegularExpression? = {
        try? NSRegularExpression(pattern: pattern, options: [.anchorsMatchLines])
    }()

    /// Splits one line into tokens. Text between matches is returned as plain segments so callers can
    /// concatenate them without losing characters.
    static func tokens(in line: String) -> [Token] {
        guard !line.isEmpty, let regex else { return line.isEmpty ? [] : [Token(kind: .plain, text: line)] }
        let ns = line as NSString
        let matches = regex.matches(in: line, options: [], range: NSRange(location: 0, length: ns.length))
        guard !matches.isEmpty else { return [Token(kind: .plain, text: line)] }

        var result: [Token] = []
        var offset = 0
        for match in matches {
            if match.range.location > offset {
                result.append(Token(kind: .plain, text: ns.substring(with: NSRange(location: offset, length: match.range.location - offset))))
            }
            let text = ns.substring(with: match.range)
            let kind: TokenKind
            if range("comment", match, ns) != nil { kind = .comment }
            else if range("text", match, ns) != nil { kind = .string }
            else if range("number", match, ns) != nil { kind = .number }
            else { kind = .keyword }
            result.append(Token(kind: kind, text: text))
            offset = match.range.location + match.range.length
        }
        if offset < ns.length {
            result.append(Token(kind: .plain, text: ns.substring(from: offset)))
        }
        return result
    }

    private static func range(_ name: String, _ match: NSTextCheckingResult, _ source: NSString) -> NSRange? {
        let value = match.range(withName: name)
        return value.location == NSNotFound ? nil : value
    }
}
