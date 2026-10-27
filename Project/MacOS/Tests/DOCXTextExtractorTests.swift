import XCTest
@testable import FeigeMac
import zlib

final class DOCXTextExtractorTests: XCTestCase {
    func testExtractsParagraphsAndIgnoresNonTextXML() throws {
        let xml = Data("""
        <?xml version="1.0" encoding="UTF-8"?>
        <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
          <w:body><w:p><w:r><w:t>Hello</w:t></w:r><w:r><w:tab/></w:r><w:r><w:t>Feige</w:t></w:r></w:p>
          <w:p><w:r><w:t>第二段</w:t></w:r><w:r><w:br/><w:t>换行</w:t></w:r></w:p></w:body>
        </w:document>
        """.utf8)
        let url = try makeDOCX(documentXML: xml, method: 8)
        XCTAssertEqual(try DOCXTextExtractor.extract(from: url), "Hello\tFeige\n第二段\n换行")
    }

    func testRejectsDeflateStreamThatExpandsBeyondDeclaredSizeLimit() throws {
        let oversizedBody = Data(repeating: 0x41, count: Int(DOCXTextExtractor.maximumDocumentBytes) + 1)
        let url = try makeDOCX(documentXML: oversizedBody, method: 8, declaredSize: DOCXTextExtractor.maximumDocumentBytes)
        XCTAssertThrowsError(try DOCXTextExtractor.extract(from: url))
    }

    func testRejectsMalformedZIP() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let malformed = root.appendingPathComponent("bad.docx")
        try Data("not a zip".utf8).write(to: malformed)
        XCTAssertThrowsError(try DOCXTextExtractor.extract(from: malformed))
    }

    func testRejectsArchiveLargerThanConfiguredLimit() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let url = root.appendingPathComponent("large.docx")
        XCTAssertTrue(FileManager.default.createFile(atPath: url.path, contents: nil))
        let handle = try FileHandle(forWritingTo: url)
        try handle.truncate(atOffset: DOCXTextExtractor.maximumArchiveBytes + 1)
        try handle.close()
        XCTAssertThrowsError(try DOCXTextExtractor.extract(from: url)) { error in
            XCTAssertEqual(error.localizedDescription, DOCXTextExtractor.ExtractionError.archiveTooLarge.localizedDescription)
        }
    }

    private func makeDOCX(documentXML: Data, method: UInt16, declaredSize: UInt32? = nil) throws -> URL {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        addTeardownBlock { try? FileManager.default.removeItem(at: root) }

        let compressed = try deflateRaw(documentXML)
        let checksum = documentXML.withUnsafeBytes { bytes in
            UInt32(crc32(0, bytes.bindMemory(to: Bytef.self).baseAddress, uInt(documentXML.count)))
        }
        let name = Data("word/document.xml".utf8)
        var local = Data()
        append32(0x04034b50, to: &local); append16(20, to: &local); append16(0x0800, to: &local)
        append16(method, to: &local); append16(0, to: &local); append16(0, to: &local)
        append32(checksum, to: &local); append32(UInt32(compressed.count), to: &local); append32(declaredSize ?? UInt32(documentXML.count), to: &local)
        append16(UInt16(name.count), to: &local); append16(0, to: &local); local.append(name); local.append(compressed)

        var central = Data()
        append32(0x02014b50, to: &central); append16(20, to: &central); append16(20, to: &central)
        append16(0x0800, to: &central); append16(method, to: &central); append16(0, to: &central); append16(0, to: &central)
        append32(checksum, to: &central); append32(UInt32(compressed.count), to: &central); append32(declaredSize ?? UInt32(documentXML.count), to: &central)
        append16(UInt16(name.count), to: &central); append16(0, to: &central); append16(0, to: &central)
        append16(0, to: &central); append16(0, to: &central); append32(0, to: &central); append32(0, to: &central)
        central.append(name)

        var archive = local
        let centralOffset = UInt32(archive.count)
        archive.append(central)
        append32(0x06054b50, to: &archive); append16(0, to: &archive); append16(0, to: &archive)
        append16(1, to: &archive); append16(1, to: &archive); append32(UInt32(central.count), to: &archive)
        append32(centralOffset, to: &archive); append16(0, to: &archive)
        let url = root.appendingPathComponent("test.docx")
        try archive.write(to: url)
        return url
    }

    private func deflateRaw(_ data: Data) throws -> Data {
        var stream = z_stream()
        guard deflateInit2_(&stream, Z_BEST_SPEED, Z_DEFLATED, -MAX_WBITS, 8, Z_DEFAULT_STRATEGY,
                            ZLIB_VERSION, Int32(MemoryLayout<z_stream>.size)) == Z_OK else {
            throw DOCXTextExtractor.ExtractionError.invalidDocument
        }
        defer { deflateEnd(&stream) }
        let outputCapacity = data.count + 128
        var output = Data(count: outputCapacity)
        let status = data.withUnsafeBytes { source in
            output.withUnsafeMutableBytes { destination in
                stream.next_in = UnsafeMutablePointer<Bytef>(mutating: source.bindMemory(to: Bytef.self).baseAddress)
                stream.avail_in = uInt(data.count)
                stream.next_out = destination.bindMemory(to: Bytef.self).baseAddress
                stream.avail_out = uInt(outputCapacity)
                return deflate(&stream, Z_FINISH)
            }
        }
        guard status == Z_STREAM_END else { throw DOCXTextExtractor.ExtractionError.invalidDocument }
        output.removeSubrange(Int(stream.total_out)..<output.count)
        return output
    }

    private func append16(_ value: UInt16, to data: inout Data) {
        data.append(UInt8(truncatingIfNeeded: value)); data.append(UInt8(truncatingIfNeeded: value >> 8))
    }

    private func append32(_ value: UInt32, to data: inout Data) {
        append16(UInt16(truncatingIfNeeded: value), to: &data)
        append16(UInt16(truncatingIfNeeded: value >> 16), to: &data)
    }
}
