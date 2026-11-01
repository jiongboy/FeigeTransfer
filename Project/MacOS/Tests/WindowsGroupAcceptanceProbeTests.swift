import XCTest
import FeigeIdentity
import Network
@testable import FeigeMac

/// Runs one isolated macOS client for the Windows group-management acceptance: it connects
/// to the Windows runtime configured through the environment, auto-approves connection
/// consent and group invitations, prints its fingerprint and port, and stays alive until a
/// marker file that the driver creates disappears.
final class WindowsGroupAcceptanceProbeTests: XCTestCase {
    @MainActor
    func testPrintRuntimeIdsForWindowsUiDriver() async throws {
        let environment = ProcessInfo.processInfo.environment
        guard environment["FEIGE_WIN_GROUP_TEST_PROBE"] == "1" else {
            throw XCTSkip("Set FEIGE_WIN_GROUP_TEST_PROBE=1 to run the Windows acceptance runtime.")
        }
        let target = (environment["FEIGE_WIN_GROUP_TEST_FINGERPRINT"] ?? "")
            .filter { !$0.isWhitespace }.uppercased()
        guard let host = environment["FEIGE_WIN_GROUP_TEST_HOST"], !host.isEmpty,
              let port = UInt16(environment["FEIGE_WIN_GROUP_TEST_PORT"] ?? ""), port != 0,
              !target.isEmpty else {
            throw XCTSkip("Set FEIGE_WIN_GROUP_TEST_{FINGERPRINT,HOST,PORT} to run the Windows acceptance runtime.")
        }
        let index = Int(environment["FEIGE_WIN_GROUP_TEST_INDEX"] ?? "") ?? 1
        let lifetime = environment["FEIGE_WIN_GROUP_TEST_PROBE_LIFETIME"] ?? ""
            .trimmingCharacters(in: .whitespacesAndNewlines)
        guard !lifetime.isEmpty else {
            throw XCTSkip("Set FEIGE_WIN_GROUP_TEST_PROBE_LIFETIME to a marker file path.")
        }
        let deadline = Date().addingTimeInterval(TimeInterval(environment["FEIGE_WIN_GROUP_TEST_TIMEOUT"] ?? "") ?? 120)
        let root = FileManager.default.temporaryDirectory
            .appendingPathComponent("FeigeWindowsRuntime-\(UUID().uuidString)", isDirectory: true)
        let support = root.appendingPathComponent("support", isDirectory: true)
        try FileManager.default.createDirectory(at: support, withIntermediateDirectories: true)
        let store = DeviceIdentityStore(serviceIdentifier: "com.feige.tests.windows-group.\(UUID().uuidString)")
        let identity = try store.loadOrCreate()
        let model = TransferModel(supportDirectory: support,
                                  identityService: "com.feige.tests.windows-group.unused",
                                  identityStore: store,
                                  identityLoader: { identity }, makeUpdates: false)
        try model.saveSettings(deviceName: "Feige macOS group runtime \(index)",
                               receiveDirectory: root.appendingPathComponent("received", isDirectory: true))
        // Group-owner acceptance runs the Windows UI with its own approval driver; never auto-accept
        // invitations in that mode or the remote page would never be exercised.
        let autoAcceptInvitations = environment["FEIGE_WIN_GROUP_TEST_AUTO_ACCEPT_INVITATIONS"] != "0"
        let approvals = Task { @MainActor in
            while !Task.isCancelled {
                if model.pendingConnectionPeerName != nil { model.approveConnection(true) }
                if autoAcceptInvitations {
                    for invitation in model.pendingGroupInvitations {
                        model.acceptGroupInvitation(invitation.id, accepted: true)
                    }
                }
                for approval in model.pendingGroupFileApprovals {
                    model.decideGroupFile(approval.manifest.metadata.transferID,
                                          peerID: approval.manifest.senderFingerprint, accepted: true)
                }
                for request in model.pendingGroupLeaveRequests {
                    model.resolveGroupLeaveRequest(request.id, accepted: true)
                }
                do { try await Task.sleep(for: .milliseconds(20)) } catch { return }
            }
        }
        defer {
            approvals.cancel()
            model.shutdown()
            store.delete()
            try? FileManager.default.removeItem(at: root)
        }
        await model.start()
        let listener = try NWListener(using: .tcp, on: .any)
        let (stream, continuation) = AsyncStream<UInt16>.makeStream()
        listener.stateUpdateHandler = { [weak listener] state in
            if case .ready = state, let port = listener?.port?.rawValue {
                continuation.yield(port); continuation.finish()
            }
        }
        listener.newConnectionHandler = { connection in Task { @MainActor in model.accept(connection) } }
        listener.start(queue: DispatchQueue(label: "feige.windows.group.runtime.\(index)"))
        var iterator = stream.makeAsyncIterator()
        let listenerPort = await iterator.next() ?? 0
        defer { listener.cancel() }
        while !model.isIdentityReady && Date() < deadline {
            try await Task.sleep(for: .milliseconds(50))
        }
        print("PROBE_RUNTIME|\(index)|\(identity.fingerprint)|\(listenerPort)|identityReady=\(model.isIdentityReady)|discovery=\(model.discoveryState)")
        fflush(stdout)
        let device = LANDevice(serviceName: "Feige-\(String(target.replacingOccurrences(of: " ", with: "").prefix(20)))",
                               displayName: "Windows runtime",
                               endpoint: NWEndpoint.hostPort(host: NWEndpoint.Host(host),
                                                             port: NWEndpoint.Port(rawValue: port) ?? .any),
                               protocolVersions: [3, 4, 5])
        var connected = false
        while Date() < deadline {
            if !connected && model.isIdentityReady && !model.isConnected(to: target) {
                model.nearbyDevices = [device]
                model.requestConnection(to: device, protocolVersion: 5)
                connected = true
            }
            if model.isConnected(to: target) { break }
            if let message = model.errorMessage { print("PROBE_ERROR|\(index)|\(message)"); model.clearError() }
            try await Task.sleep(for: .milliseconds(250))
        }
        print("PROBE_CONNECTED|\(index)|\(model.isConnected(to: target))")
        fflush(stdout)
        let holdsUntilFileDisappears = environment["FEIGE_WIN_GROUP_TEST_HOLD_ON_FILE"] != "0"
        while holdsUntilFileDisappears && FileManager.default.fileExists(atPath: lifetime), Date() < deadline {
            try await Task.sleep(for: .milliseconds(200))
        }
        // Owner mode: stay alive, report group state, and keep the runtime available for the UI test.
        if !holdsUntilFileDisappears {
            print("PROBE_READY|\(index)|\(identity.fingerprint)|\(listenerPort)|\(model.isConnected(to: target))")
            fflush(stdout)
            while Date() < deadline {
                try await Task.sleep(for: .milliseconds(200))
            }
        }
        print("PROBE_GROUPS|\(index)|\(model.groups.map { "\($0.name)#\($0.id.uuidString):\($0.members.count)" }.joined(separator: ","))")
        fflush(stdout)
    }
}
