import XCTest
import FeigeIdentity
import Network
@testable import FeigeMac

/// Drives the Windows groups page over a real LAN link: two isolated macOS runtimes dial
/// the isolated Windows runtime, auto-approve connection consent and group invitations,
/// and assert that roster changes started from the Windows UI commit on every side.
final class WindowsGroupManagementAcceptanceTests: XCTestCase {
    @MainActor
    func testWindowsUiGroupCreateAddAndRemoveOverLANWhenConfigured() async throws {
        let environment = ProcessInfo.processInfo.environment
        guard let windowsFingerprint = environment["FEIGE_WIN_GROUP_TEST_FINGERPRINT"], !windowsFingerprint.isEmpty,
              let windowsHost = environment["FEIGE_WIN_GROUP_TEST_HOST"], !windowsHost.isEmpty,
              let windowsPort = UInt16(environment["FEIGE_WIN_GROUP_TEST_PORT"] ?? ""),
              windowsPort != 0 else {
            throw XCTSkip("Set FEIGE_WIN_GROUP_TEST_{FINGERPRINT,HOST,PORT} to run the Windows LAN UI acceptance test.")
        }
        let expectedGroupName = environment["FEIGE_WIN_GROUP_TEST_NAME"] ?? ""
        let timeout = TimeInterval(environment["FEIGE_WIN_GROUP_TEST_TIMEOUT"] ?? "") ?? 300
        // Layout in groups of four, which is what group validation stores on the wire.
        let compact = windowsFingerprint.filter { !$0.isWhitespace }.uppercased()
        let target = compact.count == 64
            ? stride(from: 0, to: 64, by: 4).map { String(compact.dropFirst($0).prefix(4)) }.joined(separator: " ")
            : compact
        let root = FileManager.default.temporaryDirectory
            .appendingPathComponent("FeigeWindowsGroupManagement-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: root) }

        var setups: [Setup] = []
        defer { setups.forEach { $0.tearDown() } }
        for index in 0..<2 { setups.append(try await Setup.make(index: index, root: root)) }
        let first = setups[0]
        let second = setups[1]
        let firstID = first.identity.fingerprint
        let secondID = second.identity.fingerprint
        print("WINDOWS_GROUP_MANAGEMENT_IDS|\(firstID)|\(secondID)")
        print("WINDOWS_GROUP_MANAGEMENT_LISTENERS|\(first.port)|\(second.port)")

        let windowsDevice = LANDevice(serviceName: "Feige-\(String(target.replacingOccurrences(of: " ", with: "").prefix(20)))",
                                      displayName: "Windows runtime",
                                      endpoint: NWEndpoint.hostPort(host: NWEndpoint.Host(windowsHost),
                                                                    port: NWEndpoint.Port(rawValue: windowsPort) ?? .any),
                                      protocolVersions: [3, 4, 5])
        for setup in setups { setup.connect(to: windowsDevice) }
        try await waitUntil(timeout: 120) { setups.allSatisfy { $0.model.isConnected(to: target) } }

        let groupID = try await waitUntilValue(timeout: timeout) {
            let matches = first.model.groups.filter { group in
                (expectedGroupName.isEmpty || group.name == expectedGroupName) &&
                group.ownerFingerprint == target &&
                group.members.contains { $0.fingerprint == firstID } &&
                group.members.contains { $0.fingerprint == secondID }
            }
            return matches.count == 1 ? matches[0].id : nil
        }
        print("WINDOWS_GROUP_MANAGEMENT_GROUP|\(groupID.uuidString)")

        try await waitUntil(timeout: 90) {
            Self.containsMember(setups[0].model, groupID, secondID) &&
            Self.containsMember(setups[1].model, groupID, secondID) &&
            Self.connectedMembers(setups[0].model, groupID).count == 2
        }
        XCTAssertTrue(first.model.canSendGroupText(groupID))

        try await waitUntil(timeout: timeout) {
            setups.allSatisfy {
                !Self.containsMember($0.model, groupID, secondID) && Self.containsMember($0.model, groupID, firstID)
            }
        }
        print("WINDOWS_GROUP_MANAGEMENT_DONE|\(groupID.uuidString)")
    }

    /// The Windows groups page accepts a macOS owner's invitation, then the roster and group text
    /// must agree on every member. This direction only needs UIA Invoke, which works on the
    /// hidden acceptance desktop, so it covers the invite path the mouse-only dialogs cannot.
    @MainActor
    func testMacOwnerInvitesWindowsAndWindowsAcceptsFromItsGroupsPage() async throws {
        let environment = ProcessInfo.processInfo.environment
        guard let windowsFingerprint = environment["FEIGE_WIN_GROUP_TEST_FINGERPRINT"], !windowsFingerprint.isEmpty,
              let windowsHost = environment["FEIGE_WIN_GROUP_TEST_HOST"], !windowsHost.isEmpty,
              let windowsPort = UInt16(environment["FEIGE_WIN_GROUP_TEST_PORT"] ?? ""), windowsPort != 0 else {
            throw XCTSkip("Set FEIGE_WIN_GROUP_TEST_{FINGERPRINT,HOST,PORT} to run the Windows acceptance test.")
        }
        let timeout = TimeInterval(environment["FEIGE_WIN_GROUP_TEST_TIMEOUT"] ?? "") ?? 300
        let resultFile = environment["FEIGE_WIN_GROUP_TEST_RESULT_FILE"] ?? ""
        let deadlineFile = environment["FEIGE_WIN_GROUP_TEST_DEADLINE_FILE"] ?? ""
        let compact = windowsFingerprint.filter { !$0.isWhitespace }.uppercased()
        let target = compact.count == 64
            ? stride(from: 0, to: 64, by: 4).map { String(compact.dropFirst($0).prefix(4)) }.joined(separator: " ")
            : compact
        let groupName = environment["FEIGE_WIN_GROUP_TEST_NAME"] ?? "Mac-owner invite acceptance"
        let root = FileManager.default.temporaryDirectory
            .appendingPathComponent("FeigeWindowsGroupInvite-\(UUID().uuidString)", isDirectory: true)
        let keepRoot = environment["FEIGE_WIN_GROUP_TEST_KEEP_ROOT"] == "1"
        defer {
            if keepRoot { print("WINDOWS_GROUP_INVITE_ROOT|\(root.path)") }
            else { try? FileManager.default.removeItem(at: root) }
        }

        var setups: [Setup] = []
        defer { setups.forEach { $0.tearDown() } }
        for index in 0..<2 { setups.append(try await Setup.make(index: index, root: root)) }
        let owner = setups[0]
        let member = setups[1]
        let ownerID = owner.identity.fingerprint
        let memberID = member.identity.fingerprint
        print("WINDOWS_GROUP_INVITE_IDS|\(ownerID)|\(memberID)")
        print("WINDOWS_GROUP_INVITE_LISTENERS|\(owner.port)|\(member.port)")
        if !resultFile.isEmpty {
            try Data("\(ownerID)|\(memberID)|\(owner.port)|\(member.port)\n".utf8)
                .write(to: URL(fileURLWithPath: resultFile), options: .atomic)
        }

        let windowsDevice = LANDevice(serviceName: "Feige-\(String(target.replacingOccurrences(of: " ", with: "").prefix(20)))",
                                      displayName: "Windows runtime",
                                      endpoint: NWEndpoint.hostPort(host: NWEndpoint.Host(windowsHost),
                                                                    port: NWEndpoint.Port(rawValue: windowsPort) ?? .any),
                                      protocolVersions: [3, 4, 5])
        // Owner mode only needs the channel used for the invitation; the extra member exists so a
        // later roster change has a real peer to remove.
        // `requestConnection` silently ignores a call while the identity is still preparing, so
        // wait for readiness first and report why the channel never appeared.
        try await waitUntil(timeout: 60, message: "macOS runtime identity never became ready") {
            owner.model.isIdentityReady
        }
        let readiness = owner.model.connectionReadiness(for: windowsDevice)
        print("WINDOWS_GROUP_INVITE_READINESS|\(readiness)")
        if readiness != "ok" { throw XCTSkip("requestConnection would decline: \(readiness)") }
        owner.connect(to: windowsDevice)
        let deadline = Date().addingTimeInterval(300)
        var lastConnectionDiagnostic = Date.distantPast
        while !owner.model.isConnected(to: target), Date() < deadline {
            if let authenticatedPeer = owner.model.connectedPeerIDs.first(where: { $0 != target }) {
                XCTFail("The Windows runtime authenticated as \(authenticatedPeer), but the configured fingerprint is \(target). Refresh FEIGE_WIN_GROUP_TEST_FINGERPRINT.")
                return
            }
            if keepRoot, Date().timeIntervalSince(lastConnectionDiagnostic) >= 5 {
                lastConnectionDiagnostic = Date()
                print("WINDOWS_GROUP_INVITE_STATE|\(owner.model.connectionState)|connecting=\(owner.model.isConnecting)|pending=\(owner.model.isConnectionPending(peerID: target))|error=\(owner.model.errorMessage ?? "-")")
            }
            try await Task.sleep(for: .seconds(5))
        }
        XCTAssertTrue(owner.model.isConnected(to: target),
                      "no encrypted channel to the Windows runtime: state=\(owner.model.connectionState); error=\(owner.model.errorMessage ?? "-")")
        let channelInfo = owner.model.groupChannelSummary(target)
        print("WINDOWS_GROUP_INVITE_CHANNEL|\(channelInfo)")
        guard channelInfo.hasPrefix("v5") || channelInfo.hasPrefix("v4") else {
            throw XCTSkip("Windows runtime did not expose a group-capable channel: \(channelInfo)")
        }
        _ = member

        let groupID = try await owner.model.createGroup(name: groupName, memberPeerIDs: [target])
        print("WINDOWS_GROUP_INVITE_GROUP|\(groupID.uuidString)|\(groupName)")
        if !deadlineFile.isEmpty {
            let deadline = Date().addingTimeInterval(timeout)
            try Data("\(deadline.timeIntervalSince1970)\n".utf8)
                .write(to: URL(fileURLWithPath: deadlineFile), options: .atomic)
        }
        // A message accepted by Windows proves both the invitation and membership commit reached
        // its durable group store. Check the owner's final roster after the remote acknowledgment.
        let text = "mac-owner-invite-\(UUID().uuidString.prefix(8))"
        XCTAssertTrue(owner.model.sendGroupText(text, groupID: groupID))
        try await waitUntil(timeout: 120) {
            guard let message = owner.model.groupMessages.first(where: { $0.groupID == groupID && $0.text == text }),
                  let delivery = owner.model.groupDeliveries.first(where: { $0.groupID == groupID && $0.id == message.id })
            else { return false }
            return delivery.isComplete && delivery.deliveredCount == 1
        }
        let group = try XCTUnwrap(owner.model.groups.first(where: { $0.id == groupID }))
        XCTAssertEqual(Self.normalizedMembers(owner.model, groupID), [ownerID, target].map(Self.normalize).sorted())
        XCTAssertEqual(group.ownerFingerprint, ownerID)
        print("WINDOWS_GROUP_INVITE_DONE|\(groupID.uuidString)")
    }

    @MainActor
    private static func normalizedMembers(_ model: TransferModel, _ groupID: UUID) -> [String] {
        (model.groups.first(where: { $0.id == groupID })?.members.map { normalize($0.fingerprint) } ?? []).sorted()
    }

    @MainActor
    private static func normalize(_ fingerprint: String) -> String {
        fingerprint.filter { !$0.isWhitespace }.uppercased()
    }

    @MainActor
    private static func containsMember(_ model: TransferModel, _ groupID: UUID, _ fingerprint: String) -> Bool {
        model.groups.first(where: { $0.id == groupID })?.members.contains {
            normalize($0.fingerprint) == normalize(fingerprint)
        } == true
    }

    @MainActor
    private static func connectedMembers(_ model: TransferModel, _ groupID: UUID) -> [String] {
        guard let group = model.groups.first(where: { $0.id == groupID }) else { return [] }
        let selfFingerprint = normalize(model.identityFingerprint)
        return group.members.map(\.fingerprint)
            .filter { normalize($0) != selfFingerprint && model.isConnected(to: $0) }
    }

    @MainActor
    private func waitUntil(timeout: TimeInterval, message: String = "Timed out waiting for the Windows group change.",
                           file: StaticString = #filePath, line: UInt = #line,
                           condition: @MainActor () -> Bool) async throws {
        let deadline = Date().addingTimeInterval(timeout)
        while !condition(), Date() < deadline { try await Task.sleep(for: .milliseconds(50)) }
        guard condition() else {
            XCTFail(message, file: file, line: line)
            throw WindowsGroupAcceptanceError.timedOut
        }
    }

    @MainActor
    private func waitUntilValue<T>(timeout: TimeInterval, file: StaticString = #filePath, line: UInt = #line,
                                   condition: @MainActor () -> T?) async throws -> T {
        let deadline = Date().addingTimeInterval(timeout)
        while Date() < deadline {
            if let value = condition() { return value }
            try await Task.sleep(for: .milliseconds(50))
        }
        XCTFail("Timed out waiting for the Windows-created group.", file: file, line: line)
        throw WindowsGroupAcceptanceError.timedOut
    }
}

private enum WindowsGroupAcceptanceError: Error { case timedOut }

/// One isolated macOS runtime: identity, support directory, a raw listener that feeds
/// inbound connections into its `TransferModel`, and the auto-approval driver.
@MainActor
private final class Setup {
    let model: TransferModel
    let identity: DeviceIdentity
    let store: DeviceIdentityStore
    let listener: NWListener
    let port: UInt16
    private var autoApprovals: Task<Void, Never>?

    private init(model: TransferModel, identity: DeviceIdentity, store: DeviceIdentityStore,
                 listener: NWListener, port: UInt16) {
        self.model = model
        self.identity = identity
        self.store = store
        self.listener = listener
        self.port = port
    }

    static func make(index: Int, root: URL) async throws -> Setup {
        let support = root.appendingPathComponent("mac-\(index)", isDirectory: true)
        try FileManager.default.createDirectory(at: support, withIntermediateDirectories: true)
        let store = DeviceIdentityStore(serviceIdentifier: "com.feige.tests.windows-group.ui.\(UUID().uuidString)")
        let identity = try store.loadOrCreate()
        let model = TransferModel(supportDirectory: support,
                                  identityService: "com.feige.tests.windows-group.ui.unused",
                                  identityStore: store,
                                  identityLoader: { identity }, makeUpdates: false)
        try model.saveSettings(deviceName: "macOS group runtime \(index + 1)",
                               receiveDirectory: root.appendingPathComponent("received-\(index)", isDirectory: true))
        await model.start()
        let (listener, port) = try await makeListener(for: model, label: "\(index)")
        let setup = Setup(model: model, identity: identity, store: store, listener: listener, port: port)
        setup.startAutoApprovals()
        return setup
    }

    private static func makeListener(for model: TransferModel, label: String) async throws -> (NWListener, UInt16) {
        let listener = try NWListener(using: .tcp, on: .any)
        let (states, continuation) = AsyncStream<Result<UInt16, Error>>.makeStream()
        listener.stateUpdateHandler = { [weak listener] state in
            if case .ready = state, let port = listener?.port?.rawValue {
                continuation.yield(.success(port)); continuation.finish()
            }
            if case .failed(let error) = state {
                continuation.yield(.failure(error)); continuation.finish()
            }
        }
        listener.newConnectionHandler = { connection in Task { @MainActor in model.accept(connection) } }
        listener.start(queue: DispatchQueue(label: "feige.windows.group.ui.\(label)"))
        var iterator = states.makeAsyncIterator()
        let state = await iterator.next()
        return (listener, try XCTUnwrap(state).get())
    }

    private func startAutoApprovals() {
        autoApprovals = Task { @MainActor [weak self] in
            while !Task.isCancelled {
                guard let self else { return }
                if model.pendingConnectionPeerName != nil { model.approveConnection(true) }
                for invitation in model.pendingGroupInvitations {
                    model.acceptGroupInvitation(invitation.id, accepted: true)
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
    }

    func connect(to device: LANDevice) {
        model.requestConnection(to: device, protocolVersion: 5)
    }

    func tearDown() {
        autoApprovals?.cancel()
        listener.cancel()
        model.shutdown()
        store.delete()
    }
}
