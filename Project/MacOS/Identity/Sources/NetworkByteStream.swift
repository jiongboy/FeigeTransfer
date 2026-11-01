import Foundation
import Network

public enum NetworkTransportError: LocalizedError {
    case invalidHost, invalidPort, connectionFailed(String), closed
    public var errorDescription: String? {
        switch self {
        case .invalidHost: "设备地址无效。"
        case .invalidPort: "设备端口无效。"
        case .connectionFailed(let message): "无法建立局域网连接：\(message)"
        case .closed: "局域网连接已关闭。"
        }
    }
}

/// Bounded Network.framework byte stream. It has no TLS, discovery or unauthenticated file
/// handlers: callers must complete signed handshake and connection consent first, then exchange
/// only AuthenticatedRecordStream frames. Used for outgoing and accepted LAN connections.
public final class NetworkByteStream: ByteTransport, @unchecked Sendable {
    private let connection: NWConnection
    private let queue = DispatchQueue(label: "com.feige.transfer.network", qos: .utility)
    private let stateLock = NSLock()
    private var started = false
    private var isClosed = false
    private var startWaiters: [CheckedContinuation<Void, Error>] = []
    /// Registers how to resume the operation currently suspended on a framework callback, so a
    /// cancelled operation unwinds without tearing the transport down.
    private let pendingAbandon = AbandonBox()
    private let requireLAN: Bool

    public init(host: String, port: UInt16, requireLAN: Bool = true) throws {
        guard !host.isEmpty, !host.contains("/") else { throw NetworkTransportError.invalidHost }
        guard let nwPort = NWEndpoint.Port(rawValue: port) else { throw NetworkTransportError.invalidPort }
        connection = NWConnection(host: NWEndpoint.Host(host), port: nwPort, using: .tcp)
        self.requireLAN = requireLAN
    }

    /// Wrap accepted connections as well as outgoing connections in the same bounded adapter.
    public init(connection: NWConnection, requireLAN: Bool = true) {
        self.connection = connection
        self.requireLAN = requireLAN
    }

    public init(endpoint: NWEndpoint, requireLAN: Bool = true) {
        connection = NWConnection(to: endpoint, using: .tcp)
        self.requireLAN = requireLAN
    }

    private func timed<T: Sendable>(timeout: TimeInterval,
                                    operation: @escaping @Sendable () async throws -> T) async throws -> T {
        guard timeout > 0 else { throw AuthenticatedStreamError.timeout }
        return try await withTaskCancellationHandler {
            try Task.checkCancellation()
            if timeout == .infinity { return try await operation() }
            guard timeout.isFinite else { throw AuthenticatedStreamError.timeout }
            return try await withThrowingTaskGroup(of: T.self) { group in
                group.addTask { try await operation() }
                group.addTask {
                    try await Task.sleep(for: .seconds(timeout))
                    // A real timeout invalidates the stream: the peer stopped responding.
                    self.close()
                    throw AuthenticatedStreamError.timeout
                }
                defer { group.cancelAll() }
                guard let result = try await group.next() else { throw NetworkTransportError.closed }
                return result
            }
        } onCancel: {
            // Caller-driven cancellation abandons this one operation, not the connection: the
            // handshake deadline cancels its read group and the session must stay usable. Resuming
            // the pending Network.framework callback keeps the continuation from leaking.
            self.abandonPendingOperation()
        }
    }

    /// Resumes the operation that is suspended on a Network.framework callback, if any, so a
    /// cancelled `send`/`receive` returns instead of hanging while the transport stays open.
    private func abandonPendingOperation() {
        pendingAbandon.call()
    }

    public func start(timeout: TimeInterval = 10) async throws {
        try await timed(timeout: timeout) { try await self.startUntilReady() }
    }

    private func startUntilReady() async throws {
        try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, Error>) in
            stateLock.lock()
            guard !started, !isClosed else {
                stateLock.unlock()
                continuation.resume(throwing: NetworkTransportError.closed)
                return
            }
            started = true
            startWaiters.append(continuation)
            stateLock.unlock()
            connection.stateUpdateHandler = { [weak self] state in
                guard let self else { return }
                switch state {
                case .ready:
                    if self.requireLAN {
                        guard let endpoint = self.connection.currentPath?.remoteEndpoint,
                              LANEndpointPolicy.allows(endpoint) else {
                            self.resumeStartWaiters(.failure(NetworkTransportError.invalidHost))
                            self.close()
                            return
                        }
                    }
                    self.resumeStartWaiters(.success(()))
                case .failed(let error):
                    self.resumeStartWaiters(.failure(NetworkTransportError.connectionFailed(error.localizedDescription)))
                case .cancelled:
                    self.resumeStartWaiters(.failure(NetworkTransportError.closed))
                default: break
                }
            }
            connection.start(queue: queue)
        }
    }

    public func send(_ data: Data, timeout: TimeInterval = 15) async throws {
        guard !data.isEmpty, data.count <= AuthenticatedRecordStream.maxReadBatchLength else {
            throw AuthenticatedStreamError.tooLarge
        }
        try await timed(timeout: timeout) {
            try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, Error>) in
                let resume = Suspension(continuation)
                self.pendingAbandon.set { resume.fail(AuthenticatedStreamError.cancelled) }
                self.connection.send(content: data, completion: .contentProcessed { error in
                    self.pendingAbandon.clear()
                    if let error { resume.fail(NetworkTransportError.connectionFailed(error.localizedDescription)) }
                    else { resume.succeed(()) }
                })
            }
        }
    }

    public func receive(maximumLength: Int = AuthenticatedRecordStream.maxReadBatchLength,
                        timeout: TimeInterval = 15) async throws -> Data {
        guard maximumLength > 0, maximumLength <= AuthenticatedRecordStream.maxReadBatchLength else {
            throw AuthenticatedStreamError.tooLarge
        }
        return try await timed(timeout: timeout) {
            try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Data, Error>) in
                let resume = Suspension(continuation)
                self.pendingAbandon.set { resume.fail(AuthenticatedStreamError.cancelled) }
                self.connection.receive(minimumIncompleteLength: 1, maximumLength: maximumLength) { data, _, complete, error in
                    self.pendingAbandon.clear()
                    if let error { resume.fail(NetworkTransportError.connectionFailed(error.localizedDescription)) }
                    else if let data, !data.isEmpty { resume.succeed(data) }
                    else if complete { resume.fail(AuthenticatedStreamError.unexpectedEOF) }
                    else { resume.fail(AuthenticatedStreamError.truncated) }
                }
            }
        }
    }

    public func close() {
        stateLock.lock()
        guard !isClosed else { stateLock.unlock(); return }
        isClosed = true
        stateLock.unlock()
        connection.cancel()
        resumeStartWaiters(.failure(NetworkTransportError.closed))
    }

    private func resumeStartWaiters(_ result: Result<Void, Error>) {
        stateLock.lock()
        let waiters = startWaiters
        startWaiters.removeAll()
        stateLock.unlock()
        for waiter in waiters {
            switch result {
            case .success: waiter.resume(returning: ())
            case .failure(let error): waiter.resume(throwing: error)
            }
        }
    }
}

/// Holds the abort hook for the operation currently suspended on a framework callback.
private final class AbandonBox: @unchecked Sendable {
    private let lock = NSLock()
    private var hook: (@Sendable () -> Void)?
    func set(_ next: @escaping @Sendable () -> Void) { lock.lock(); hook = next; lock.unlock() }
    func clear() { lock.lock(); hook = nil; lock.unlock() }
    func call() { lock.lock(); let current = hook; hook = nil; lock.unlock(); current?() }
    var pointee: (@Sendable () -> Void)? {
        lock.lock(); defer { lock.unlock() }; return hook
    }
}

/// Resumes a continuation exactly once, whichever side reports first.
private final class Suspension<T: Sendable>: @unchecked Sendable {
    private let lock = NSLock()
    private var continuation: CheckedContinuation<T, Error>?
    init(_ continuation: CheckedContinuation<T, Error>) { self.continuation = continuation }
    func succeed(_ value: T) { lock.lock(); let c = continuation; continuation = nil; lock.unlock(); c?.resume(returning: value) }
    func fail(_ error: Error) { lock.lock(); let c = continuation; continuation = nil; lock.unlock(); c?.resume(throwing: error) }
}
