import XCTest
import FeigeIdentity
import Network

/// A cancelled send/receive abandons one operation; it must never tear down the transport, because
/// the handshake deadline cancels its own read group and later reads reuse the same connection.
final class NetworkByteStreamCancellationTests: XCTestCase {
    private final class LockedPort: @unchecked Sendable {
        private let lock = NSLock()
        private var stored: UInt16 = 0
        var value: UInt16 {
            get { lock.lock(); defer { lock.unlock() }; return stored }
            set { lock.lock(); stored = newValue; lock.unlock() }
        }
    }

    func testCancelledReceiveLeavesTheTransportUsable() async throws {
        let listener = try NWListener(using: .tcp, on: .any)
        let ready = expectation(description: "listener ready")
        let port = LockedPort()
        listener.stateUpdateHandler = { state in
            guard case .ready = state else { return }
            port.value = listener.port?.rawValue ?? 0
            ready.fulfill()
        }
        // The peer accepts but never writes, so the receive stays pending until it is cancelled.
        listener.newConnectionHandler = { _ in }
        listener.start(queue: DispatchQueue(label: "feige.cancellation.listener"))
        await fulfillment(of: [ready], timeout: 5)
        defer { listener.cancel() }

        let stream = NetworkByteStream(endpoint: .hostPort(host: "127.0.0.1",
                                                            port: NWEndpoint.Port(rawValue: port.value)!),
                                       requireLAN: false)
        try await stream.start(timeout: 5)
        defer { stream.close() }

        let receive = Task { try await stream.receive(maximumLength: 64, timeout: 30) }
        try await Task.sleep(for: .milliseconds(300))
        receive.cancel()
        _ = try? await receive.value

        // Before the fix this reported a failed connection, because cancelling closed the socket.
        do {
            _ = try await stream.receive(maximumLength: 64, timeout: 1)
            XCTFail("The peer never wrote, so this read should time out.")
        } catch let error as AuthenticatedStreamError {
            XCTAssertEqual(error, .timeout, "A cancelled read must leave the transport open, not broken.")
        }
    }
}
