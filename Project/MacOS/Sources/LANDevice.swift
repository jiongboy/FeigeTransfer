import Network

struct LANDevice: Identifiable, Hashable, Sendable {
    var id: String { endpoint.debugDescription }
    let serviceName: String
    let displayName: String
    let endpoint: NWEndpoint
    var addressDescription: String { endpoint.debugDescription }
}
