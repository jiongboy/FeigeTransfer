import Foundation
import FeigeIdentity

enum GroupWireJSON {
    /// Canonical v4 JSON: UTF-8, recursively sorted object keys, compact separators,
    /// unescaped slashes, omitted nil optionals, and dates as integer milliseconds since Unix epoch.
    static func encode<T: Encodable>(_ value: T) throws -> Data {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.sortedKeys, .withoutEscapingSlashes]
        encoder.dateEncodingStrategy = .custom { date, encoder in
            let milliseconds = date.timeIntervalSince1970 * 1_000
            guard milliseconds.isFinite,
                  milliseconds >= Double(Int64.min),
                  milliseconds < Double(Int64.max) else {
                throw EncodingError.invalidValue(date, EncodingError.Context(
                    codingPath: encoder.codingPath,
                    debugDescription: "date must fit in signed Unix epoch milliseconds."))
            }
            var container = encoder.singleValueContainer()
            try container.encode(Int64(milliseconds.rounded(.down)))
        }
        return try encoder.encode(value)
    }

    static func decoder() -> JSONDecoder {
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .millisecondsSince1970
        return decoder
    }
}

/// A validated group conversation. Member identity is always the complete device
/// fingerprint; display names are informational and are never used for identity.
struct GroupConversation: Codable, Sendable, Equatable, Identifiable {
    static let maximumMemberCount = 20

    let id: UUID
    let name: String
    let ownerFingerprint: String
    /// Monotonically increasing membership epoch chosen by the group protocol.
    let revision: UInt64
    let members: [GroupConversationMember]
    /// The prior owner is retained for idempotent acknowledgements of a completed handoff.
    let previousOwnerFingerprint: String?

    init(id: UUID = UUID(), name: String, ownerFingerprint: String, revision: UInt64 = 0,
         members: [GroupConversationMember], previousOwnerFingerprint: String? = nil) throws {
        let normalizedName = try GroupConversationValidation.groupName(name)
        let normalizedOwner = try GroupConversationValidation.fingerprint(ownerFingerprint)
        try Self.validateMembers(members, ownerFingerprint: normalizedOwner)

        self.id = id
        self.name = normalizedName
        self.ownerFingerprint = normalizedOwner
        self.revision = revision
        self.members = members
        let previousOwner = try previousOwnerFingerprint.map(GroupConversationValidation.fingerprint)
        guard previousOwner != normalizedOwner else { throw GroupConversationError.invalidControlMessage }
        self.previousOwnerFingerprint = previousOwner
    }

    var owner: GroupConversationMember? {
        members.first { $0.fingerprint == ownerFingerprint }
    }

    func member(withFingerprint fingerprint: String) throws -> GroupConversationMember? {
        let normalized = try GroupConversationValidation.fingerprint(fingerprint)
        return members.first { $0.fingerprint == normalized }
    }

    private static func validateMembers(_ members: [GroupConversationMember], ownerFingerprint: String) throws {
        guard !members.isEmpty else { throw GroupConversationError.emptyMembers }
        guard members.count <= maximumMemberCount else {
            throw GroupConversationError.tooManyMembers(maximum: maximumMemberCount)
        }
        var fingerprints = Set<String>()
        for member in members {
            guard fingerprints.insert(member.fingerprint).inserted else {
                throw GroupConversationError.duplicateMember(member.fingerprint)
            }
        }
        guard fingerprints.contains(ownerFingerprint) else {
            throw GroupConversationError.ownerNotMember
        }
    }

    private enum CodingKeys: String, CodingKey {
        case id, name, ownerFingerprint, revision, members, previousOwnerFingerprint
    }

    init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        do {
            try self.init(id: container.decode(UUID.self, forKey: .id),
                          name: container.decode(String.self, forKey: .name),
                          ownerFingerprint: container.decode(String.self, forKey: .ownerFingerprint),
                          revision: container.decode(UInt64.self, forKey: .revision),
                          members: container.decode([GroupConversationMember].self, forKey: .members),
                          previousOwnerFingerprint: container.decodeIfPresent(String.self, forKey: .previousOwnerFingerprint))
        } catch let error as DecodingError {
            throw error
        } catch {
            throw DecodingError.dataCorruptedError(forKey: .members, in: container,
                                                    debugDescription: error.localizedDescription)
        }
    }
}

/// A group member's device identity and an optional, untrusted display label.
struct GroupConversationMember: Codable, Sendable, Equatable, Identifiable {
    let fingerprint: String
    /// Display-only metadata. Never use this value for authentication or uniqueness.
    let displayName: String?

    var id: String { fingerprint }

    init(fingerprint: String, displayName: String? = nil) throws {
        self.fingerprint = try GroupConversationValidation.fingerprint(fingerprint)
        self.displayName = try GroupConversationValidation.displayName(displayName)
    }

    private enum CodingKeys: String, CodingKey { case fingerprint, displayName }

    init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        do {
            try self.init(fingerprint: container.decode(String.self, forKey: .fingerprint),
                          displayName: container.decodeIfPresent(String.self, forKey: .displayName))
        } catch let error as DecodingError {
            throw error
        } catch {
            throw DecodingError.dataCorruptedError(forKey: .fingerprint, in: container,
                                                    debugDescription: error.localizedDescription)
        }
    }
}

/// The supported payload forms for a group message.
enum GroupMessageKind: String, Codable, Sendable, Equatable {
    case text
    case image
    case file
}

/// Transfer metadata for an image or file. It deliberately contains no local path.
struct GroupFileMetadata: Codable, Sendable, Equatable {
    let transferID: UUID
    let fileName: String
    let size: UInt64
    /// Optional MIME type supplied as descriptive metadata, not as a trust decision.
    let mediaType: String?
    /// Optional SHA-256 digest. It must contain exactly 32 bytes when present.
    let sha256: Data?

    init(transferID: UUID = UUID(), fileName: String, size: UInt64, mediaType: String? = nil,
         sha256: Data? = nil) throws {
        self.transferID = transferID
        self.fileName = try GroupConversationValidation.fileName(fileName)
        guard size <= UInt64(Int64.max) else { throw GroupConversationError.fileSizeOutOfRange }
        self.size = size
        self.mediaType = try GroupConversationValidation.mediaType(mediaType)
        if let sha256, sha256.count != 32 { throw GroupConversationError.invalidDigest }
        self.sha256 = sha256
    }

    private enum CodingKeys: String, CodingKey { case transferID, fileName, size, mediaType, sha256 }

    func encode(to encoder: Encoder) throws {
        var container = encoder.container(keyedBy: CodingKeys.self)
        try container.encode(transferID.uuidString.lowercased(), forKey: .transferID)
        try container.encode(fileName, forKey: .fileName)
        try container.encode(size, forKey: .size)
        try container.encodeIfPresent(mediaType, forKey: .mediaType)
        try container.encodeIfPresent(sha256, forKey: .sha256)
    }

    init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        do {
            try self.init(transferID: container.decode(UUID.self, forKey: .transferID),
                          fileName: container.decode(String.self, forKey: .fileName),
                          size: container.decode(UInt64.self, forKey: .size),
                          mediaType: container.decodeIfPresent(String.self, forKey: .mediaType),
                          sha256: container.decodeIfPresent(Data.self, forKey: .sha256))
        } catch let error as DecodingError {
            throw error
        } catch {
            throw DecodingError.dataCorruptedError(forKey: .fileName, in: container,
                                                    debugDescription: error.localizedDescription)
        }
    }
}

/// A transport-independent group message. It is intentionally independent of the
/// app-executable-only `ChatMessage` type and contains no device-local file path.
struct GroupMessageEnvelope: Codable, Sendable, Equatable, Identifiable {
    let id: UUID
    let groupID: UUID
    let senderFingerprint: String
    let memberRevision: UInt64
    let date: Date
    let kind: GroupMessageKind
    let text: String?
    let fileMetadata: GroupFileMetadata?

    init(id: UUID = UUID(), groupID: UUID, senderFingerprint: String, memberRevision: UInt64,
         date: Date = Date(), kind: GroupMessageKind, text: String? = nil,
         fileMetadata: GroupFileMetadata? = nil) throws {
        let normalizedSender = try GroupConversationValidation.fingerprint(senderFingerprint)
        guard date.timeIntervalSince1970.isFinite else { throw GroupConversationError.invalidDate }
        switch kind {
        case .text:
            guard let text,
                  !text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty,
                  text.utf8.count <= GroupConversationValidation.maximumTextBytes,
                  fileMetadata == nil else {
                throw GroupConversationError.invalidTextPayload
            }
        case .image, .file:
            guard text == nil, fileMetadata != nil else {
                throw GroupConversationError.invalidFilePayload
            }
        }
        self.id = id
        self.groupID = groupID
        self.senderFingerprint = normalizedSender
        self.memberRevision = memberRevision
        self.date = date
        self.kind = kind
        self.text = text
        self.fileMetadata = fileMetadata
    }

    static func decodeStrict(_ data: Data) throws -> GroupMessageEnvelope {
        let object = try GroupConversationValidation.jsonObject(data,
            allowed: ["id", "groupID", "senderFingerprint", "memberRevision", "date", "kind", "text", "fileMetadata"],
            required: ["id", "groupID", "senderFingerprint", "memberRevision", "date", "kind"])
        if let metadata = object["fileMetadata"] as? [String: Any] {
            _ = try GroupConversationValidation.validateObject(metadata,
                allowed: ["transferID", "fileName", "size", "mediaType", "sha256"],
                required: ["transferID", "fileName", "size"])
        }
        return try GroupWireJSON.decoder().decode(GroupMessageEnvelope.self, from: data)
    }

    private enum CodingKeys: String, CodingKey {
        case id, groupID, senderFingerprint, memberRevision, date, kind, text, fileMetadata
    }

    func encode(to encoder: Encoder) throws {
        var container = encoder.container(keyedBy: CodingKeys.self)
        try container.encode(id.uuidString.lowercased(), forKey: .id)
        try container.encode(groupID.uuidString.lowercased(), forKey: .groupID)
        try container.encode(senderFingerprint, forKey: .senderFingerprint)
        try container.encode(memberRevision, forKey: .memberRevision)
        try container.encode(date, forKey: .date)
        try container.encode(kind, forKey: .kind)
        try container.encodeIfPresent(text, forKey: .text)
        try container.encodeIfPresent(fileMetadata, forKey: .fileMetadata)
    }

    init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        do {
            try self.init(id: container.decode(UUID.self, forKey: .id),
                          groupID: container.decode(UUID.self, forKey: .groupID),
                          senderFingerprint: container.decode(String.self, forKey: .senderFingerprint),
                          memberRevision: container.decode(UInt64.self, forKey: .memberRevision),
                          date: container.decode(Date.self, forKey: .date),
                          kind: container.decode(GroupMessageKind.self, forKey: .kind),
                          text: container.decodeIfPresent(String.self, forKey: .text),
                          fileMetadata: container.decodeIfPresent(GroupFileMetadata.self, forKey: .fileMetadata))
        } catch let error as DecodingError {
            throw error
        } catch {
            throw DecodingError.dataCorruptedError(forKey: .kind, in: container,
                                                    debugDescription: error.localizedDescription)
        }
    }
}

enum GroupDeliveryStatus: String, Codable, Sendable, Equatable {
    case pending
    case transferring
    case delivered
    case unreachable
    case declined
    case failed
}

/// Delivery state for one recipient. Progress is a finite fraction from 0 through 1.
struct GroupMemberDelivery: Codable, Sendable, Equatable, Identifiable {
    let memberFingerprint: String
    let status: GroupDeliveryStatus
    let progress: Double
    let failureReason: String?

    var id: String { memberFingerprint }

    init(memberFingerprint: String, status: GroupDeliveryStatus = .pending, progress: Double = 0,
         failureReason: String? = nil) throws {
        let normalizedFingerprint = try GroupConversationValidation.fingerprint(memberFingerprint)
        guard progress.isFinite, (0...1).contains(progress) else {
            throw GroupConversationError.invalidProgress
        }
        let reason = try GroupConversationValidation.failureReason(failureReason)
        switch status {
        case .pending:
            guard progress == 0, reason == nil else { throw GroupConversationError.invalidDeliveryState }
        case .transferring:
            guard reason == nil else { throw GroupConversationError.invalidDeliveryState }
        case .delivered:
            guard progress == 1, reason == nil else { throw GroupConversationError.invalidDeliveryState }
        case .unreachable, .declined, .failed:
            guard reason != nil else { throw GroupConversationError.missingFailureReason }
        }
        self.memberFingerprint = normalizedFingerprint
        self.status = status
        self.progress = progress
        self.failureReason = reason
    }

    private enum CodingKeys: String, CodingKey { case memberFingerprint, status, progress, failureReason }

    init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        do {
            try self.init(memberFingerprint: container.decode(String.self, forKey: .memberFingerprint),
                          status: container.decode(GroupDeliveryStatus.self, forKey: .status),
                          progress: container.decode(Double.self, forKey: .progress),
                          failureReason: container.decodeIfPresent(String.self, forKey: .failureReason))
        } catch let error as DecodingError {
            throw error
        } catch {
            throw DecodingError.dataCorruptedError(forKey: .status, in: container,
                                                    debugDescription: error.localizedDescription)
        }
    }
}

/// Sender-local delivery tracking for one group envelope. Recipient states are
/// intentionally separate from the immutable message sent to the group.
struct GroupMessageDelivery: Codable, Sendable, Equatable, Identifiable {
    let id: UUID
    let groupID: UUID
    private(set) var recipients: [GroupMemberDelivery]

    var isComplete: Bool { recipients.allSatisfy { $0.status != .pending && $0.status != .transferring } }
    var deliveredCount: Int { recipients.filter { $0.status == .delivered }.count }

    init(envelope: GroupMessageEnvelope, group: GroupConversation) throws {
        guard envelope.groupID == group.id else { throw GroupConversationError.groupIDMismatch }
        guard envelope.memberRevision == group.revision else { throw GroupConversationError.revisionMismatch }
        guard group.members.contains(where: { $0.fingerprint == envelope.senderFingerprint }) else {
            throw GroupConversationError.senderNotMember
        }
        self.id = envelope.id
        self.groupID = group.id
        recipients = try group.members
            .filter { $0.fingerprint != envelope.senderFingerprint }
            .map { try GroupMemberDelivery(memberFingerprint: $0.fingerprint) }
    }

    mutating func update(memberFingerprint: String, status: GroupDeliveryStatus,
                         progress: Double = 0, failureReason: String? = nil) throws {
        let normalizedFingerprint = try GroupConversationValidation.fingerprint(memberFingerprint)
        guard let index = recipients.firstIndex(where: { $0.memberFingerprint == normalizedFingerprint }) else {
            throw GroupConversationError.unknownRecipient(normalizedFingerprint)
        }
        recipients[index] = try GroupMemberDelivery(memberFingerprint: normalizedFingerprint,
                                                     status: status, progress: progress,
                                                     failureReason: failureReason)
    }

    private enum CodingKeys: String, CodingKey { case id, groupID, recipients }

    init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        let decodedRecipients = try container.decode([GroupMemberDelivery].self, forKey: .recipients)
        do {
            try Self.validateUniqueRecipients(decodedRecipients)
            id = try container.decode(UUID.self, forKey: .id)
            groupID = try container.decode(UUID.self, forKey: .groupID)
            recipients = decodedRecipients
        } catch let error as DecodingError {
            throw error
        } catch {
            throw DecodingError.dataCorruptedError(forKey: .recipients, in: container,
                                                    debugDescription: error.localizedDescription)
        }
    }

    private static func validateUniqueRecipients(_ recipients: [GroupMemberDelivery]) throws {
        var fingerprints = Set<String>()
        for recipient in recipients {
            guard fingerprints.insert(recipient.memberFingerprint).inserted else {
                throw GroupConversationError.duplicateRecipient(recipient.memberFingerprint)
            }
        }
    }
}

struct PendingGroupFileApproval: Identifiable, Equatable {
    let manifest: GroupFileManifestWire
    let peerName: String
    let groupName: String
    var id: UUID { manifest.metadata.transferID }
}

struct PendingGroupInvitation: Identifiable, Sendable {
    let message: GroupControlMessage
    let peerFingerprint: String
    let peerName: String
    var id: UUID { message.eventID }
}


struct PendingGroupLeaveRequest: Identifiable, Codable, Sendable {
    let message: GroupControlMessage
    let peerName: String
    var id: UUID { message.eventID }
}

struct GroupCommitTarget: Codable, Sendable, Equatable {
    let peerID: String
    let message: GroupControlMessage
}

struct PendingGroupCommit: Codable, Sendable, Equatable {
    let group: GroupConversation
    let targets: [GroupCommitTarget]
}

struct PendingGroupPreparation: Codable, Sendable, Equatable {
    let group: GroupConversation
    let invitationTargets: [GroupCommitTarget]
    let prepareTargets: [GroupCommitTarget]
    let durableAbortTargetFingerprints: [String]?

    init(group: GroupConversation, invitationTargets: [GroupCommitTarget], prepareTargets: [GroupCommitTarget],
         durableAbortTargetFingerprints: [String]? = nil) {
        self.group = group
        self.invitationTargets = invitationTargets
        self.prepareTargets = prepareTargets
        self.durableAbortTargetFingerprints = durableAbortTargetFingerprints
    }
}

struct PendingGroupAbort: Codable, Sendable, Equatable {
    let group: GroupConversation
    let ownerFingerprint: String
    let targets: [GroupCommitTarget]
    var acknowledgedPeers: Set<String>
}

struct GroupAbortTombstone: Codable, Sendable, Equatable {
    let abort: GroupControlMessage
    let accepted: Bool
}

struct GroupAckKey: Hashable, Sendable {
    let groupID: UUID
    let messageID: UUID
    let peerFingerprint: String
}

struct GroupControlWaitKey: Hashable, Sendable {
    let peerFingerprint: String
    let eventID: UUID
    let responseKind: GroupControlKind
}

enum GroupControlKind: String, Codable, Sendable, Hashable {
    case invite
    case inviteReply
    case membershipPrepare
    case membershipPrepareReply
    case membershipCommit
    case membershipCommitReply
    case membershipAbort
    case membershipAbortReply
    case leave
    case leaveReply
}

/// Strict JSON payload carried by encrypted v4 groupControl frames.
struct GroupControlMessage: Codable, Sendable, Equatable {
    let version: UInt8
    let kind: GroupControlKind
    let eventID: UUID
    let groupID: UUID
    let ownerFingerprint: String
    let epoch: UInt64
    let baseEpoch: UInt64?
    let targetFingerprint: String?
    let newOwnerFingerprint: String?
    let name: String?
    let roster: [GroupConversationMember]?
    let accepted: Bool?

    init(kind: GroupControlKind, eventID: UUID = UUID(), groupID: UUID,
         ownerFingerprint: String, epoch: UInt64, baseEpoch: UInt64? = nil,
         targetFingerprint: String? = nil, newOwnerFingerprint: String? = nil,
         name: String? = nil, roster: [GroupConversationMember]? = nil, accepted: Bool? = nil) throws {
        version = 1
        self.kind = kind
        self.eventID = eventID
        self.groupID = groupID
        self.ownerFingerprint = try GroupConversationValidation.fingerprint(ownerFingerprint)
        self.epoch = epoch
        self.baseEpoch = baseEpoch
        self.targetFingerprint = try targetFingerprint.map(GroupConversationValidation.fingerprint)
        self.newOwnerFingerprint = try newOwnerFingerprint.map(GroupConversationValidation.fingerprint)
        self.name = try name.map(GroupConversationValidation.groupName)
        self.roster = roster
        self.accepted = accepted
        try validate()
    }

    static func decodeStrict(_ data: Data) throws -> GroupControlMessage {
        let object = try GroupConversationValidation.jsonObject(data,
            allowed: ["version", "kind", "eventID", "groupID", "ownerFingerprint", "epoch", "baseEpoch",
                      "targetFingerprint", "newOwnerFingerprint", "name", "roster", "accepted"],
            required: ["version", "kind", "eventID", "groupID", "ownerFingerprint", "epoch"])
        if let roster = object["roster"] as? [[String: Any]] {
            for member in roster {
                _ = try GroupConversationValidation.validateObject(member,
                    allowed: ["fingerprint", "displayName"], required: ["fingerprint"])
            }
        }
        return try JSONDecoder().decode(GroupControlMessage.self, from: data)
    }

    private enum CodingKeys: String, CodingKey, CaseIterable {
        case version, kind, eventID, groupID, ownerFingerprint, epoch, baseEpoch
        case targetFingerprint, newOwnerFingerprint, name, roster, accepted
    }

    func encode(to encoder: Encoder) throws {
        var values = encoder.container(keyedBy: CodingKeys.self)
        try values.encode(version, forKey: .version)
        try values.encode(kind, forKey: .kind)
        try values.encode(eventID.uuidString.lowercased(), forKey: .eventID)
        try values.encode(groupID.uuidString.lowercased(), forKey: .groupID)
        try values.encode(ownerFingerprint, forKey: .ownerFingerprint)
        try values.encode(epoch, forKey: .epoch)
        try values.encodeIfPresent(baseEpoch, forKey: .baseEpoch)
        try values.encodeIfPresent(targetFingerprint, forKey: .targetFingerprint)
        try values.encodeIfPresent(newOwnerFingerprint, forKey: .newOwnerFingerprint)
        try values.encodeIfPresent(name, forKey: .name)
        try values.encodeIfPresent(roster, forKey: .roster)
        try values.encodeIfPresent(accepted, forKey: .accepted)
    }

    init(from decoder: Decoder) throws {
        let values = try decoder.container(keyedBy: CodingKeys.self)
        guard Set(values.allKeys.map(\.stringValue)).isSubset(of: Set(CodingKeys.allCases.map(\.rawValue))) else {
            throw DecodingError.dataCorrupted(.init(codingPath: decoder.codingPath, debugDescription: "未知群控字段"))
        }
        version = try values.decode(UInt8.self, forKey: .version)
        kind = try values.decode(GroupControlKind.self, forKey: .kind)
        eventID = try values.decode(UUID.self, forKey: .eventID)
        groupID = try values.decode(UUID.self, forKey: .groupID)
        ownerFingerprint = try GroupConversationValidation.fingerprint(values.decode(String.self, forKey: .ownerFingerprint))
        epoch = try values.decode(UInt64.self, forKey: .epoch)
        baseEpoch = try values.decodeIfPresent(UInt64.self, forKey: .baseEpoch)
        targetFingerprint = try values.decodeIfPresent(String.self, forKey: .targetFingerprint)
            .map(GroupConversationValidation.fingerprint)
        newOwnerFingerprint = try values.decodeIfPresent(String.self, forKey: .newOwnerFingerprint)
            .map(GroupConversationValidation.fingerprint)
        name = try values.decodeIfPresent(String.self, forKey: .name).map(GroupConversationValidation.groupName)
        roster = try values.decodeIfPresent([GroupConversationMember].self, forKey: .roster)
        accepted = try values.decodeIfPresent(Bool.self, forKey: .accepted)
        try validate()
    }

    private func validate() throws {
        guard version == 1 else { throw GroupConversationError.invalidControlMessage }
        switch kind {
        case .invite:
            guard baseEpoch == nil, newOwnerFingerprint == nil,
                  let targetFingerprint, let name, let roster else {
                throw GroupConversationError.invalidControlMessage
            }
            let proposed = try GroupConversation(id: groupID, name: name, ownerFingerprint: ownerFingerprint,
                                                 revision: epoch, members: roster)
            guard proposed.members.contains(where: { $0.fingerprint == targetFingerprint }) else {
                throw GroupConversationError.invalidControlMessage
            }
        case .inviteReply, .leaveReply:
            guard baseEpoch == nil, newOwnerFingerprint == nil, targetFingerprint != nil, accepted != nil else {
                throw GroupConversationError.invalidControlMessage
            }
        case .membershipPrepare, .membershipCommit:
            guard let roster else { throw GroupConversationError.invalidControlMessage }
            if let baseEpoch {
                guard baseEpoch < UInt64.max, epoch == baseEpoch + 1 else {
                    throw GroupConversationError.invalidControlMessage
                }
            } else {
                guard epoch == 0, newOwnerFingerprint == nil else {
                    throw GroupConversationError.invalidControlMessage
                }
            }
            if let newOwnerFingerprint, newOwnerFingerprint == ownerFingerprint {
                throw GroupConversationError.invalidControlMessage
            }
            _ = try GroupConversation(id: groupID, name: name ?? "群组",
                                      ownerFingerprint: newOwnerFingerprint ?? ownerFingerprint,
                                      revision: epoch, members: roster)
        case .membershipPrepareReply, .membershipCommitReply, .membershipAbortReply:
            guard baseEpoch == nil, newOwnerFingerprint == nil, targetFingerprint != nil, accepted != nil else {
                throw GroupConversationError.invalidControlMessage
            }
        case .membershipAbort:
            guard targetFingerprint != nil, newOwnerFingerprint == nil, name == nil,
                  roster == nil, accepted == nil else { throw GroupConversationError.invalidControlMessage }
            if let baseEpoch {
                guard baseEpoch < UInt64.max, epoch == baseEpoch + 1 else {
                    throw GroupConversationError.invalidControlMessage
                }
            } else {
                guard epoch == 0 else { throw GroupConversationError.invalidControlMessage }
            }
        case .leave:
            guard baseEpoch == nil, newOwnerFingerprint == nil, targetFingerprint != nil,
                  roster == nil, name == nil, accepted == nil else {
                throw GroupConversationError.invalidControlMessage
            }
        }
    }
}

struct GroupTextAck: Codable, Sendable, Equatable {
    let groupID: UUID
    let messageID: UUID
    let epoch: UInt64
    let accepted: Bool
    let reason: String?

    static func decodeStrict(_ data: Data) throws -> GroupTextAck {
        _ = try GroupConversationValidation.jsonObject(data,
            allowed: ["groupID", "messageID", "epoch", "accepted", "reason"],
            required: ["groupID", "messageID", "epoch", "accepted"])
        return try JSONDecoder().decode(GroupTextAck.self, from: data)
    }

    private enum CodingKeys: String, CodingKey, CaseIterable { case groupID, messageID, epoch, accepted, reason }

    func encode(to encoder: Encoder) throws {
        var values = encoder.container(keyedBy: CodingKeys.self)
        try values.encode(groupID.uuidString.lowercased(), forKey: .groupID)
        try values.encode(messageID.uuidString.lowercased(), forKey: .messageID)
        try values.encode(epoch, forKey: .epoch)
        try values.encode(accepted, forKey: .accepted)
        try values.encodeIfPresent(reason, forKey: .reason)
    }

    init(from decoder: Decoder) throws {
        let values = try decoder.container(keyedBy: CodingKeys.self)
        let required: Set<String> = [CodingKeys.groupID.rawValue, CodingKeys.messageID.rawValue,
                                     CodingKeys.epoch.rawValue, CodingKeys.accepted.rawValue]
        let present = Set(values.allKeys.map(\.rawValue))
        guard required.isSubset(of: present), present.isSubset(of: Set(CodingKeys.allCases.map(\.rawValue))) else {
            throw DecodingError.dataCorrupted(.init(codingPath: decoder.codingPath, debugDescription: "群消息回执字段无效"))
        }
        try self.init(groupID: values.decode(UUID.self, forKey: .groupID),
                      messageID: values.decode(UUID.self, forKey: .messageID),
                      epoch: values.decode(UInt64.self, forKey: .epoch),
                      accepted: values.decode(Bool.self, forKey: .accepted),
                      reason: values.decodeIfPresent(String.self, forKey: .reason))
    }

    init(groupID: UUID, messageID: UUID, epoch: UInt64, accepted: Bool, reason: String? = nil) throws {
        self.groupID = groupID
        self.messageID = messageID
        self.epoch = epoch
        self.accepted = accepted
        self.reason = try GroupConversationValidation.failureReason(reason)
        guard accepted ? reason == nil : reason != nil else { throw GroupConversationError.invalidControlMessage }
    }
}

enum GroupConversationError: LocalizedError, Equatable {
    case invalidFingerprint
    case invalidGroupName
    case invalidDisplayName
    case emptyMembers
    case tooManyMembers(maximum: Int)
    case duplicateMember(String)
    case ownerNotMember
    case invalidFileName
    case fileSizeOutOfRange
    case invalidMediaType
    case invalidDigest
    case invalidDate
    case invalidTextPayload
    case invalidFilePayload
    case invalidProgress
    case invalidDeliveryState
    case missingFailureReason
    case groupIDMismatch
    case revisionMismatch
    case senderNotMember
    case unknownRecipient(String)
    case duplicateRecipient(String)
    case invalidControlMessage
    case groupPreparationPending

    var errorDescription: String? {
        switch self {
        case .invalidFingerprint: "设备指纹必须是完整的 64 位 SHA-256 十六进制值。"
        case .invalidGroupName: "群组名称不能为空、不能含控制字符，且长度不能超过 80 个字符。"
        case .invalidDisplayName: "成员显示名称不能含控制字符，且 UTF-8 长度不能超过 256 字节。"
        case .emptyMembers: "群组至少需要一名成员。"
        case .tooManyMembers(let maximum): "群组成员不能超过 \(maximum) 人。"
        case .duplicateMember(let fingerprint): "群组中存在重复设备指纹：\(fingerprint)。"
        case .ownerNotMember: "群组创建者必须是群组成员。"
        case .invalidFileName: "文件名不能为空、不能包含路径分隔符或控制字符。"
        case .fileSizeOutOfRange: "文件大小超出支持范围。"
        case .invalidMediaType: "媒体类型格式无效或过长。"
        case .invalidDigest: "SHA-256 摘要必须为 32 字节。"
        case .invalidDate: "消息日期无效。"
        case .invalidTextPayload: "文字消息不能为空、不能只有空白字符，且不能超过 8192 字节。"
        case .invalidFilePayload: "图片或文件消息必须只包含文件元数据。"
        case .invalidProgress: "传输进度必须是 0 到 1 之间的有限数值。"
        case .invalidDeliveryState: "投递状态与进度或失败原因不匹配。"
        case .missingFailureReason: "未送达、拒收或失败状态必须包含原因。"
        case .groupIDMismatch: "消息所属群组与投递群组不匹配。"
        case .revisionMismatch: "消息成员版本与当前群组版本不匹配。"
        case .senderNotMember: "消息发送者不是当前群组成员。"
        case .unknownRecipient(let fingerprint): "投递目标不是当前消息的接收成员：\(fingerprint)。"
        case .duplicateRecipient(let fingerprint): "投递记录中存在重复设备指纹：\(fingerprint)。"
        case .invalidControlMessage: "群组控制消息缺少必需字段或包含无效状态。"
        case .groupPreparationPending: "成员事务已保存，尚未完成。可在侧栏的待处理群组操作中重试。"
        }
    }
}

enum GroupConversationValidation {
    static let maximumTextBytes = 8192

    static func jsonObject(_ data: Data, allowed: Set<String>, required: Set<String>) throws -> [String: Any] {
        guard let object = try JSONSerialization.jsonObject(with: data) as? [String: Any] else {
            throw GroupConversationError.invalidControlMessage
        }
        return try validateObject(object, allowed: allowed, required: required)
    }

    static func validateObject(_ object: [String: Any], allowed: Set<String>, required: Set<String>) throws -> [String: Any] {
        let keys = Set(object.keys)
        guard keys.isSubset(of: allowed), required.isSubset(of: keys) else {
            throw GroupConversationError.invalidControlMessage
        }
        return object
    }

    static func fingerprint(_ value: String) throws -> String {
        // Accept the app's grouped display format or its ungrouped wire form, then
        // store one canonical full fingerprint. No truncation or nickname lookup.
        let hex = value.replacingOccurrences(of: " ", with: "").uppercased()
        let bytes = Array(hex.utf8)
        guard bytes.count == 64,
              bytes.allSatisfy({ (48...57).contains($0) || (65...70).contains($0) }) else {
            throw GroupConversationError.invalidFingerprint
        }
        return stride(from: 0, to: 64, by: 4).map { offset in
            let start = hex.index(hex.startIndex, offsetBy: offset)
            let end = hex.index(start, offsetBy: 4)
            return String(hex[start..<end])
        }.joined(separator: " ")
    }

    static func groupName(_ value: String) throws -> String {
        let trimmed = value.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty, trimmed.count <= 80,
              !trimmed.unicodeScalars.contains(where: { CharacterSet.controlCharacters.contains($0) }) else {
            throw GroupConversationError.invalidGroupName
        }
        return trimmed
    }

    static func displayName(_ value: String?) throws -> String? {
        guard let value else { return nil }
        let trimmed = value.trimmingCharacters(in: .whitespacesAndNewlines)
        guard trimmed.utf8.count <= 256,
              !trimmed.unicodeScalars.contains(where: { CharacterSet.controlCharacters.contains($0) }) else {
            throw GroupConversationError.invalidDisplayName
        }
        return trimmed.isEmpty ? nil : trimmed
    }

    static func fileName(_ value: String) throws -> String {
        let trimmed = value.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty, trimmed != ".", trimmed != "..", trimmed.utf8.count <= 255,
              !trimmed.contains("/"), !trimmed.contains("\\"),
              !trimmed.unicodeScalars.contains(where: { CharacterSet.controlCharacters.contains($0) }) else {
            throw GroupConversationError.invalidFileName
        }
        return trimmed
    }

    static func mediaType(_ value: String?) throws -> String? {
        guard let value else { return nil }
        let trimmed = value.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty, trimmed.utf8.count <= 127,
              trimmed.utf8.allSatisfy({ (33...126).contains($0) }), trimmed.contains("/") else {
            throw GroupConversationError.invalidMediaType
        }
        return trimmed
    }

    static func failureReason(_ value: String?) throws -> String? {
        guard let value else { return nil }
        let trimmed = value.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty, trimmed.utf8.count <= 2048,
              !trimmed.unicodeScalars.contains(where: { CharacterSet.controlCharacters.contains($0) }) else {
            throw GroupConversationError.invalidDeliveryState
        }
        return trimmed
    }
}
