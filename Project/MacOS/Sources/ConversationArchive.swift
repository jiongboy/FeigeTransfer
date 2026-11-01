import Foundation

struct ConversationArchive: Codable {
    static let currentVersion = 6

    var version: Int
    var messages: [ChatMessage]
    var hiddenFingerprints: Set<String>
    var groups: [GroupConversation]
    var groupMessages: [GroupMessageEnvelope]
    var groupDeliveries: [GroupMessageDelivery]
    var groupFileLocations: [UUID: String]
    var pendingGroupCommits: [PendingGroupCommit]
    var pendingGroupPreparations: [PendingGroupPreparation]
    var pendingGroupMembershipChanges: [UUID: GroupControlMessage]
    var acceptedGroupInvitations: [UUID: GroupControlMessage]
    var pendingGroupAborts: [PendingGroupAbort]
    var groupAbortTombstones: [GroupAbortTombstone]

    init(messages: [ChatMessage] = [], hiddenFingerprints: Set<String> = [],
         groups: [GroupConversation] = [], groupMessages: [GroupMessageEnvelope] = [],
         groupDeliveries: [GroupMessageDelivery] = [], groupFileLocations: [UUID: String] = [:],
         pendingGroupCommits: [PendingGroupCommit] = [],
         pendingGroupPreparations: [PendingGroupPreparation] = [],
          pendingGroupMembershipChanges: [UUID: GroupControlMessage] = [:],
         acceptedGroupInvitations: [UUID: GroupControlMessage] = [:],
          pendingGroupAborts: [PendingGroupAbort] = [],
          groupAbortTombstones: [GroupAbortTombstone] = []) {
        version = Self.currentVersion
        self.messages = messages
        self.hiddenFingerprints = hiddenFingerprints
        self.groups = groups
        self.groupMessages = groupMessages
        self.groupDeliveries = groupDeliveries
        self.groupFileLocations = groupFileLocations
        self.pendingGroupCommits = pendingGroupCommits
        self.pendingGroupPreparations = pendingGroupPreparations
        self.pendingGroupMembershipChanges = pendingGroupMembershipChanges
        self.acceptedGroupInvitations = acceptedGroupInvitations
        self.pendingGroupAborts = pendingGroupAborts
        self.groupAbortTombstones = groupAbortTombstones
    }

    private enum CodingKeys: String, CodingKey {
        case version, messages, hiddenFingerprints, groups, groupMessages, groupDeliveries, groupFileLocations
        case pendingGroupCommits, pendingGroupPreparations, pendingGroupMembershipChanges, acceptedGroupInvitations
        case pendingGroupAborts, groupAbortTombstones
    }

    init(from decoder: Decoder) throws {
        let values = try decoder.container(keyedBy: CodingKeys.self)
        let decodedVersion = try values.decode(Int.self, forKey: .version)
        guard decodedVersion == 1 || decodedVersion == 2 || decodedVersion == 3 || decodedVersion == 4 || decodedVersion == 5 || decodedVersion == Self.currentVersion else {
            throw ArchiveError.unsupportedVersion(decodedVersion)
        }
        version = Self.currentVersion
        messages = try values.decode([ChatMessage].self, forKey: .messages)
        hiddenFingerprints = try values.decodeIfPresent(Set<String>.self, forKey: .hiddenFingerprints) ?? []
        groups = try values.decodeIfPresent([GroupConversation].self, forKey: .groups) ?? []
        groupMessages = try values.decodeIfPresent([GroupMessageEnvelope].self, forKey: .groupMessages) ?? []
        groupDeliveries = try values.decodeIfPresent([GroupMessageDelivery].self, forKey: .groupDeliveries) ?? []
        groupFileLocations = try values.decodeIfPresent([UUID: String].self, forKey: .groupFileLocations) ?? [:]
        pendingGroupCommits = try values.decodeIfPresent([PendingGroupCommit].self, forKey: .pendingGroupCommits) ?? []
        pendingGroupPreparations = try values.decodeIfPresent([PendingGroupPreparation].self,
                                                               forKey: .pendingGroupPreparations) ?? []
        pendingGroupMembershipChanges = try values.decodeIfPresent([UUID: GroupControlMessage].self,
                                                                    forKey: .pendingGroupMembershipChanges) ?? [:]
        acceptedGroupInvitations = try values.decodeIfPresent([UUID: GroupControlMessage].self,
                                                               forKey: .acceptedGroupInvitations) ?? [:]
        pendingGroupAborts = try values.decodeIfPresent([PendingGroupAbort].self, forKey: .pendingGroupAborts) ?? []
        groupAbortTombstones = try values.decodeIfPresent([GroupAbortTombstone].self, forKey: .groupAbortTombstones) ?? []
        let pendingAbortGroupIDs = Set(pendingGroupAborts.map { $0.group.id })
        guard Set(pendingGroupAborts.map { $0.group.id }).count == pendingGroupAborts.count,
              pendingAbortGroupIDs.isDisjoint(with: Set(pendingGroupCommits.map { $0.group.id })),
              pendingAbortGroupIDs.isDisjoint(with: Set(pendingGroupPreparations.map { $0.group.id })),
              Set(groupAbortTombstones.map { $0.abort.eventID }).count == groupAbortTombstones.count,
              groupAbortTombstones.allSatisfy({ $0.abort.kind == .membershipAbort }) else {
            throw DecodingError.dataCorruptedError(forKey: .pendingGroupAborts, in: values,
                debugDescription: "群组取消归档包含重复事务记录")
        }
        for pending in pendingGroupAborts {
            let owner = try GroupConversationValidation.fingerprint(pending.ownerFingerprint)
            let peers = pending.targets.map(\.peerID)
            let events = pending.targets.map { $0.message.eventID }
            guard owner == pending.ownerFingerprint, !pending.targets.isEmpty,
                  Set(peers).count == peers.count, Set(events).count == events.count,
                  pending.acknowledgedPeers.isSubset(of: Set(peers)),
                  pending.targets.allSatisfy({ target in
                      target.message.kind == .membershipAbort && target.message.groupID == pending.group.id &&
                      target.message.ownerFingerprint == pending.ownerFingerprint &&
                      target.message.targetFingerprint == target.peerID
                  }) else {
                throw DecodingError.dataCorruptedError(forKey: .pendingGroupAborts, in: values,
                    debugDescription: "群组取消目标或确认记录无效")
            }
        }
    }

    static func decode(_ data: Data) throws -> ConversationArchive {
        let decoder = JSONDecoder()
        if let legacyMessages = try? decoder.decode([ChatMessage].self, from: data) {
            return ConversationArchive(messages: legacyMessages)
        }
        return try decoder.decode(ConversationArchive.self, from: data)
    }

    enum ArchiveError: LocalizedError {
        case unsupportedVersion(Int)
        var errorDescription: String? {
            switch self {
            case .unsupportedVersion(let version): "会话记录版本 \(version) 不受支持，原文件已保留。"
            }
        }
    }
}
