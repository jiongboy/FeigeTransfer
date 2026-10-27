import Foundation

/// Minimal bounded duplex byte transport used by the authenticated handshake and the
/// record stream. `NetworkByteStream` is the real Network.framework adapter; tests provide
/// an in-memory duplex pipe so the full handshake and transfer flow can be exercised
/// end-to-end without opening a real socket. No unauthenticated file handler lives here.
public protocol ByteTransport: AnyObject, Sendable {
    func start(timeout: TimeInterval) async throws
    func send(_ data: Data, timeout: TimeInterval) async throws
    func receive(maximumLength: Int, timeout: TimeInterval) async throws -> Data
    func close()
}
