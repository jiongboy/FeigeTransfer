import XCTest
import Network
import CryptoKit
import FeigeIdentity

private final class TestListener: @unchecked Sendable {
    let listener: NWListener
    let connections: AsyncStream<NWConnection>
    private let connectionContinuation: AsyncStream<NWConnection>.Continuation
    init() throws {
        listener = try NWListener(using: .tcp, on: .any)
        let pair = AsyncStream<NWConnection>.makeStream()
        connections = pair.stream
        connectionContinuation = pair.continuation
    }
    func start() async throws -> UInt16 {
        try await withCheckedThrowingContinuation { continuation in
            let gate = OnceContinuation(continuation)
            listener.stateUpdateHandler = { [listener] state in
                if case .ready = state, let port = listener.port { gate.resolve(.success(port.rawValue)) }
                if case .failed(let error) = state { gate.resolve(.failure(error)) }
                if case .cancelled = state { gate.resolve(.failure(CancellationError())) }
            }
            listener.newConnectionHandler = { [connectionContinuation] in connectionContinuation.yield($0) }
            listener.start(queue: DispatchQueue(label: "feige.test.listener"))
        }
    }
    func stop() { listener.cancel(); connectionContinuation.finish() }
    deinit { stop() }
}

private final class OnceContinuation: @unchecked Sendable {
    private let lock = NSLock()
    private var continuation: CheckedContinuation<UInt16, Error>?
    init(_ continuation: CheckedContinuation<UInt16, Error>) { self.continuation = continuation }
    func resolve(_ result: Result<UInt16, Error>) {
        let value = lock.withLock { let c = continuation; continuation = nil; return c }
        value?.resume(with: result)
    }
}

final class NetworkTransferTests: XCTestCase {
    private func transports() async throws -> (NetworkByteStream, NetworkByteStream, TestListener) {
        let listener = try TestListener()
        let port = try await listener.start()
        let client = try NetworkByteStream(host: "127.0.0.1", port: port)
        async let starting: Void = client.start(timeout: 3)
        var iterator = listener.connections.makeAsyncIterator()
        let next = await iterator.next()
        let accepted = try XCTUnwrap(next)
        let server = NetworkByteStream(connection: accepted)
        try await server.start(timeout: 3)
        try await starting
        return (client, server, listener)
    }

    func testRealTCPHandshakeTransferAndAtomicCommit() async throws {
        let (client, server, listener) = try await transports()
        defer { client.close(); server.close(); listener.stop() }
        let a = DeviceIdentityStore(serviceIdentifier: "com.feige.tcp.a.\(UUID())")
        let b = DeviceIdentityStore(serviceIdentifier: "com.feige.tcp.b.\(UUID())")
        defer { a.delete(); b.delete() }
        let idA = try a.loadOrCreate()
        let idB = try b.loadOrCreate()
        async let connecting = TransferService.establishSession(transport: client, identity: idA,
                                                               role: .initiator, deviceName: "A", sessionCode: "123456") {
            UserConfirmation(peerFingerprint: idB.fingerprint, enteredSAS: $0.sas)
        }
        let sessionB = try await TransferService.establishSession(transport: server, identity: idB,
                                                                 role: .responder, deviceName: "B", sessionCode: "123456") {
            UserConfirmation(peerFingerprint: idA.fingerprint, enteredSAS: $0.sas)
        }
        let sessionA = try await connecting
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let content = Data((0..<250_000).map { UInt8($0 % 251) })
        let input = root.appendingPathComponent("input.bin")
        try content.write(to: input)
        let manifest = try FileManifest(fileName: "input.bin", size: UInt64(content.count), sha256: Data(SHA256.hash(data: content)))
        let sink = try TemporaryFileSink(directoryURL: root, manifest: manifest)
        let sender = TransferSession(session: sessionA, transport: client)
        let receiver = TransferSession(session: sessionB, transport: server)
        async let sending: Void = sender.send(fileURL: input)
        _ = try await receiver.receive(approve: { _ in true }, sink: { try sink.write($0) }, finalize: { try sink.commit() })
        try await sending
        XCTAssertEqual(try Data(contentsOf: XCTUnwrap(sink.savedURL)), content)
        XCTAssertEqual(sink.savedURL?.lastPathComponent, "input (1).bin")
    }

    func testReceiveTimeoutAndCancellationActuallyReturn() async throws {
        let (client, server, listener) = try await transports()
        defer { client.close(); server.close(); listener.stop() }
        let start = Date()
        do { _ = try await client.receive(maximumLength: 1, timeout: 0.05); XCTFail("expected timeout") }
        catch { XCTAssertLessThan(Date().timeIntervalSince(start), 2) }

        let (client2, server2, listener2) = try await transports()
        defer { client2.close(); server2.close(); listener2.stop() }
        let reading = Task { try await client2.receive(maximumLength: 1, timeout: 5) }
        try await Task.sleep(for: .milliseconds(30))
        reading.cancel()
        do { _ = try await reading.value; XCTFail("expected cancellation") }
        catch { XCTAssertLessThan(Date().timeIntervalSince(start), 2) }
    }

    func testV2StreamsMoreThan100MiBOverRealTCPWithoutSizeCap() async throws {
        let (client, server, listener) = try await transports()
        defer { client.close(); server.close(); listener.stop() }
        let a = DeviceIdentityStore(serviceIdentifier: "com.feige.large.a.\(UUID())")
        let b = DeviceIdentityStore(serviceIdentifier: "com.feige.large.b.\(UUID())")
        defer { a.delete(); b.delete() }
        let identityA = try a.loadOrCreate()
        let identityB = try b.loadOrCreate()
        async let connecting = ConnectionService.establish(transport: client, identity: identityA, role: .initiator, deviceName: "A")
        let receiver = try await ConnectionService.establish(transport: server, identity: identityB, role: .responder, deviceName: "B") { _ in true }
        let sender = try await connecting
        defer { sender.close(); receiver.close() }
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let output = root.appendingPathComponent("received")
        try FileManager.default.createDirectory(at: output, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let input = root.appendingPathComponent("large.bin")
        XCTAssertTrue(FileManager.default.createFile(atPath: input.path, contents: nil))
        let file = try FileHandle(forWritingTo: input)
        let size: UInt64 = 128 * 1024 * 1024 + 17
        try file.truncate(atOffset: size)
        try file.close()
        let readerA = Task { try await sender.run(receiveDirectory: root, didReceive: { _, _ in }) }
        let readerB = Task { try await receiver.run(receiveDirectory: output, didReceive: { _, _ in }) }
        defer { readerA.cancel(); readerB.cancel() }
        let metadata = try await sender.sendFile(input)
        XCTAssertEqual(metadata.size, size)
        let saved = output.appendingPathComponent("large.bin")
        XCTAssertEqual(try saved.resourceValues(forKeys: [.fileSizeKey]).fileSize, Int(size))
        XCTAssertEqual(try Self.hash(input), try Self.hash(saved))
        sender.close(); receiver.close()
        _ = try? await readerA.value
        _ = try? await readerB.value
    }

    private static func hash(_ file: URL) throws -> Data {
        let handle = try FileHandle(forReadingFrom: file)
        defer { try? handle.close() }
        var digest = SHA256()
        while let bytes = try handle.read(upToCount: 64 * 1024), !bytes.isEmpty { digest.update(data: bytes) }
        return Data(digest.finalize())
    }

    func testLANPolicyRejectsPublicAddressesAndNames() {
        for host in ["8.8.8.8", "example.com", "0.0.0.0", "224.0.0.1", "2001:4860:4860::8888"] {
            XCTAssertFalse(LANEndpointPolicy.allows(.hostPort(host: NWEndpoint.Host(host), port: 1234)))
        }
        for host in ["127.0.0.1", "192.168.1.2", "10.0.0.2", "172.16.1.2", "169.254.1.2", "::1", "fd00::1", "fe80::1"] {
            XCTAssertTrue(LANEndpointPolicy.allows(.hostPort(host: NWEndpoint.Host(host), port: 1234)))
        }
    }
}
