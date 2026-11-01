import XCTest
@testable import FeigeMac

final class GroupConversationTests: XCTestCase {
    private func fingerprint(_ value: UInt8) -> String {
        String(repeating: String(format: "%02X", value), count: 32)
    }

    func testGroupWireEncodesDateAsIntegerUnixMilliseconds() throws {
        let messageID = UUID(uuidString: "ABCDEFAB-1234-5678-9ABC-DEF012345678")!
        let groupID = UUID(uuidString: "FEDCBA98-7654-3210-FEED-C0FFEE123456")!
        let date = Date(timeIntervalSince1970: 1_761_732_492.123456)
        let envelope = try GroupMessageEnvelope(id: messageID, groupID: groupID, senderFingerprint: fingerprint(1),
                                                memberRevision: 1, date: date, kind: .text,
                                                text: "integer timestamp")
        let data = try GroupWireJSON.encode(envelope)
        let object = try XCTUnwrap(JSONSerialization.jsonObject(with: data) as? [String: Any])
        let encodedDate = try XCTUnwrap(object["date"] as? NSNumber)
        XCTAssertEqual(object["id"] as? String, messageID.uuidString.lowercased())
        XCTAssertEqual(object["groupID"] as? String, groupID.uuidString.lowercased())
        XCTAssertEqual(encodedDate.int64Value, 1_761_732_492_123)
        XCTAssertEqual(encodedDate.doubleValue, Double(encodedDate.int64Value))
    }

    func testGroupWireUUIDFieldsUseLowercaseAcrossMessageTypes() throws {
        let groupID = UUID(uuidString: "FEDCBA98-7654-3210-FEED-C0FFEE123456")!
        let messageID = UUID(uuidString: "ABCDEFAB-1234-5678-9ABC-DEF012345678")!
        let transferID = UUID(uuidString: "A1B2C3D4-E5F6-4711-8A9B-CDEF01234567")!

        let control = try GroupControlMessage(kind: .leave, eventID: messageID, groupID: groupID,
                                              ownerFingerprint: fingerprint(1), epoch: 1,
                                              targetFingerprint: fingerprint(2))
        let controlJSON = try XCTUnwrap(JSONSerialization.jsonObject(with: GroupWireJSON.encode(control)) as? [String: Any])
        XCTAssertEqual(controlJSON["eventID"] as? String, messageID.uuidString.lowercased())
        XCTAssertEqual(controlJSON["groupID"] as? String, groupID.uuidString.lowercased())

        let acknowledgement = try GroupTextAck(groupID: groupID, messageID: messageID, epoch: 1, accepted: true)
        let ackJSON = try XCTUnwrap(JSONSerialization.jsonObject(with: GroupWireJSON.encode(acknowledgement)) as? [String: Any])
        XCTAssertEqual(ackJSON["groupID"] as? String, groupID.uuidString.lowercased())
        XCTAssertEqual(ackJSON["messageID"] as? String, messageID.uuidString.lowercased())

        let metadata = try GroupFileMetadata(transferID: transferID, fileName: "file.txt", size: 1)
        let envelope = try GroupMessageEnvelope(id: messageID, groupID: groupID, senderFingerprint: fingerprint(1),
                                                memberRevision: 1, kind: .file, fileMetadata: metadata)
        let envelopeJSON = try XCTUnwrap(JSONSerialization.jsonObject(with: GroupWireJSON.encode(envelope)) as? [String: Any])
        let metadataJSON = try XCTUnwrap(envelopeJSON["fileMetadata"] as? [String: Any])
        XCTAssertEqual(metadataJSON["transferID"] as? String, transferID.uuidString.lowercased())
    }

    func testSharedV4CanonicalJSONVectorsAndStrictRejections() throws {
        let fixtureURL = URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent()
            .deletingLastPathComponent()
            .deletingLastPathComponent()
            .appendingPathComponent("Docs/Public/TestVectors/v4.json")
        let fixtureData = try Data(contentsOf: fixtureURL)
        let root = try XCTUnwrap(JSONSerialization.jsonObject(with: fixtureData) as? [String: Any])
        XCTAssertEqual(root["format"] as? String, "feige-v4-group-json-vectors")
        XCTAssertEqual(root["version"] as? Int, 4)
        XCTAssertEqual(root["canonicalization"] as? String,
                       "utf8-sorted-keys-compact-unescaped-slashes-unix-milliseconds-omit-nil-v1")

        let vectors = try XCTUnwrap(root["vectors"] as? [[String: Any]])
        XCTAssertEqual(vectors.count, 4)
        for vector in vectors {
            let identifier = try XCTUnwrap(vector["id"] as? String)
            let type = try XCTUnwrap(vector["type"] as? String)
            let canonicalJSON = try XCTUnwrap(vector["canonicalJson"] as? String)
            let expectedDigest = try XCTUnwrap(vector["sha256Hex"] as? String)
            let input = Data(canonicalJSON.utf8)
            let encoded: Data
            switch type {
            case "groupControl":
                encoded = try GroupWireJSON.encode(GroupControlMessage.decodeStrict(input))
            case "groupText":
                encoded = try GroupWireJSON.encode(GroupMessageEnvelope.decodeStrict(input))
            case "groupTextAck":
                encoded = try GroupWireJSON.encode(GroupTextAck.decodeStrict(input))
            default:
                XCTFail("Unknown vector type: \(type)")
                continue
            }
            XCTAssertEqual(encoded, input, "Canonical bytes changed for \(identifier)")
            XCTAssertEqual(SHA256Text.hexDigest(of: encoded), expectedDigest, "Digest mismatch for \(identifier)")
        }

        let invalidVectors = try XCTUnwrap(root["invalidVectors"] as? [[String: Any]])
        XCTAssertEqual(invalidVectors.count, 5)
        for vector in invalidVectors {
            let identifier = try XCTUnwrap(vector["id"] as? String)
            let type = try XCTUnwrap(vector["type"] as? String)
            let rawJSON = Data(try XCTUnwrap(vector["rawJson"] as? String).utf8)
            switch type {
            case "groupControl":
                XCTAssertThrowsError(try GroupControlMessage.decodeStrict(rawJSON), identifier)
            case "groupText":
                XCTAssertThrowsError(try GroupMessageEnvelope.decodeStrict(rawJSON), identifier)
            case "groupTextAck":
                XCTAssertThrowsError(try GroupTextAck.decodeStrict(rawJSON), identifier)
            default:
                XCTFail("Unknown invalid vector type: \(type)")
            }
        }
    }

    func testMembershipAbortRequiresExactEpochAndNoExtraFields() throws {
        let groupID = UUID(uuidString: "FEDCBA98-7654-3210-FEED-C0FFEE123456")!
        let eventID = UUID(uuidString: "ABCDEFAB-1234-5678-9ABC-DEF012345678")!
        let owner = fingerprint(1)

        let target = fingerprint(2)
        _ = try GroupControlMessage(kind: .membershipAbort, eventID: eventID, groupID: groupID,
                                    ownerFingerprint: owner, epoch: 0, targetFingerprint: target)
        _ = try GroupControlMessage(kind: .membershipAbort, eventID: eventID, groupID: groupID,
                                    ownerFingerprint: owner, epoch: 2, baseEpoch: 1, targetFingerprint: target)

        XCTAssertThrowsError(try GroupControlMessage(kind: .membershipAbort, eventID: eventID, groupID: groupID,
            ownerFingerprint: owner, epoch: 3, baseEpoch: 1, targetFingerprint: target))
        XCTAssertThrowsError(try GroupControlMessage(kind: .membershipAbort, eventID: eventID, groupID: groupID,
            ownerFingerprint: owner, epoch: 1, targetFingerprint: target))
        XCTAssertThrowsError(try GroupControlMessage(kind: .membershipAbort, eventID: eventID, groupID: groupID,
            ownerFingerprint: owner, epoch: 0, targetFingerprint: target, name: "invalid"))
        XCTAssertThrowsError(try GroupControlMessage(kind: .membershipAbort, eventID: eventID, groupID: groupID,
            ownerFingerprint: owner, epoch: 0))
    }

    func testGroupRequiresUniqueFullFingerprintsAndOwnerMembership() throws {
        let owner = try GroupConversationMember(fingerprint: fingerprint(1), displayName: "Owner")
        let member = try GroupConversationMember(fingerprint: fingerprint(2), displayName: "Member")
        let group = try GroupConversation(name: "Team", ownerFingerprint: owner.fingerprint, members: [owner, member])
        XCTAssertEqual(group.members.count, 2)

        XCTAssertThrowsError(try GroupConversation(name: "Team", ownerFingerprint: fingerprint(3), members: [owner, member])) {
            XCTAssertEqual($0 as? GroupConversationError, .ownerNotMember)
        }
        XCTAssertThrowsError(try GroupConversation(name: "Team", ownerFingerprint: owner.fingerprint,
                                                    members: [owner, owner])) {
            XCTAssertEqual($0 as? GroupConversationError, .duplicateMember(owner.fingerprint))
        }
        XCTAssertThrowsError(try GroupConversationMember(fingerprint: "ABCD")) {
            XCTAssertEqual($0 as? GroupConversationError, .invalidFingerprint)
        }
    }

    func testGroupMemberLimitIncludesOwnerAndDeliveryTracksEachRecipient() throws {
        let members = try (1...20).map { try GroupConversationMember(fingerprint: fingerprint(UInt8($0))) }
        let group = try GroupConversation(name: "20 people", ownerFingerprint: members[0].fingerprint, members: members)
        let envelope = try GroupMessageEnvelope(groupID: group.id, senderFingerprint: members[0].fingerprint,
                                               memberRevision: group.revision, kind: .text, text: "Hello")
        var delivery = try GroupMessageDelivery(envelope: envelope, group: group)
        XCTAssertEqual(delivery.recipients.count, 19)
        try delivery.update(memberFingerprint: members[1].fingerprint, status: .delivered, progress: 1)
        try delivery.update(memberFingerprint: members[2].fingerprint, status: .unreachable,
                            failureReason: "offline")
        XCTAssertEqual(delivery.deliveredCount, 1)
        XCTAssertFalse(delivery.isComplete)
        XCTAssertThrowsError(try delivery.update(memberFingerprint: fingerprint(21), status: .delivered, progress: 1))

        let extra = try GroupConversationMember(fingerprint: fingerprint(21))
        XCTAssertThrowsError(try GroupConversation(name: "21 people", ownerFingerprint: members[0].fingerprint,
                                                    members: members + [extra])) {
            XCTAssertEqual($0 as? GroupConversationError, .tooManyMembers(maximum: 20))
        }
    }

    func testMessagePayloadAndDeliveryStatesAreValidatedWhenDecoded() throws {
        let groupID = UUID()
        let sender = fingerprint(1)
        XCTAssertThrowsError(try GroupMessageEnvelope(groupID: groupID, senderFingerprint: sender,
                                                      memberRevision: 0, kind: .text, text: " \n"))
        let invalidPayload = Data("{\"id\":\"\(UUID())\",\"groupID\":\"\(groupID)\",\"senderFingerprint\":\"\(sender)\",\"memberRevision\":0,\"date\":0,\"kind\":\"file\",\"text\":\"oops\"}".utf8)
        XCTAssertThrowsError(try JSONDecoder().decode(GroupMessageEnvelope.self, from: invalidPayload))
        XCTAssertThrowsError(try GroupMemberDelivery(memberFingerprint: sender, status: .delivered, progress: 0))
        XCTAssertThrowsError(try GroupMemberDelivery(memberFingerprint: sender, status: .failed, failureReason: nil))
    }

    func testMembershipControlRequiresConsecutiveEpochAndValidLeaveRequest() throws {
        let owner = fingerprint(41)
        let member = fingerprint(42)
        let roster = try [GroupConversationMember(fingerprint: owner), GroupConversationMember(fingerprint: member)]
        let groupID = UUID()
        let prepare = try GroupControlMessage(kind: .membershipPrepare, groupID: groupID,
            ownerFingerprint: owner, epoch: 1, baseEpoch: 0, targetFingerprint: member,
            name: "Team", roster: roster)
        XCTAssertEqual(try GroupControlMessage.decodeStrict(JSONEncoder().encode(prepare)), prepare)
        XCTAssertThrowsError(try GroupControlMessage(kind: .membershipPrepare, groupID: groupID,
            ownerFingerprint: owner, epoch: 2, baseEpoch: 0, targetFingerprint: member,
            name: "Team", roster: roster))

        let handoffRoster = try [GroupConversationMember(fingerprint: member)]
        let handoff = try GroupControlMessage(kind: .membershipPrepare, groupID: groupID,
            ownerFingerprint: owner, epoch: 1, baseEpoch: 0, targetFingerprint: member,
            newOwnerFingerprint: member, name: "Team", roster: handoffRoster)
        XCTAssertEqual(try GroupControlMessage.decodeStrict(JSONEncoder().encode(handoff)), handoff)
        XCTAssertThrowsError(try GroupControlMessage(kind: .membershipPrepare, groupID: groupID,
            ownerFingerprint: owner, epoch: 1, baseEpoch: 0, targetFingerprint: member,
            newOwnerFingerprint: fingerprint(99), name: "Team", roster: handoffRoster))

        let leave = try GroupControlMessage(kind: .leave, groupID: groupID,
            ownerFingerprint: owner, epoch: 0, targetFingerprint: member)
        XCTAssertEqual(try GroupControlMessage.decodeStrict(JSONEncoder().encode(leave)), leave)
        var invalid = try XCTUnwrap(JSONSerialization.jsonObject(with: JSONEncoder().encode(leave)) as? [String: Any])
        invalid["accepted"] = true
        XCTAssertThrowsError(try GroupControlMessage.decodeStrict(JSONSerialization.data(withJSONObject: invalid)))
    }

    func testGroupControlStrictDecodeAndArchiveRoundTrip() throws {
        let owner = try GroupConversationMember(fingerprint: fingerprint(31), displayName: "Owner")
        let member = try GroupConversationMember(fingerprint: fingerprint(32), displayName: "Member")
        let group = try GroupConversation(name: "Team", ownerFingerprint: owner.fingerprint, members: [owner, member])
        let transferredGroup = try GroupConversation(name: "Transferred", ownerFingerprint: member.fingerprint,
            revision: 2, members: [member], previousOwnerFingerprint: owner.fingerprint)
        let invite = try GroupControlMessage(kind: .invite, groupID: group.id,
                                             ownerFingerprint: owner.fingerprint, epoch: group.revision,
                                             targetFingerprint: member.fingerprint, name: group.name,
                                             roster: group.members)
        let inviteData = try JSONEncoder().encode(invite)
        XCTAssertEqual(try GroupControlMessage.decodeStrict(inviteData), invite)
        var invalidObject = try XCTUnwrap(JSONSerialization.jsonObject(with: inviteData) as? [String: Any])
        invalidObject["unexpected"] = true
        let invalidData = try JSONSerialization.data(withJSONObject: invalidObject)
        XCTAssertThrowsError(try GroupControlMessage.decodeStrict(invalidData))

        let envelope = try GroupMessageEnvelope(groupID: group.id, senderFingerprint: owner.fingerprint,
                                                memberRevision: group.revision, kind: .text, text: "hello")
        let delivery = try GroupMessageDelivery(envelope: envelope, group: group)
        let transferID = UUID()
        let fileMetadata = try GroupFileMetadata(transferID: transferID, fileName: "report.pdf", size: 123,
                                                 sha256: Data(repeating: 9, count: 32))
        let fileEnvelope = try GroupMessageEnvelope(groupID: group.id, senderFingerprint: owner.fingerprint,
            memberRevision: group.revision, kind: .file, fileMetadata: fileMetadata)
        let fileDelivery = try GroupMessageDelivery(envelope: fileEnvelope, group: group)
        let transactionID = UUID()
        let proposed = try GroupConversation(id: group.id, name: group.name, ownerFingerprint: owner.fingerprint,
                                             revision: 1, members: group.members)
        let acceptedInvite = try GroupControlMessage(kind: .invite, eventID: transactionID, groupID: group.id,
            ownerFingerprint: owner.fingerprint, epoch: 1, targetFingerprint: member.fingerprint,
            name: proposed.name, roster: proposed.members)
        let prepare = try GroupControlMessage(kind: .membershipPrepare, eventID: transactionID, groupID: group.id,
            ownerFingerprint: owner.fingerprint, epoch: 1, baseEpoch: 0, targetFingerprint: member.fingerprint,
            name: proposed.name, roster: proposed.members)
        let commit = try GroupControlMessage(kind: .membershipCommit, eventID: transactionID, groupID: group.id,
            ownerFingerprint: owner.fingerprint, epoch: 1, baseEpoch: 0, targetFingerprint: member.fingerprint,
            name: proposed.name, roster: proposed.members)
        let pendingCommit = PendingGroupCommit(group: proposed,
            targets: [GroupCommitTarget(peerID: member.fingerprint, message: commit)])
        let pendingPreparation = PendingGroupPreparation(group: proposed,
            invitationTargets: [GroupCommitTarget(peerID: member.fingerprint, message: acceptedInvite)],
            prepareTargets: [GroupCommitTarget(peerID: member.fingerprint, message: prepare)])
        let abortMember = try GroupConversationMember(fingerprint: fingerprint(33), displayName: "Second member")
        let abortGroup = try GroupConversation(id: UUID(), name: "Abort group", ownerFingerprint: owner.fingerprint,
            members: [owner, member, abortMember])
        let abortA = try GroupControlMessage(kind: .membershipAbort, eventID: UUID(), groupID: abortGroup.id,
            ownerFingerprint: owner.fingerprint, epoch: 1, baseEpoch: 0, targetFingerprint: member.fingerprint)
        let abortB = try GroupControlMessage(kind: .membershipAbort, eventID: UUID(), groupID: abortGroup.id,
            ownerFingerprint: owner.fingerprint, epoch: 1, baseEpoch: 0, targetFingerprint: abortMember.fingerprint)
        let pendingAbort = PendingGroupAbort(group: abortGroup, ownerFingerprint: owner.fingerprint,
            targets: [GroupCommitTarget(peerID: member.fingerprint, message: abortA),
                      GroupCommitTarget(peerID: abortMember.fingerprint, message: abortB)],
            acknowledgedPeers: [member.fingerprint])
        let tombstone = GroupAbortTombstone(abort: abortA, accepted: true)
        let archive = ConversationArchive(groups: [group, transferredGroup], groupMessages: [envelope, fileEnvelope],
            groupDeliveries: [delivery, fileDelivery], groupFileLocations: [transferID: "/tmp/report.pdf"],
            pendingGroupCommits: [pendingCommit], pendingGroupPreparations: [pendingPreparation],
            pendingGroupMembershipChanges: [group.id: prepare], acceptedGroupInvitations: [transactionID: acceptedInvite],
            pendingGroupAborts: [pendingAbort], groupAbortTombstones: [tombstone])
        let restored = try ConversationArchive.decode(JSONEncoder().encode(archive))
        XCTAssertEqual(restored.groups, [group, transferredGroup])
        XCTAssertEqual(restored.groupMessages, [envelope, fileEnvelope])
        XCTAssertEqual(restored.groupDeliveries, [delivery, fileDelivery])
        XCTAssertEqual(restored.groupFileLocations, [transferID: "/tmp/report.pdf"])
        XCTAssertEqual(restored.pendingGroupCommits, [pendingCommit])
        XCTAssertEqual(restored.pendingGroupPreparations, [pendingPreparation])
        XCTAssertEqual(restored.pendingGroupMembershipChanges, [group.id: prepare])
        XCTAssertEqual(restored.acceptedGroupInvitations, [transactionID: acceptedInvite])
        XCTAssertEqual(restored.pendingGroupAborts, [pendingAbort])
        XCTAssertEqual(restored.groupAbortTombstones, [tombstone])

        let versionThree = try JSONSerialization.data(withJSONObject: [
            "version": 3, "messages": [], "hiddenFingerprints": [], "groups": [],
            "groupMessages": [], "groupDeliveries": []
        ])
        let migratedV3 = try ConversationArchive.decode(versionThree)
        XCTAssertEqual(migratedV3.version, ConversationArchive.currentVersion)
        XCTAssertTrue(migratedV3.pendingGroupCommits.isEmpty)

        let versionFour = try JSONSerialization.data(withJSONObject: [
            "version": 4, "messages": [], "hiddenFingerprints": [], "groups": [],
            "groupMessages": [], "groupDeliveries": [], "pendingGroupCommits": [],
            "pendingGroupMembershipChanges": [], "acceptedGroupInvitations": []
        ])
        let migratedV4 = try ConversationArchive.decode(versionFour)
        XCTAssertEqual(migratedV4.version, ConversationArchive.currentVersion)
        XCTAssertTrue(migratedV4.pendingGroupPreparations.isEmpty)

        let versionFive = try JSONSerialization.data(withJSONObject: [
            "version": 5, "messages": [], "hiddenFingerprints": [], "groups": [],
            "groupMessages": [], "groupDeliveries": [], "pendingGroupCommits": [],
            "pendingGroupPreparations": [], "pendingGroupMembershipChanges": [], "acceptedGroupInvitations": []
        ])
        let migratedV5 = try ConversationArchive.decode(versionFive)
        XCTAssertEqual(migratedV5.version, ConversationArchive.currentVersion)
        XCTAssertTrue(migratedV5.pendingGroupAborts.isEmpty)
        XCTAssertTrue(migratedV5.groupAbortTombstones.isEmpty)

        let versionTwo = try JSONSerialization.data(withJSONObject: [
            "version": 2, "messages": [], "hiddenFingerprints": [], "groups": [],
            "groupMessages": [], "groupDeliveries": []
        ])
        let migratedV2 = try ConversationArchive.decode(versionTwo)
        XCTAssertEqual(migratedV2.version, ConversationArchive.currentVersion)
        XCTAssertTrue(migratedV2.groupFileLocations.isEmpty)

        let legacy = try JSONEncoder().encode([ChatMessage(peerID: "peer", peerName: "Old",
                                                           direction: .received, kind: .text,
                                                           text: "legacy", status: .received)])
        let migrated = try ConversationArchive.decode(legacy)
        XCTAssertEqual(migrated.version, ConversationArchive.currentVersion)
        XCTAssertTrue(migrated.groups.isEmpty)
    }
}
