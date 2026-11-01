import XCTest
import FeigeIdentity
@testable import FeigeMac

final class WindowsGroupProvisioningTests: XCTestCase {
    @MainActor
    func testWindowsGroupOnlyProvisioningOverLANWhenConfigured() async throws {
        guard let windowsFingerprint = ProcessInfo.processInfo.environment["FEIGE_WIN_GROUP_TEST_FINGERPRINT"],
              !windowsFingerprint.isEmpty else {
            throw XCTSkip("Set FEIGE_WIN_GROUP_TEST_FINGERPRINT to run the Windows LAN acceptance test.")
        }

        let normalizedWindowsFingerprint = Self.normalize(windowsFingerprint)
        let root = FileManager.default.temporaryDirectory
            .appendingPathComponent("FeigeWindowsGroupRuntime-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let supportDirectory = root.appendingPathComponent("support", isDirectory: true)
        try FileManager.default.createDirectory(at: supportDirectory, withIntermediateDirectories: true)

        var selectedIdentity: (DeviceIdentityStore, DeviceIdentity)?
        for _ in 0..<12 {
            let store = DeviceIdentityStore(serviceIdentifier: "com.feige.tests.windows-group.\(UUID().uuidString)")
            let identity = try store.loadOrCreate()
            if Self.normalize(identity.fingerprint) > normalizedWindowsFingerprint {
                selectedIdentity = (store, identity)
                break
            }
            store.delete()
        }
        guard let (identityStore, identity) = selectedIdentity else {
            throw XCTSkip("Could not generate a test identity that makes Windows the deterministic dialer.")
        }

        let groupID = UUID(uuidString: ProcessInfo.processInfo.environment["FEIGE_WIN_GROUP_TEST_GROUP_ID"] ?? "")
            ?? UUID(uuidString: "11111111-1111-4111-8111-111111111111")!
        print("WINDOWS_GROUP_TEST_MAC_IDENTITY|\(identity.fingerprint)|\(groupID.uuidString)")
        if let resultPath = ProcessInfo.processInfo.environment["FEIGE_WIN_GROUP_TEST_RESULT_FILE"] {
            let result = "\(identity.fingerprint)|\(groupID.uuidString)\n"
            try Data(result.utf8).write(to: URL(fileURLWithPath: resultPath), options: .atomic)
        }
        let members = try [
            GroupConversationMember(fingerprint: windowsFingerprint, displayName: "Windows runtime"),
            GroupConversationMember(fingerprint: identity.fingerprint, displayName: "macOS runtime")
        ]
        let group = try GroupConversation(id: groupID, name: "LAN provisioning acceptance",
                                          ownerFingerprint: windowsFingerprint, revision: 1, members: members)
        let archive = ConversationArchive(groups: [group])
        try JSONEncoder().encode(archive).write(to: supportDirectory.appendingPathComponent("messages.json"),
                                                 options: .atomic)

        let model = TransferModel(supportDirectory: supportDirectory,
                                  identityService: "com.feige.tests.windows-group.unused",
                                  identityStore: identityStore,
                                  identityLoader: { identity }, makeUpdates: false)
        try model.saveSettings(deviceName: "Feige macOS group runtime",
                               receiveDirectory: root.appendingPathComponent("received", isDirectory: true))
        let approvals = Task { @MainActor in
            while !Task.isCancelled {
                if model.pendingConnectionPeerName != nil { model.approveConnection(true) }
                do { try await Task.sleep(for: .milliseconds(10)) } catch { return }
            }
        }
        defer {
            approvals.cancel()
            model.shutdown()
            identityStore.delete()
            try? FileManager.default.removeItem(at: root)
        }

        await model.start()
        try? await Task.sleep(for: .seconds(3))
        print("WINDOWS_GROUP_TEST_STATE|identityReady=\(model.isIdentityReady)|discovery=\(model.discoveryState)|nearby=\(model.nearbyDevices.map(\.serviceName))")
        try await Self.waitUntil(timeout: 180) { model.isConnected(to: windowsFingerprint) }
        guard model.isConnected(to: windowsFingerprint) else { return }
        let sent = model.sendGroupText("windows-group-runtime-ack", groupID: groupID)
        XCTAssertTrue(sent)
        guard sent else { return }
        try await Self.waitUntil(timeout: 30) {
            guard let delivery = model.groupDeliveries.last(where: { $0.groupID == groupID }) else { return false }
            return delivery.recipients.count == 1 && delivery.recipients[0].status == .delivered
        }

        if ProcessInfo.processInfo.environment["FEIGE_WIN_GROUP_TEST_REQUEST_LEAVE"] == "1" {
            let expectRejection = ProcessInfo.processInfo.environment["FEIGE_WIN_GROUP_TEST_EXPECT_LEAVE_REJECTION"] == "1"
            model.requestLeaveGroup(groupID)
            if expectRejection {
                try await Self.waitUntil(timeout: 60) { model.errorMessage == "群主暂未批准退出申请。" }
                XCTAssertEqual(model.groups.first(where: { $0.id == groupID })?.members.count, 2)
            } else {
                try await Self.waitUntil(timeout: 60) {
                    model.groups.first(where: { $0.id == groupID })?.members.count == 1
                }
                XCTAssertEqual(model.groups.first(where: { $0.id == groupID })?.members.map(\.fingerprint),
                               [windowsFingerprint])
            }
        }
    }

    private static func normalize(_ fingerprint: String) -> String {
        fingerprint.filter { !$0.isWhitespace }.uppercased()
    }

    @MainActor
    private static func waitUntil(timeout: TimeInterval,
                                  condition: @MainActor () -> Bool,
                                  file: StaticString = #filePath,
                                  line: UInt = #line) async throws {
        let deadline = Date().addingTimeInterval(timeout)
        while !condition(), Date() < deadline {
            try await Task.sleep(for: .milliseconds(25))
        }
        XCTAssertTrue(condition(), "Timed out waiting for Windows group-only connection or message ACK.",
                      file: file, line: line)
    }
}
