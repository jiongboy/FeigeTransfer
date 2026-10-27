import Foundation
import FeigeIdentity

/// In-memory duplex byte transport for end-to-end tests. Two endpoints are paired;
/// each endpoint's `send` feeds the other's inbound buffer, and `receive` drains it.
/// Bounded to the same limits the real transport enforces, so tests exercise the same
/// framing rules. No real socket is opened. Send never blocks (buffer is unbounded in
/// memory), which mirrors the real transport's fire-and-forget send semantics closely
/// enough for the handshake and record-flow tests.
final class InMemoryDuplexTransport: ByteTransport, @unchecked Sendable {
    private let inbound = InboundBuffer()
    private weak var peer: InMemoryDuplexTransport?
    private let stateLock = NSLock()
    private var isClosed = false

    static func pair() -> (InMemoryDuplexTransport, InMemoryDuplexTransport) {
        let a = InMemoryDuplexTransport()
        let b = InMemoryDuplexTransport()
        a.peer = b
        b.peer = a
        return (a, b)
    }

    /// Synchronous snapshot of closed state, so async callers avoid direct NSLock use.
    private func snapshot() -> (closed: Bool, peer: InMemoryDuplexTransport?) {
        stateLock.lock()
        defer { stateLock.unlock() }
        return (isClosed, peer)
    }

    func start(timeout: TimeInterval) async throws {
        if snapshot().closed { throw NetworkTransportError.closed }
    }

    func send(_ data: Data, timeout: TimeInterval) async throws {
        guard !data.isEmpty else { return }
        guard data.count <= AuthenticatedRecordStream.maxReadBatchLength else {
            throw AuthenticatedStreamError.tooLarge
        }
        let state = snapshot()
        guard !state.closed, let target = state.peer else { throw NetworkTransportError.closed }
        target.inbound.append(data)
    }

    func receive(maximumLength: Int, timeout: TimeInterval) async throws -> Data {
        guard maximumLength > 0, maximumLength <= AuthenticatedRecordStream.maxReadBatchLength else {
            throw AuthenticatedStreamError.tooLarge
        }
        if snapshot().closed { throw NetworkTransportError.closed }
        return try await inbound.read(maximumLength: maximumLength, timeout: timeout)
    }

    func close() {
        stateLock.lock()
        let alreadyClosed = isClosed
        isClosed = true
        let peerNow = peer
        stateLock.unlock()
        if alreadyClosed { return }
        inbound.close()
        peerNow?.inbound.close()
    }

    /// Reads are served from a shared buffer; a pending reader is resumed as soon as the
    /// peer appends. EOF is surfaced as `unexpectedEOF` so the authenticated stream can
    /// enforce that a finish record arrived before the connection ended.
    private final class InboundBuffer: @unchecked Sendable {
        private var buffer = Data()
        // Each pending reader remembers the maximum length it asked for, so a resumed
        // reader is never handed more bytes than it requested (the handshake's readExactly
        // relies on this to frame messages correctly).
        private var readers: [(continuation: CheckedContinuation<Data, Error>, maximumLength: Int)] = []
        private var closed = false
        private let lock = NSLock()

        func append(_ data: Data) {
            let pending: [(CheckedContinuation<Data, Error>, Int)] = lock.withLock {
                buffer.append(data)
                let waiting = readers
                readers.removeAll()
                return waiting
            }
            for reader in pending {
                deliver(to: reader.0, maximumLength: reader.1)
            }
        }

        func read(maximumLength: Int, timeout: TimeInterval) async throws -> Data {
            try await withCheckedThrowingContinuation { continuation in
                var immediateChunk: Data?
                var immediateEOF = false
                var didQueue = false
                lock.withLock {
                    if !buffer.isEmpty {
                        let chunk = Data(buffer.prefix(maximumLength))
                        buffer.removeFirst(chunk.count)
                        immediateChunk = chunk
                    } else if closed {
                        immediateEOF = true
                    } else {
                        readers.append((continuation, maximumLength))
                        didQueue = true
                    }
                }
                if let immediateChunk {
                    continuation.resume(returning: immediateChunk)
                } else if immediateEOF {
                    continuation.resume(throwing: AuthenticatedStreamError.unexpectedEOF)
                } else if !didQueue {
                    continuation.resume(throwing: AuthenticatedStreamError.truncated)
                }
            }
        }

        func close() {
            let pending: [CheckedContinuation<Data, Error>] = lock.withLock {
                closed = true
                let waiting = readers.map(\.continuation)
                readers.removeAll()
                return waiting
            }
            for reader in pending {
                reader.resume(throwing: AuthenticatedStreamError.unexpectedEOF)
            }
        }

        private func deliver(to continuation: CheckedContinuation<Data, Error>,
                             maximumLength: Int) {
            var chunk: Data?
            var isEOF = false
            var didResume = false
            lock.withLock {
                if !buffer.isEmpty {
                    let part = Data(buffer.prefix(maximumLength))
                    buffer.removeFirst(part.count)
                    chunk = part
                } else if closed {
                    isEOF = true
                } else {
                    readers.append((continuation, maximumLength))
                    didResume = true
                }
            }
            if let chunk {
                continuation.resume(returning: chunk)
            } else if isEOF {
                continuation.resume(throwing: AuthenticatedStreamError.unexpectedEOF)
            } else if !didResume {
                continuation.resume(throwing: AuthenticatedStreamError.truncated)
            }
        }
    }
}
