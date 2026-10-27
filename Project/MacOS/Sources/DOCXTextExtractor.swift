import Foundation
import zlib

/// Minimal bounded reader for the uncompressed OOXML document part inside a DOCX ZIP.
/// It deliberately extracts text only: no fields, macros, relationships, or external content are run.
enum DOCXTextExtractor {
    static let maximumArchiveBytes: UInt64 = 50 * 1024 * 1024
    static let maximumDocumentBytes: UInt32 = 2 * 1024 * 1024

    enum ExtractionError: LocalizedError {
        case archiveTooLarge, invalidArchive, documentTooLarge, unsupportedCompression, invalidDocument

        var errorDescription: String? {
            switch self {
            case .archiveTooLarge: "DOCX 文件超过 50 MiB 预览限制。"
            case .invalidArchive: "DOCX 文件结构无效或不受支持。"
            case .documentTooLarge: "DOCX 正文超过 2 MiB 预览限制。"
            case .unsupportedCompression: "DOCX 正文使用了不支持的压缩方式。"
            case .invalidDocument: "无法解析 DOCX 正文。"
            }
        }
    }

    static func extract(from url: URL) throws -> String {
        let attrs = try FileManager.default.attributesOfItem(atPath: url.path)
        guard let fileSize = (attrs[.size] as? NSNumber)?.uint64Value, fileSize <= maximumArchiveBytes else {
            throw ExtractionError.archiveTooLarge
        }
        let archive = try Data(contentsOf: url, options: .mappedIfSafe)
        guard let entry = try documentEntry(in: archive) else { throw ExtractionError.invalidArchive }
        let xml: Data
        switch entry.method {
        case 0:
            guard entry.compressedSize == entry.uncompressedSize else { throw ExtractionError.invalidArchive }
            xml = entry.bytes
        case 8:
            xml = try inflateRaw(entry.bytes, expectedSize: entry.uncompressedSize)
        default:
            throw ExtractionError.unsupportedCompression
        }
        guard xml.count == Int(entry.uncompressedSize), crc32Value(xml) == entry.checksum else {
            throw ExtractionError.invalidArchive
        }
        let parser = DOCXXMLTextParser()
        parser.parse(xml)
        guard !parser.failed else { throw ExtractionError.invalidDocument }
        return parser.result
    }

    private struct Entry {
        let method: UInt16
        let checksum: UInt32
        let compressedSize: UInt32
        let uncompressedSize: UInt32
        let bytes: Data
    }

    private static func documentEntry(in data: Data) throws -> Entry? {
        let signature: UInt32 = 0x06054b50
        guard data.count >= 22 else { throw ExtractionError.invalidArchive }
        let searchStart = max(0, data.count - 65_557)
        guard let eocd = (searchStart...(data.count - 22)).reversed().first(where: { readUInt32(data, $0) == signature }) else {
            throw ExtractionError.invalidArchive
        }
        let diskNumber = readUInt16(data, eocd + 4)
        let centralDisk = readUInt16(data, eocd + 6)
        let diskEntries = readUInt16(data, eocd + 8)
        let totalEntries = readUInt16(data, eocd + 10)
        let centralSize = readUInt32(data, eocd + 12)
        let centralOffset = readUInt32(data, eocd + 16)
        let commentLength = Int(readUInt16(data, eocd + 20))
        guard diskNumber == 0, centralDisk == 0, diskEntries == totalEntries,
              UInt64(eocd) + 22 + UInt64(commentLength) == UInt64(data.count),
              centralOffset != UInt32.max, centralSize != UInt32.max,
              UInt64(centralOffset) + UInt64(centralSize) == UInt64(eocd) else {
            throw ExtractionError.invalidArchive
        }

        var cursor = Int(centralOffset)
        let centralEnd = cursor + Int(centralSize)
        for _ in 0..<totalEntries {
            guard cursor + 46 <= centralEnd, readUInt32(data, cursor) == 0x02014b50 else { throw ExtractionError.invalidArchive }
            let flags = readUInt16(data, cursor + 8)
            let method = readUInt16(data, cursor + 10)
            let checksum = readUInt32(data, cursor + 16)
            let compressed = readUInt32(data, cursor + 20)
            let uncompressed = readUInt32(data, cursor + 24)
            let nameLength = Int(readUInt16(data, cursor + 28))
            let extraLength = Int(readUInt16(data, cursor + 30))
            let comment = Int(readUInt16(data, cursor + 32))
            let localOffset = readUInt32(data, cursor + 42)
            let next = cursor + 46 + nameLength + extraLength + comment
            guard next <= centralEnd, localOffset != UInt32.max else { throw ExtractionError.invalidArchive }
            guard cursor + 46 + nameLength <= next else { throw ExtractionError.invalidArchive }
            let nameBytes = data.subdata(in: (cursor + 46)..<(cursor + 46 + nameLength))
            let name = String(data: nameBytes, encoding: (flags & 0x0800) != 0 ? .utf8 : .ascii)
            if name == "word/document.xml" {
                guard (flags & 0x0001) == 0, compressed != UInt32.max, uncompressed != UInt32.max else {
                    throw ExtractionError.invalidArchive
                }
                guard uncompressed <= maximumDocumentBytes else { throw ExtractionError.documentTooLarge }
                guard localOffset <= centralOffset else { throw ExtractionError.invalidArchive }
                let local = Int(localOffset)
                guard local <= data.count - 30, local < Int(centralOffset), readUInt32(data, local) == 0x04034b50 else { throw ExtractionError.invalidArchive }
                let localFlags = readUInt16(data, local + 6)
                let localMethod = readUInt16(data, local + 8)
                let localNameLength = Int(readUInt16(data, local + 26))
                let localExtraLength = Int(readUInt16(data, local + 28))
                let localNameEnd = local + 30 + localNameLength
                guard localNameEnd <= data.count, localNameEnd <= Int(centralOffset),
                      localExtraLength <= data.count - localNameEnd,
                      localExtraLength <= Int(centralOffset) - localNameEnd else { throw ExtractionError.invalidArchive }
                let dataStart = localNameEnd + localExtraLength
                guard dataStart <= Int(centralOffset), Int(compressed) <= data.count - dataStart,
                      localFlags == flags, localMethod == method else { throw ExtractionError.invalidArchive }
                let end = dataStart + Int(compressed)
                let localName = data.subdata(in: (local + 30)..<localNameEnd)
                guard localName == nameBytes else { throw ExtractionError.invalidArchive }
                return Entry(method: method, checksum: checksum, compressedSize: compressed,
                             uncompressedSize: uncompressed, bytes: data.subdata(in: dataStart..<end))
            }
            cursor = next
        }
        return nil
    }

    private static func inflateRaw(_ input: Data, expectedSize: UInt32) throws -> Data {
        var stream = z_stream()
        let initStatus = inflateInit2_(&stream, -15, ZLIB_VERSION, Int32(MemoryLayout<z_stream>.size))
        guard initStatus == Z_OK else { throw ExtractionError.invalidDocument }
        defer { inflateEnd(&stream) }

        var output = Data()
        let chunkSize = 64 * 1024
        let status = input.withUnsafeBytes { source -> Int32 in
            stream.next_in = UnsafeMutablePointer<Bytef>(mutating: source.bindMemory(to: Bytef.self).baseAddress)
            stream.avail_in = uInt(input.count)
            var status: Int32 = Z_OK
            repeat {
                var chunk = [UInt8](repeating: 0, count: chunkSize)
                let produced = chunk.withUnsafeMutableBytes { destination -> Int in
                    stream.next_out = destination.bindMemory(to: Bytef.self).baseAddress
                    stream.avail_out = uInt(chunkSize)
                    status = inflate(&stream, Z_NO_FLUSH)
                    return chunkSize - Int(stream.avail_out)
                }
                guard status == Z_OK || status == Z_STREAM_END else { return status }
                guard produced <= Int(maximumDocumentBytes) - output.count else { return Z_MEM_ERROR }
                output.append(contentsOf: chunk.prefix(produced))
                if status == Z_STREAM_END { break }
                if produced == 0 && stream.avail_in == 0 { return Z_DATA_ERROR }
            } while true
            return status
        }
        guard status == Z_STREAM_END, output.count == Int(expectedSize), stream.total_out == expectedSize,
              stream.total_in == input.count else {
            throw ExtractionError.invalidDocument
        }
        return output
    }

    private static func crc32Value(_ data: Data) -> UInt32 {
        data.withUnsafeBytes { bytes in
            UInt32(crc32(0, bytes.bindMemory(to: Bytef.self).baseAddress, uInt(data.count)))
        }
    }

    private static func readUInt16(_ data: Data, _ offset: Int) -> UInt16 {
        UInt16(data[offset]) | (UInt16(data[offset + 1]) << 8)
    }

    private static func readUInt32(_ data: Data, _ offset: Int) -> UInt32 {
        UInt32(data[offset]) | (UInt32(data[offset + 1]) << 8) | (UInt32(data[offset + 2]) << 16) | (UInt32(data[offset + 3]) << 24)
    }
}

private final class DOCXXMLTextParser: NSObject, XMLParserDelegate {
    private(set) var failed = false
    private var paragraphs: [String] = []
    private var currentParagraph = ""
    private var activeTextDepth: Int?
    private var paragraphCount = 0
    private var totalCharacters = 0
    private let maximumParagraphs = 5000
    private let maximumCharacters = 2_000_000

    func parse(_ data: Data) {
        let xmlParser = XMLParser(data: data)
        xmlParser.delegate = self
        failed = !xmlParser.parse()
    }

    var result: String {
        paragraphs.joined(separator: "\n")
    }

    func parser(_ parser: XMLParser, didStartElement elementName: String, namespaceURI: String?, qualifiedName qName: String?, attributes attributeDict: [String: String] = [:]) {
        let localName = (qName ?? elementName).split(separator: ":").last.map(String.init) ?? elementName
        switch localName {
        case "p": currentParagraph = ""
        case "t": activeTextDepth = 1
        case "tab": currentParagraph.append("\t")
        case "br", "cr": currentParagraph.append("\n")
        default: break
        }
    }

    func parser(_ parser: XMLParser, foundCharacters string: String) {
        guard activeTextDepth != nil else { return }
        guard currentParagraph.utf16.count + string.utf16.count <= maximumCharacters else {
            failed = true
            parser.abortParsing()
            return
        }
        currentParagraph.append(string)
    }

    func parser(_ parser: XMLParser, didEndElement elementName: String, namespaceURI: String?, qualifiedName qName: String?) {
        let localName = (qName ?? elementName).split(separator: ":").last.map(String.init) ?? elementName
        if localName == "t" { activeTextDepth = nil }
        if localName == "p" {
            paragraphCount += 1
            guard paragraphCount <= maximumParagraphs,
                  totalCharacters + currentParagraph.utf16.count <= maximumCharacters else {
                failed = true
                parser.abortParsing()
                return
            }
            totalCharacters += currentParagraph.utf16.count
            paragraphs.append(currentParagraph)
            currentParagraph = ""
        }
    }

    func parser(_ parser: XMLParser, parseErrorOccurred parseError: Error) { failed = true }
}
