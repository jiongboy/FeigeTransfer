import XCTest
import FeigeIdentity
import Network
@testable import FeigeMac

final class GroupConversationTransferTests: XCTestCase {
    @MainActor
    func testV4GroupTextAndMemberLeaveCommit() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let ownerStore = DeviceIdentityStore(serviceIdentifier: "com.feige.group.owner.\(UUID())")
        let memberStore = DeviceIdentityStore(serviceIdentifier: "com.feige.group.member.\(UUID())")
        let meshMemberStore = DeviceIdentityStore(serviceIdentifier: "com.feige.group.mesh-member.\(UUID())")
        defer {
            try? FileManager.default.removeItem(at: root)
            ownerStore.delete()
            memberStore.delete()
            meshMemberStore.delete()
        }

        let ownerLoader = GroupIdentityLoader(store: ownerStore)
        let memberLoader = GroupIdentityLoader(store: memberStore)
        let meshMemberLoader = GroupIdentityLoader(store: meshMemberStore)
        let owner = TransferModel(supportDirectory: root.appendingPathComponent("owner"), identityStore: ownerStore,
                                  identityLoader: { try ownerLoader.load() })
        let member = TransferModel(supportDirectory: root.appendingPathComponent("member"), identityStore: memberStore,
                                   identityLoader: { try memberLoader.load() })
        let meshMember = TransferModel(supportDirectory: root.appendingPathComponent("mesh-member"),
                                       identityStore: meshMemberStore,
                                       identityLoader: { try meshMemberLoader.load() })
        try owner.saveSettings(deviceName: "群主", receiveDirectory: root.appendingPathComponent("owner-files"))
        try member.saveSettings(deviceName: "成员", receiveDirectory: root.appendingPathComponent("member-files"))
        try meshMember.saveSettings(deviceName: "第二成员", receiveDirectory: root.appendingPathComponent("mesh-member-files"))
        await owner.start()
        await member.start()
        await meshMember.start()

        let (memberListener, memberPort) = try await makeListener(for: member, label: "member")
        let (meshListener, meshPort) = try await makeListener(for: meshMember, label: "mesh-member")
        defer {
            memberListener.cancel()
            meshListener.cancel()
            owner.shutdown()
            member.shutdown()
            meshMember.shutdown()
        }
        var memberDevice = try owner.manualDevice(host: "127.0.0.1", port: String(memberPort))
        memberDevice = LANDevice(serviceName: "test-member", displayName: "成员", endpoint: memberDevice.endpoint,
                                 protocolVersions: [3, 4])
        var meshDevice = try owner.manualDevice(host: "127.0.0.1", port: String(meshPort))
        meshDevice = LANDevice(serviceName: "test-mesh-member", displayName: "第二成员", endpoint: meshDevice.endpoint,
                               protocolVersions: [3, 4])
        let memberMeshDevice = LANDevice(serviceName: serviceName(for: meshMember.identityFingerprint),
            displayName: "第二成员", endpoint: meshDevice.endpoint, protocolVersions: [4])
        let meshPeerDevice = LANDevice(serviceName: serviceName(for: member.identityFingerprint),
            displayName: "成员", endpoint: memberDevice.endpoint, protocolVersions: [4])

        let rejectNextMeshGroupFile = GroupFileApprovalPolicy()
        let approvals = Task { @MainActor in
            while !Task.isCancelled {
                member.nearbyDevices = [memberMeshDevice]
                meshMember.nearbyDevices = [meshPeerDevice]
                if member.pendingConnectionPeerName != nil { member.approveConnection(true) }
                if meshMember.pendingConnectionPeerName != nil { meshMember.approveConnection(true) }
                for approval in member.pendingGroupFileApprovals {
                    member.decideGroupFile(approval.manifest.metadata.transferID,
                                          peerID: approval.manifest.senderFingerprint, accepted: true)
                }
                for approval in meshMember.pendingGroupFileApprovals {
                    let shouldAccept = !rejectNextMeshGroupFile.rejectNext
                    rejectNextMeshGroupFile.rejectNext = false
                    meshMember.decideGroupFile(approval.manifest.metadata.transferID,
                                               peerID: approval.manifest.senderFingerprint, accepted: shouldAccept)
                }
                for invitation in member.pendingGroupInvitations {
                    member.acceptGroupInvitation(invitation.id, accepted: true)
                }
                for invitation in meshMember.pendingGroupInvitations {
                    meshMember.acceptGroupInvitation(invitation.id, accepted: true)
                }
                if let request = owner.pendingGroupLeaveRequests.first {
                    owner.resolveGroupLeaveRequest(request.id, accepted: true)
                }
                do { try await Task.sleep(for: .milliseconds(10)) } catch { return }
            }
        }
        defer { approvals.cancel() }

        owner.requestConnection(to: memberDevice, protocolVersion: 4)
        owner.requestConnection(to: meshDevice, protocolVersion: 4)
        try await waitUntil {
            owner.isConnected(to: member.identityFingerprint) && member.isConnected(to: owner.identityFingerprint) &&
            owner.isConnected(to: meshMember.identityFingerprint) && meshMember.isConnected(to: owner.identityFingerprint)
        }

        let groupID = try await owner.createGroup(name: "验收群", memberPeerIDs: [member.identityFingerprint,
                                                                                 meshMember.identityFingerprint])
        try await waitUntil {
            owner.groups.first(where: { $0.id == groupID })?.revision == 0 &&
            member.groups.first(where: { $0.id == groupID })?.revision == 0 &&
            meshMember.groups.first(where: { $0.id == groupID })?.revision == 0 &&
            member.isConnected(to: meshMember.identityFingerprint) && meshMember.isConnected(to: member.identityFingerprint)
        }

        XCTAssertTrue(owner.sendGroupText("群消息回归", groupID: groupID))
        try await waitUntil {
            guard let messageID = owner.groupMessages.first(where: {
                $0.groupID == groupID && $0.text == "群消息回归"
            })?.id else { return false }
            return member.groupMessages.contains(where: { $0.groupID == groupID && $0.id == messageID }) &&
                meshMember.groupMessages.contains(where: { $0.groupID == groupID && $0.id == messageID }) &&
                owner.groupDeliveries.first(where: { $0.groupID == groupID && $0.id == messageID })?.deliveredCount == 2
        }

        let fileURL = root.appendingPathComponent("group-fixture.bin")
        let fileContents = Data((0..<65_536).map { UInt8($0 % 251) })
        try fileContents.write(to: fileURL)
        owner.startSendingGroupFiles([fileURL], groupID: groupID)
        try await waitUntil {
            guard let envelope = owner.groupMessages.first(where: {
                $0.groupID == groupID && $0.fileMetadata?.fileName == fileURL.lastPathComponent
            }), let delivery = owner.groupDeliveries.first(where: { $0.groupID == groupID && $0.id == envelope.id }),
                  let memberFile = member.groupMessages.first(where: { $0.groupID == groupID && $0.id == envelope.id }),
                  let meshFile = meshMember.groupMessages.first(where: { $0.groupID == groupID && $0.id == envelope.id }),
                  let memberTransferID = memberFile.fileMetadata?.transferID,
                  let meshTransferID = meshFile.fileMetadata?.transferID,
                  let memberPath = member.groupFileLocations[memberTransferID],
                  let meshPath = meshMember.groupFileLocations[meshTransferID] else { return false }
            return delivery.deliveredCount == 2 &&
                delivery.recipients.allSatisfy { $0.status == .delivered } &&
                memberTransferID != meshTransferID &&
                (try? Data(contentsOf: URL(fileURLWithPath: memberPath))) == fileContents &&
                (try? Data(contentsOf: URL(fileURLWithPath: meshPath))) == fileContents
        }

        let rejectedFileURL = root.appendingPathComponent("group-rejected-for-one-member.bin")
        let rejectedFileContents = Data((0..<32_768).map { UInt8(($0 * 7) % 253) })
        try rejectedFileContents.write(to: rejectedFileURL)
        rejectNextMeshGroupFile.rejectNext = true
        owner.startSendingGroupFiles([rejectedFileURL], groupID: groupID)
        try await waitUntil {
            guard let envelope = owner.groupMessages.first(where: {
                $0.groupID == groupID && $0.fileMetadata?.fileName == rejectedFileURL.lastPathComponent
            }), let delivery = owner.groupDeliveries.first(where: { $0.groupID == groupID && $0.id == envelope.id }),
                  let deliveredMember = delivery.recipients.first(where: { $0.memberFingerprint == member.identityFingerprint }),
                  let declinedMember = delivery.recipients.first(where: { $0.memberFingerprint == meshMember.identityFingerprint }),
                  let memberFile = member.groupMessages.first(where: { $0.groupID == groupID && $0.id == envelope.id }),
                  let memberTransferID = memberFile.fileMetadata?.transferID,
                  let memberPath = member.groupFileLocations[memberTransferID] else { return false }
            return delivery.isComplete && delivery.deliveredCount == 1 &&
                deliveredMember.status == .delivered && declinedMember.status == .declined &&
                !meshMember.groupMessages.contains(where: { $0.groupID == groupID && $0.id == envelope.id }) &&
                (try? Data(contentsOf: URL(fileURLWithPath: memberPath))) == rejectedFileContents
        }

        member.requestLeaveGroup(groupID)
        try await waitUntil {
            owner.groups.first(where: { $0.id == groupID })?.revision == 1 &&
            member.groups.first(where: { $0.id == groupID })?.revision == 1 &&
            meshMember.groups.first(where: { $0.id == groupID })?.revision == 1
        }
        let ownerRoster = try XCTUnwrap(owner.groups.first(where: { $0.id == groupID }))
        let memberRoster = try XCTUnwrap(member.groups.first(where: { $0.id == groupID }))
        let meshRoster = try XCTUnwrap(meshMember.groups.first(where: { $0.id == groupID }))
        XCTAssertEqual(Set(ownerRoster.members.map(\.fingerprint)), Set([owner.identityFingerprint, meshMember.identityFingerprint]))
        XCTAssertFalse(memberRoster.members.contains(where: { $0.fingerprint == member.identityFingerprint }))
        XCTAssertEqual(Set(meshRoster.members.map(\.fingerprint)), Set([owner.identityFingerprint, meshMember.identityFingerprint]))
        XCTAssertFalse(member.canSendGroupText(groupID))

        try await owner.leaveGroupAsOwner(groupID)
        try await waitUntil {
            guard let ownerGroup = owner.groups.first(where: { $0.id == groupID }),
                  let meshGroup = meshMember.groups.first(where: { $0.id == groupID }) else { return false }
            return ownerGroup.revision == 2 && meshGroup.revision == 2 &&
                ownerGroup.ownerFingerprint == meshMember.identityFingerprint &&
                meshGroup.ownerFingerprint == meshMember.identityFingerprint &&
                !ownerGroup.members.contains(where: { $0.fingerprint == owner.identityFingerprint }) &&
                !owner.canSendGroupText(groupID) && meshMember.canSendGroupText(groupID)
        }
        let transferredGroup = try XCTUnwrap(meshMember.groups.first(where: { $0.id == groupID }))
        XCTAssertEqual(transferredGroup.previousOwnerFingerprint, owner.identityFingerprint)
        XCTAssertEqual(transferredGroup.members.map(\.fingerprint), [meshMember.identityFingerprint])

        let retainedHistory = meshMember.groupMessages.filter { $0.groupID == groupID }
        try await meshMember.leaveGroupAsOwner(groupID)
        XCTAssertFalse(meshMember.groups.contains(where: { $0.id == groupID }))
        XCTAssertEqual(meshMember.groupMessages.filter { $0.groupID == groupID }, retainedHistory)
        XCTAssertFalse(meshMember.canSendGroupText(groupID))
    }

    @MainActor
    func testPendingGroupPreparationReplaysAfterOwnerRestart() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let ownerStore = DeviceIdentityStore(serviceIdentifier: "com.feige.group.prepare-recovery.owner.\(UUID())")
        let memberStore = DeviceIdentityStore(serviceIdentifier: "com.feige.group.prepare-recovery.member.\(UUID())")
        defer {
            try? FileManager.default.removeItem(at: root)
            ownerStore.delete()
            memberStore.delete()
        }
        let ownerLoader = GroupIdentityLoader(store: ownerStore)
        let memberLoader = GroupIdentityLoader(store: memberStore)
        let ownerSupport = root.appendingPathComponent("owner")
        let memberSupport = root.appendingPathComponent("member")
        let owner = TransferModel(supportDirectory: ownerSupport, identityStore: ownerStore,
                                  identityLoader: { try ownerLoader.load() })
        let memberModel = TransferModel(supportDirectory: memberSupport, identityStore: memberStore,
                                        identityLoader: { try memberLoader.load() })
        try owner.saveSettings(deviceName: "群主", receiveDirectory: root.appendingPathComponent("owner-files"))
        try memberModel.saveSettings(deviceName: "成员", receiveDirectory: root.appendingPathComponent("member-files"))
        await owner.start()
        await memberModel.start()
        let (listener, port) = try await makeListener(for: memberModel, label: "prepare-recovery-member")
        defer {
            listener.cancel()
            owner.shutdown()
            memberModel.shutdown()
        }

        let invitationPolicy = GroupFileApprovalPolicy()
        invitationPolicy.rejectNext = true
        let approvals = Task { @MainActor in
            while !Task.isCancelled {
                if memberModel.pendingConnectionPeerName != nil { memberModel.approveConnection(true) }
                for invitation in memberModel.pendingGroupInvitations {
                    let shouldAccept = !invitationPolicy.rejectNext
                    invitationPolicy.rejectNext = false
                    memberModel.acceptGroupInvitation(invitation.id, accepted: shouldAccept)
                }
                do { try await Task.sleep(for: .milliseconds(10)) } catch { return }
            }
        }
        defer { approvals.cancel() }

        var device = try owner.manualDevice(host: "127.0.0.1", port: String(port))
        device = LANDevice(serviceName: serviceName(for: memberModel.identityFingerprint), displayName: "成员",
                           endpoint: device.endpoint, protocolVersions: [4])
        owner.requestConnection(to: device, protocolVersion: 4)
        try await waitUntil {
            owner.isConnected(to: memberModel.identityFingerprint) &&
            memberModel.isConnected(to: owner.identityFingerprint)
        }

        do {
            _ = try await owner.createGroup(name: "待恢复群", memberPeerIDs: [memberModel.identityFingerprint])
            XCTFail("declined invitation must leave a durable pre-commit transaction")
        } catch { }
        let failedAttempt = try ConversationArchive.decode(Data(contentsOf: ownerSupport.appendingPathComponent("messages.json")))
        XCTAssertEqual(failedAttempt.pendingGroupPreparations.count, 1)
        let pending = try XCTUnwrap(failedAttempt.pendingGroupPreparations.first)
        let groupID = pending.group.id
        XCTAssertEqual(pending.invitationTargets.count, 1)
        XCTAssertEqual(pending.prepareTargets.count, 1)

        owner.shutdown()
        let resumedOwner = TransferModel(supportDirectory: ownerSupport, identityStore: ownerStore,
                                         identityLoader: { try ownerLoader.load() })
        defer { resumedOwner.shutdown() }
        try resumedOwner.saveSettings(deviceName: "群主", receiveDirectory: root.appendingPathComponent("owner-files"))
        await resumedOwner.start()
        let resumedDevice = LANDevice(serviceName: serviceName(for: memberModel.identityFingerprint), displayName: "成员",
                                      endpoint: device.endpoint, protocolVersions: [4])
        resumedOwner.nearbyDevices = [resumedDevice]
        try await waitUntil {
            resumedOwner.groups.first(where: { $0.id == groupID })?.revision == 0 &&
            memberModel.groups.first(where: { $0.id == groupID })?.revision == 0
        }
        let recovered = try ConversationArchive.decode(Data(contentsOf: ownerSupport.appendingPathComponent("messages.json")))
        XCTAssertTrue(recovered.pendingGroupPreparations.isEmpty)
        XCTAssertTrue(recovered.pendingGroupCommits.isEmpty)
        XCTAssertEqual(recovered.groups.first(where: { $0.id == groupID }), pending.group)
        resumedOwner.shutdown()
    }

    @MainActor
    func testV5CancellationPersistsIntentAndRecipientTombstone() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let ownerStore = DeviceIdentityStore(serviceIdentifier: "com.feige.group.abort.owner.\(UUID())")
        let memberStore = DeviceIdentityStore(serviceIdentifier: "com.feige.group.abort.member.\(UUID())")
        defer {
            try? FileManager.default.removeItem(at: root)
            ownerStore.delete()
            memberStore.delete()
        }
        let ownerLoader = GroupIdentityLoader(store: ownerStore)
        let memberLoader = GroupIdentityLoader(store: memberStore)
        let ownerSupport = root.appendingPathComponent("owner")
        let memberSupport = root.appendingPathComponent("member")
        let owner = TransferModel(supportDirectory: ownerSupport, identityStore: ownerStore,
                                  identityLoader: { try ownerLoader.load() })
        let member = TransferModel(supportDirectory: memberSupport, identityStore: memberStore,
                                   identityLoader: { try memberLoader.load() })
        try owner.saveSettings(deviceName: "群主", receiveDirectory: root.appendingPathComponent("owner-files"))
        try member.saveSettings(deviceName: "成员", receiveDirectory: root.appendingPathComponent("member-files"))
        await owner.start()
        await member.start()
        let (listener, port) = try await makeListener(for: member, label: "v5-abort-member")
        defer {
            listener.cancel()
            owner.shutdown()
            member.shutdown()
        }

        let rejectNextInvitation = GroupFileApprovalPolicy()
        rejectNextInvitation.rejectNext = true
        let approvals = Task { @MainActor in
            while !Task.isCancelled {
                if member.pendingConnectionPeerName != nil { member.approveConnection(true) }
                for invitation in member.pendingGroupInvitations {
                    let accepted = !rejectNextInvitation.rejectNext
                    rejectNextInvitation.rejectNext = false
                    member.acceptGroupInvitation(invitation.id, accepted: accepted)
                }
                do { try await Task.sleep(for: .milliseconds(10)) } catch { return }
            }
        }
        defer { approvals.cancel() }

        var device = try owner.manualDevice(host: "127.0.0.1", port: String(port))
        device = LANDevice(serviceName: serviceName(for: member.identityFingerprint), displayName: "成员",
                           endpoint: device.endpoint, protocolVersions: [4, 5])
        owner.requestConnection(to: device, protocolVersion: 5)
        try await waitUntil {
            owner.isConnected(to: member.identityFingerprint) &&
            member.isConnected(to: owner.identityFingerprint)
        }

        do {
            _ = try await owner.createGroup(name: "待取消群", memberPeerIDs: [member.identityFingerprint])
            XCTFail("declined invitation must leave a cancellable pre-commit transaction")
        } catch { }
        let pending = try ConversationArchive.decode(Data(contentsOf: ownerSupport.appendingPathComponent("messages.json")))
        let groupID = try XCTUnwrap(pending.pendingGroupPreparations.first?.group.id)
        owner.disconnect(peerID: member.identityFingerprint)
        try await waitUntil {
            !owner.isConnected(to: member.identityFingerprint) && !member.isConnected(to: owner.identityFingerprint)
        }
        let cancellationCompleted = try await owner.cancelPendingGroupPreparation(groupID)
        XCTAssertFalse(cancellationCompleted)
        XCTAssertNil(owner.pendingGroupPreparations[groupID])
        XCTAssertNotNil(owner.pendingGroupAborts[groupID])
        XCTAssertNil(owner.pendingGroupCommits[groupID])
        let waitingArchive = try ConversationArchive.decode(Data(contentsOf: ownerSupport.appendingPathComponent("messages.json")))
        XCTAssertEqual(waitingArchive.pendingGroupAborts.count, 1)
        XCTAssertTrue(waitingArchive.pendingGroupAborts[0].acknowledgedPeers.isEmpty)

        owner.requestConnection(to: device, protocolVersion: 5)
        try await waitUntil {
            owner.isConnected(to: member.identityFingerprint) &&
            owner.pendingGroupAborts[groupID] == nil
        }
        XCTAssertFalse(owner.groups.contains { $0.id == groupID })
        XCTAssertFalse(member.groups.contains { $0.id == groupID })

        let ownerArchive = try ConversationArchive.decode(Data(contentsOf: ownerSupport.appendingPathComponent("messages.json")))
        let memberArchive = try ConversationArchive.decode(Data(contentsOf: memberSupport.appendingPathComponent("messages.json")))
        XCTAssertTrue(ownerArchive.pendingGroupAborts.isEmpty)
        XCTAssertEqual(ownerArchive.groupAbortTombstones.count, 1)
        XCTAssertEqual(memberArchive.groupAbortTombstones.count, 1)
        XCTAssertTrue(ownerArchive.groupAbortTombstones[0].accepted)
        XCTAssertTrue(memberArchive.groupAbortTombstones[0].accepted)
        XCTAssertEqual(ownerArchive.groupAbortTombstones[0].abort.groupID, groupID)
        XCTAssertEqual(memberArchive.groupAbortTombstones[0].abort.targetFingerprint, member.identityFingerprint)
    }

    @MainActor
    func testPendingGroupCommitReplaysAfterOwnerRestart() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let ownerStore = DeviceIdentityStore(serviceIdentifier: "com.feige.group.recovery.owner.\(UUID())")
        let memberStore = DeviceIdentityStore(serviceIdentifier: "com.feige.group.recovery.member.\(UUID())")
        defer {
            try? FileManager.default.removeItem(at: root)
            ownerStore.delete()
            memberStore.delete()
        }
        let ownerLoader = GroupIdentityLoader(store: ownerStore)
        let memberLoader = GroupIdentityLoader(store: memberStore)
        let ownerIdentity = try ownerLoader.load()
        let memberIdentity = try memberLoader.load()
        let ownerMember = try GroupConversationMember(fingerprint: ownerIdentity.fingerprint, displayName: "群主")
        let member = try GroupConversationMember(fingerprint: memberIdentity.fingerprint, displayName: "成员")
        let groupID = UUID()
        let previous = try GroupConversation(id: groupID, name: "恢复群", ownerFingerprint: ownerMember.fingerprint,
                                             revision: 0, members: [ownerMember, member])
        let proposed = try GroupConversation(id: groupID, name: "恢复群",
            ownerFingerprint: member.fingerprint, revision: 1, members: [member],
            previousOwnerFingerprint: ownerMember.fingerprint)
        let eventID = UUID()
        let commit = try GroupControlMessage(kind: .membershipCommit, eventID: eventID, groupID: groupID,
            ownerFingerprint: ownerMember.fingerprint, epoch: 1, baseEpoch: 0,
            targetFingerprint: member.fingerprint, newOwnerFingerprint: member.fingerprint,
            name: proposed.name, roster: proposed.members)
        let pending = PendingGroupCommit(group: proposed,
            targets: [GroupCommitTarget(peerID: member.fingerprint, message: commit)])
        let ownerSupport = root.appendingPathComponent("owner")
        let memberSupport = root.appendingPathComponent("member")
        try FileManager.default.createDirectory(at: ownerSupport, withIntermediateDirectories: true)
        try FileManager.default.createDirectory(at: memberSupport, withIntermediateDirectories: true)
        try JSONEncoder().encode(ConversationArchive(groups: [previous], pendingGroupCommits: [pending]))
            .write(to: ownerSupport.appendingPathComponent("messages.json"))
        try JSONEncoder().encode(ConversationArchive(groups: [proposed]))
            .write(to: memberSupport.appendingPathComponent("messages.json"))

        let owner = TransferModel(supportDirectory: ownerSupport, identityStore: ownerStore,
                                  identityLoader: { try ownerLoader.load() })
        let memberModel = TransferModel(supportDirectory: memberSupport, identityStore: memberStore,
                                        identityLoader: { try memberLoader.load() })
        try owner.saveSettings(deviceName: "群主", receiveDirectory: root.appendingPathComponent("owner-files"))
        try memberModel.saveSettings(deviceName: "成员", receiveDirectory: root.appendingPathComponent("member-files"))
        await memberModel.start()
        let (listener, port) = try await makeListener(for: memberModel, label: "recovery-member")
        defer {
            listener.cancel()
            owner.shutdown()
            memberModel.shutdown()
        }
        let approvals = Task { @MainActor in
            while !Task.isCancelled {
                if memberModel.pendingConnectionPeerName != nil { memberModel.approveConnection(true) }
                do { try await Task.sleep(for: .milliseconds(10)) } catch { return }
            }
        }
        defer { approvals.cancel() }

        var device = try owner.manualDevice(host: "127.0.0.1", port: String(port))
        device = LANDevice(serviceName: serviceName(for: memberIdentity.fingerprint), displayName: "成员",
                           endpoint: device.endpoint, protocolVersions: [4])
        await owner.start()
        owner.nearbyDevices = [device]
        try await waitUntil {
            owner.groups.first(where: { $0.id == groupID })?.revision == 1 &&
            owner.groups.first(where: { $0.id == groupID })?.ownerFingerprint == memberIdentity.fingerprint &&
            memberModel.groups.first(where: { $0.id == groupID })?.revision == 1
        }
        let restoredOwnerArchive = try ConversationArchive.decode(Data(contentsOf: ownerSupport.appendingPathComponent("messages.json")))
        let restoredMemberArchive = try ConversationArchive.decode(Data(contentsOf: memberSupport.appendingPathComponent("messages.json")))
        XCTAssertTrue(restoredOwnerArchive.pendingGroupCommits.isEmpty)
        XCTAssertEqual(restoredOwnerArchive.groups.first(where: { $0.id == groupID }), proposed)
        XCTAssertEqual(restoredMemberArchive.groups.first(where: { $0.id == groupID }), proposed)
        XCTAssertTrue(restoredMemberArchive.pendingGroupMembershipChanges.isEmpty)
    }

    /// A V5 cancellation issued while the peer is offline must survive a real process restart:
    /// the owner reloads its durable intent and tombstone from disk, resends the abort once the
    /// recipient reconnects, and neither side ever commits the cancelled roster.
    @MainActor
    func testV5CancellationResumesAfterOwnerRestartAndRecipientTombstoneSurvives() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let ownerStore = DeviceIdentityStore(serviceIdentifier: "com.feige.group.abort-restart.owner.\(UUID())")
        let memberStore = DeviceIdentityStore(serviceIdentifier: "com.feige.group.abort-restart.member.\(UUID())")
        defer {
            try? FileManager.default.removeItem(at: root)
            ownerStore.delete()
            memberStore.delete()
        }
        let ownerLoader = GroupIdentityLoader(store: ownerStore)
        let memberLoader = GroupIdentityLoader(store: memberStore)
        let ownerSupport = root.appendingPathComponent("owner")
        let memberSupport = root.appendingPathComponent("member")
        let ownerFiles = root.appendingPathComponent("owner-files")
        let memberFiles = root.appendingPathComponent("member-files")
        let owner = TransferModel(supportDirectory: ownerSupport, identityStore: ownerStore,
                                  identityLoader: { try ownerLoader.load() })
        let member = TransferModel(supportDirectory: memberSupport, identityStore: memberStore,
                                   identityLoader: { try memberLoader.load() })
        try owner.saveSettings(deviceName: "群主", receiveDirectory: ownerFiles)
        try member.saveSettings(deviceName: "成员", receiveDirectory: memberFiles)
        await owner.start()
        await member.start()
        let (listener, port) = try await makeListener(for: member, label: "v5-abort-restart-member")
        defer {
            listener.cancel()
            owner.shutdown()
            member.shutdown()
        }

        let rejectNextInvitation = GroupFileApprovalPolicy()
        rejectNextInvitation.rejectNext = true
        let approvals = Task { @MainActor in
            while !Task.isCancelled {
                if member.pendingConnectionPeerName != nil { member.approveConnection(true) }
                for invitation in member.pendingGroupInvitations {
                    let accepted = !rejectNextInvitation.rejectNext
                    rejectNextInvitation.rejectNext = false
                    member.acceptGroupInvitation(invitation.id, accepted: accepted)
                }
                do { try await Task.sleep(for: .milliseconds(10)) } catch { return }
            }
        }
        defer { approvals.cancel() }

        var device = try owner.manualDevice(host: "127.0.0.1", port: String(port))
        device = LANDevice(serviceName: serviceName(for: member.identityFingerprint), displayName: "成员",
                           endpoint: device.endpoint, protocolVersions: [4, 5])
        owner.requestConnection(to: device, protocolVersion: 5)
        try await waitUntil {
            owner.isConnected(to: member.identityFingerprint) &&
            member.isConnected(to: owner.identityFingerprint)
        }

        do {
            _ = try await owner.createGroup(name: "重启取消群", memberPeerIDs: [member.identityFingerprint])
            XCTFail("declined invitation must leave a cancellable pre-commit transaction")
        } catch { }
        let pending = try ConversationArchive.decode(Data(contentsOf: ownerSupport.appendingPathComponent("messages.json")))
        let groupID = try XCTUnwrap(pending.pendingGroupPreparations.first?.group.id)
        owner.disconnect(peerID: member.identityFingerprint)
        try await waitUntil {
            !owner.isConnected(to: member.identityFingerprint) && !member.isConnected(to: owner.identityFingerprint)
        }
        let cancellationCompleted = try await owner.cancelPendingGroupPreparation(groupID)
        XCTAssertFalse(cancellationCompleted, "The abort cannot complete while the recipient is offline.")
        XCTAssertNotNil(owner.pendingGroupAborts[groupID])

        // Stop the owner entirely; only the on-disk intent and tombstone may carry the cancellation forward.
        owner.shutdown()
        let savedBeforeRestart = try ConversationArchive.decode(Data(contentsOf: ownerSupport.appendingPathComponent("messages.json")))
        XCTAssertEqual(savedBeforeRestart.pendingGroupAborts.count, 1)
        XCTAssertTrue(savedBeforeRestart.pendingGroupAborts[0].acknowledgedPeers.isEmpty)
        XCTAssertTrue(savedBeforeRestart.pendingGroupPreparations.isEmpty)
        XCTAssertFalse(savedBeforeRestart.groups.contains { $0.id == groupID })

        let restartedOwner = TransferModel(supportDirectory: ownerSupport, identityStore: ownerStore,
                                           identityLoader: { try ownerLoader.load() })
        defer { restartedOwner.shutdown() }
        try restartedOwner.saveSettings(deviceName: "群主", receiveDirectory: ownerFiles)
        await restartedOwner.start()
        let restartedArchive = try ConversationArchive.decode(Data(contentsOf: ownerSupport.appendingPathComponent("messages.json")))
        XCTAssertEqual(restartedArchive.pendingGroupAborts.count, 1, "The reloaded model must restore the durable abort.")
        XCTAssertFalse(restartedOwner.groups.contains { $0.id == groupID })

        let reconnected = LANDevice(serviceName: serviceName(for: member.identityFingerprint), displayName: "成员",
                                    endpoint: device.endpoint, protocolVersions: [4, 5])
        restartedOwner.nearbyDevices = [reconnected]
        restartedOwner.requestConnection(to: reconnected, protocolVersion: 5)
        try await waitUntil {
            restartedOwner.isConnected(to: member.identityFingerprint) &&
            restartedOwner.pendingGroupAborts[groupID] == nil &&
            member.isConnected(to: owner.identityFingerprint)
        }
        XCTAssertFalse(restartedOwner.groups.contains { $0.id == groupID })
        XCTAssertFalse(member.groups.contains { $0.id == groupID })

        let ownerArchive = try ConversationArchive.decode(Data(contentsOf: ownerSupport.appendingPathComponent("messages.json")))
        let memberArchive = try ConversationArchive.decode(Data(contentsOf: memberSupport.appendingPathComponent("messages.json")))
        XCTAssertTrue(ownerArchive.pendingGroupAborts.isEmpty)
        XCTAssertTrue(ownerArchive.pendingGroupPreparations.isEmpty)
        XCTAssertTrue(ownerArchive.pendingGroupCommits.isEmpty)
        XCTAssertEqual(ownerArchive.groupAbortTombstones.count, 1)
        XCTAssertEqual(memberArchive.groupAbortTombstones.count, 1)
        XCTAssertTrue(ownerArchive.groupAbortTombstones[0].accepted)
        XCTAssertTrue(memberArchive.groupAbortTombstones[0].accepted)
        XCTAssertEqual(memberArchive.groupAbortTombstones[0].abort.groupID, groupID)

        // A restarted recipient keeps its tombstone, so a replayed prepare of the same epoch is still refused.
        member.shutdown()
        let restartedMember = TransferModel(supportDirectory: memberSupport, identityStore: memberStore,
                                            identityLoader: { try memberLoader.load() })
        defer { restartedMember.shutdown() }
        try restartedMember.saveSettings(deviceName: "成员", receiveDirectory: memberFiles)
        await restartedMember.start()
        let memberRestart = try ConversationArchive.decode(Data(contentsOf: memberSupport.appendingPathComponent("messages.json")))
        XCTAssertEqual(memberRestart.groupAbortTombstones.count, 1)
        XCTAssertFalse(restartedMember.groups.contains { $0.id == groupID })
    }

    /// Mirrors the Windows groups page sequence (create with one member, add a second, remove it
    /// again) through the public model API, and checks every member sees the same ordered roster.
    @MainActor
    func testGroupCreateAddMemberThenRemoveMemberKeepsRosterOrderedOnEverySide() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let ownerStore = DeviceIdentityStore(serviceIdentifier: "com.feige.group.roster.owner.\(UUID())")
        let firstStore = DeviceIdentityStore(serviceIdentifier: "com.feige.group.roster.first.\(UUID())")
        let secondStore = DeviceIdentityStore(serviceIdentifier: "com.feige.group.roster.second.\(UUID())")
        defer {
            try? FileManager.default.removeItem(at: root)
            ownerStore.delete(); firstStore.delete(); secondStore.delete()
        }
        let ownerLoader = GroupIdentityLoader(store: ownerStore)
        let firstLoader = GroupIdentityLoader(store: firstStore)
        let secondLoader = GroupIdentityLoader(store: secondStore)
        let owner = TransferModel(supportDirectory: root.appendingPathComponent("owner"), identityStore: ownerStore,
                                  identityLoader: { try ownerLoader.load() })
        let first = TransferModel(supportDirectory: root.appendingPathComponent("first"), identityStore: firstStore,
                                  identityLoader: { try firstLoader.load() })
        let second = TransferModel(supportDirectory: root.appendingPathComponent("second"), identityStore: secondStore,
                                   identityLoader: { try secondLoader.load() })
        for (model, name) in [(owner, "群主"), (first, "成员一"), (second, "成员二")] {
            try model.saveSettings(deviceName: name, receiveDirectory: root.appendingPathComponent(name))
        }
        await owner.start(); await first.start(); await second.start()
        let (firstListener, firstPort) = try await makeListener(for: first, label: "roster-first")
        let (secondListener, secondPort) = try await makeListener(for: second, label: "roster-second")
        defer {
            firstListener.cancel(); secondListener.cancel()
            owner.shutdown(); first.shutdown(); second.shutdown()
        }
        var firstDevice = try owner.manualDevice(host: "127.0.0.1", port: String(firstPort))
        firstDevice = LANDevice(serviceName: serviceName(for: first.identityFingerprint), displayName: "成员一",
                                endpoint: firstDevice.endpoint, protocolVersions: [4])
        var secondDevice = try owner.manualDevice(host: "127.0.0.1", port: String(secondPort))
        secondDevice = LANDevice(serviceName: serviceName(for: second.identityFingerprint), displayName: "成员二",
                                 endpoint: secondDevice.endpoint, protocolVersions: [4])
        let approvals = Task { @MainActor in
            while !Task.isCancelled {
                if first.pendingConnectionPeerName != nil { first.approveConnection(true) }
                if second.pendingConnectionPeerName != nil { second.approveConnection(true) }
                for invitation in first.pendingGroupInvitations { first.acceptGroupInvitation(invitation.id, accepted: true) }
                for invitation in second.pendingGroupInvitations { second.acceptGroupInvitation(invitation.id, accepted: true) }
                do { try await Task.sleep(for: .milliseconds(10)) } catch { return }
            }
        }
        defer { approvals.cancel() }

        owner.requestConnection(to: firstDevice, protocolVersion: 4)
        try await waitUntil {
            owner.isConnected(to: first.identityFingerprint) && first.isConnected(to: owner.identityFingerprint)
        }
        let groupID = try await owner.createGroup(name: "成员变更验收", memberPeerIDs: [first.identityFingerprint])
        try await waitUntil {
            Self.roster(owner, groupID) == Self.roster(first, groupID) && Self.roster(owner, groupID)?.count == 2
        }

        // Adding the second member must not change the owner's position or the first member's view.
        owner.requestConnection(to: secondDevice, protocolVersion: 4)
        try await waitUntil {
            owner.isConnected(to: second.identityFingerprint) && second.isConnected(to: owner.identityFingerprint)
        }
        try await owner.addGroupMember(second.identityFingerprint, to: groupID)
        try await waitUntil {
            Self.roster(owner, groupID)?.count == 3 &&
            Self.roster(first, groupID)?.count == 3 && Self.roster(second, groupID)?.count == 3
        }
        XCTAssertEqual(owner.groups.first(where: { $0.id == groupID })?.ownerFingerprint, owner.identityFingerprint)

        // Removing it again returns every side to the original two-member roster.
        try await owner.removeGroupMember(second.identityFingerprint, from: groupID)
        try await waitUntil {
            Self.roster(owner, groupID)?.count == 2 && Self.roster(first, groupID)?.count == 2 &&
            Self.roster(second, groupID)?.count == 2 &&
            !(Self.roster(owner, groupID)?.contains(second.identityFingerprint) ?? true)
        }
        XCTAssertEqual(Self.roster(owner, groupID), Self.roster(first, groupID))
        let revision = try XCTUnwrap(owner.groups.first(where: { $0.id == groupID })?.revision)
        XCTAssertEqual(revision, 2, "add and remove must each advance the membership revision once")
    }

    @MainActor
    func testOwnerAddsSeveralMembersInOneRosterTransaction() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let names = ["群主", "成员一", "成员二", "成员三"]
        let stores = names.indices.map { DeviceIdentityStore(serviceIdentifier: "com.feige.group.batch.\($0).\(UUID())") }
        defer {
            try? FileManager.default.removeItem(at: root)
            stores.forEach { $0.delete() }
        }
        let models = zip(stores, names).map { store, name -> TransferModel in
            let loader = GroupIdentityLoader(store: store)
            return TransferModel(supportDirectory: root.appendingPathComponent(name), identityStore: store,
                                 identityLoader: { try loader.load() })
        }
        for (model, name) in zip(models, names) {
            try model.saveSettings(deviceName: name, receiveDirectory: root.appendingPathComponent(name + "-files"))
            await model.start()
        }
        let owner = models[0], members = Array(models.dropFirst())
        var listeners: [NWListener] = []
        defer {
            listeners.forEach { $0.cancel() }
            models.forEach { $0.shutdown() }
        }
        var devices: [LANDevice] = []
        for (index, member) in members.enumerated() {
            let (listener, port) = try await makeListener(for: member, label: "batch-\(index)")
            listeners.append(listener)
            let device = try owner.manualDevice(host: "127.0.0.1", port: String(port))
            devices.append(LANDevice(serviceName: serviceName(for: member.identityFingerprint), displayName: names[index + 1],
                                     endpoint: device.endpoint, protocolVersions: [4]))
        }
        let approvals = Task { @MainActor in
            while !Task.isCancelled {
                for member in members {
                    if member.pendingConnectionPeerName != nil { member.approveConnection(true) }
                    for invitation in member.pendingGroupInvitations { member.acceptGroupInvitation(invitation.id, accepted: true) }
                }
                do { try await Task.sleep(for: .milliseconds(10)) } catch { return }
            }
        }
        defer { approvals.cancel() }

        for (member, device) in zip(members, devices) {
            owner.requestConnection(to: device, protocolVersion: 4)
            try await waitUntil {
                owner.isConnected(to: member.identityFingerprint) && member.isConnected(to: owner.identityFingerprint)
            }
        }
        let groupID = try await owner.createGroup(name: "批量添加", memberPeerIDs: [members[0].identityFingerprint])
        try await waitUntil { Self.roster(members[0], groupID)?.count == 2 }

        try await owner.addGroupMembers(members.dropFirst().map(\.identityFingerprint), to: groupID)
        try await waitUntil { models.allSatisfy { Self.roster($0, groupID)?.count == 4 } }
        XCTAssertEqual(Set(models.compactMap { Self.roster($0, groupID) }).count, 1)
        XCTAssertEqual(owner.groups.first(where: { $0.id == groupID })?.revision, 1,
                       "selected members must join in a single roster revision")
    }

    @MainActor
    private static func roster(_ model: TransferModel, _ groupID: UUID) -> [String]? {
        model.groups.first(where: { $0.id == groupID })?.members.map(\.fingerprint)
    }

    @MainActor
    private func makeListener(for model: TransferModel, label: String) async throws -> (NWListener, UInt16) {
        let listener = try NWListener(using: .tcp, on: .any)
        let (states, continuation) = AsyncStream<Result<UInt16, Error>>.makeStream()
        listener.stateUpdateHandler = { [weak listener] state in
            if case .ready = state, let port = listener?.port?.rawValue {
                continuation.yield(.success(port))
                continuation.finish()
            }
            if case .failed(let error) = state {
                continuation.yield(.failure(error))
                continuation.finish()
            }
        }
        listener.newConnectionHandler = { connection in Task { @MainActor in model.accept(connection) } }
        listener.start(queue: DispatchQueue(label: "feige.group.test.\(label)"))
        var iterator = states.makeAsyncIterator()
        let state = await iterator.next()
        return (listener, try XCTUnwrap(state).get())
    }

    private func serviceName(for fingerprint: String) -> String {
        "Feige-\(String(fingerprint.replacingOccurrences(of: " ", with: "").prefix(20)))"
    }

    @MainActor
    private func waitUntil(timeout: TimeInterval = 30, condition: @MainActor () -> Bool,
                           file: StaticString = #filePath, line: UInt = #line) async throws {
        let deadline = Date().addingTimeInterval(timeout)
        while !condition(), Date() < deadline {
            try await Task.sleep(for: .milliseconds(10))
        }
        XCTAssertTrue(condition(), "Timed out waiting for group state", file: file, line: line)
    }
}

private final class GroupIdentityLoader: @unchecked Sendable {
    private let store: DeviceIdentityStore
    init(store: DeviceIdentityStore) { self.store = store }
    func load() throws -> DeviceIdentity { try store.loadOrCreate() }
}

@MainActor
private final class GroupFileApprovalPolicy {
    var rejectNext = false
}
