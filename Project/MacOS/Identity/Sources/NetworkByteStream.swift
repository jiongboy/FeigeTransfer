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
                    // Cancelling a task alone does not resume Network.framework callbacks.
                    self.close()
                    throw AuthenticatedStreamError.timeout
                }
                defer { group.cancelAll() }
                guard let result = try await group.next() else { throw NetworkTransportError.closed }
                return result
            }
        } onCancel: {
            self.close()
        }
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
                self.connection.send(content: data, completion: .contentProcessed { error in
                    if let error { continuation.resume(throwing: NetworkTransportError.connectionFailed(error.localizedDescription)) }
                    else { continuation.resume(returning: ()) }
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
                self.connection.receive(minimumIncompleteLength: 1, maximumLength: maximumLength) { data, _, complete, error in
                    if let error { continuation.resume(throwing: NetworkTransportError.connectionFailed(error.localizedDescription)) }
                    else if let data, !data.isEmpty { continuation.resume(returning: data) }
                    else if complete { continuation.resume(throwing: AuthenticatedStreamError.unexpectedEOF) }
                    else { continuation.resume(throwing: AuthenticatedStreamError.truncated) }
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
