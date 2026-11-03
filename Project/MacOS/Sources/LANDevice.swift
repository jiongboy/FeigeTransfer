import Network

struct LANDevice: Identifiable, Hashable, Sendable {
    var id: String { endpoint.debugDescription }
    let serviceName: String
    let displayName: String
    let endpoint: NWEndpoint
    var protocolVersions: Set<UInt16> = [3]
    var fingerprintHint: String? = nil
    var avatarId: String? = nil
    var addressDescription: String { endpoint.debugDescription }
}
