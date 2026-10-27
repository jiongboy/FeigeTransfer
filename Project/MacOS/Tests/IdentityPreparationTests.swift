import XCTest
import FeigeIdentity
@testable import FeigeMac

final class IdentityPreparationTests: XCTestCase {
    @MainActor
    func testAuthorizationFinishesBeforeNetworkingAndIsCachedAcrossStarts() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let store = DeviceIdentityStore(serviceIdentifier: "com.feige.prepare.\(UUID())")
        defer { store.delete(); try? FileManager.default.removeItem(at: root) }
        let identity = try store.loadOrCreate()
        let gate = PreparationGate(identity: identity)
        let model = TransferModel(supportDirectory: root, identityLoader: { try gate.load() })
        defer { gate.release(); model.shutdown() }
        try model.saveSettings(deviceName: "准备测试", receiveDirectory: root.appendingPathComponent("received"))
        let starting = Task { await model.start() }
        while gate.calls == 0 { try await Task.sleep(for: .milliseconds(5)) }
        XCTAssertTrue(model.isPreparingIdentity)
        XCTAssertFalse(model.isIdentityReady)
        XCTAssertNil(model.listenerPort)
        let device = try model.manualDevice(host: "127.0.0.1", port: "12345")
        model.requestConnection(to: device)
        XCTAssertFalse(model.isConnecting)
        XCTAssertNotNil(model.errorMessage)
        gate.release()
        await starting.value
        XCTAssertTrue(model.isIdentityReady)
        XCTAssertFalse(model.isPreparingIdentity)
        XCTAssertEqual(model.identityFingerprint, identity.fingerprint)
        model.disconnect()
        await model.start()
        XCTAssertTrue(model.isIdentityReady)
        XCTAssertEqual(gate.calls, 1)
    }

    @MainActor
    func testFailedAuthorizationCanRetryWithoutRestartingApp() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let store = DeviceIdentityStore(serviceIdentifier: "com.feige.retry.\(UUID())")
        defer { store.delete(); try? FileManager.default.removeItem(at: root) }
        let gate = PreparationGate(identity: try store.loadOrCreate(), failFirst: true)
        gate.release()
        let model = TransferModel(supportDirectory: root, identityLoader: { try gate.load() })
        defer { model.shutdown() }
        try model.saveSettings(deviceName: "重试测试", receiveDirectory: root.appendingPathComponent("received"))
        await model.start()
        XCTAssertFalse(model.isIdentityReady)
        XCTAssertNotNil(model.identityPreparationError)
        XCTAssertNil(model.listenerPort)
        await model.start()
        XCTAssertTrue(model.isIdentityReady)
        XCTAssertNil(model.identityPreparationError)
        XCTAssertEqual(gate.calls, 2)
    }

    @MainActor
    func testFirstRunDoesNotPrepareIdentityUntilSetupContinues() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let store = DeviceIdentityStore(serviceIdentifier: "com.feige.first-run.(UUID())")
        defer { store.delete(); try? FileManager.default.removeItem(at: root) }
        let gate = PreparationGate(identity: try store.loadOrCreate())
        let model = TransferModel(supportDirectory: root, identityLoader: { try gate.load() })
        defer { gate.release(); model.shutdown() }
        await model.start()
        XCTAssertTrue(model.isFirstRun)
        XCTAssertEqual(gate.calls, 0)
        let preparation = Task { await model.prepareIdentityForSetup() }
        while gate.calls == 0 { try await Task.sleep(for: .milliseconds(5)) }
        gate.release()
        await preparation.value
        XCTAssertTrue(model.isIdentityReady)
        XCTAssertNil(model.listenerPort)
    }

    @MainActor
    func testShutdownDuringAuthorizationDoesNotStartListenerAfterwards() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let store = DeviceIdentityStore(serviceIdentifier: "com.feige.quitprepare.\(UUID())")
        defer { store.delete(); try? FileManager.default.removeItem(at: root) }
        let gate = PreparationGate(identity: try store.loadOrCreate())
        let model = TransferModel(supportDirectory: root, identityLoader: { try gate.load() })
        defer { gate.release(); model.shutdown() }
        try model.saveSettings(deviceName: "退出测试", receiveDirectory: root.appendingPathComponent("received"))
        let starting = Task { await model.start() }
        while gate.calls == 0 { try await Task.sleep(for: .milliseconds(5)) }
        model.shutdown()
        gate.release()
        await starting.value
        XCTAssertFalse(model.isIdentityReady)
        XCTAssertFalse(model.isPreparingIdentity)
        XCTAssertNil(model.listenerPort)
    }
}

private final class PreparationGate: @unchecked Sendable {
    private let lock = NSLock()
    private let semaphore = DispatchSemaphore(value: 0)
    private let identity: DeviceIdentity
    private let failFirst: Bool
    private var count = 0
    var calls: Int { lock.withLock { count } }
    init(identity: DeviceIdentity, failFirst: Bool = false) { self.identity = identity; self.failFirst = failFirst }
    func release() { semaphore.signal() }
    func load() throws -> DeviceIdentity {
        let call = lock.withLock { count += 1; return count }
        if call == 1 {
            guard semaphore.wait(timeout: .now() + 5) == .success else { throw CancellationError() }
            if failFirst { throw DeviceIdentityError.signingAuthorization(-25293) }
        }
        return identity
    }
}
