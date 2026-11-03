import Darwin
import Foundation

/// User-configured probe targets: one private IPv4 address, a /24–/32 prefix, or a three-octet /24 shorthand.
public enum ProbeTargets {
    public static let maxEntries = 16
    public static let maxHosts = 4096

    /// Router default LANs offered for one-click entry. None is probed until the user saves it: each /24 costs 254
    /// probes a minute and the whole list would nearly exhaust `maxHosts`. Keep in sync with the Windows list.
    public static let common: [(target: String, note: String)] = [
        ("192.168.0.0/24", "TP-Link、D-Link、Netgear 等"),
        ("192.168.1.0/24", "多数路由器、运营商光猫"),
        ("192.168.3.0/24", "华为、荣耀路由器"),
        ("192.168.8.0/24", "华为随身 Wi-Fi、4G/5G 路由"),
        ("192.168.31.0/24", "小米路由器"),
        ("192.168.50.0/24", "华硕路由器"),
        ("192.168.68.0/24", "TP-Link Deco"),
        ("192.168.86.0/24", "Google Nest Wifi"),
        ("192.168.199.0/24", "极路由"),
        ("10.0.0.0/24", "部分运营商路由器"),
        ("10.0.1.0/24", "Apple AirPort"),
    ]

    /// Company subnets probed without being saved, but only while this machine itself has a 10.80.0.0/16 address, so
    /// devices off the company network never sweep them. Keep in sync with the Windows list.
    public static let company = ["10.80.1.0/24", "10.80.10.0/24"]

    /// The unsaved targets that apply to a machine with `localAddresses`.
    public static func automatic(for localAddresses: [UInt32]) -> [String] {
        localAddresses.contains { $0 >> 16 == 0x0A50 } ? company : []
    }

    /// Hosts for one probe round: the saved targets plus the automatic ones. Saved targets win when both do not fit.
    public static func roundHosts(saved: [String], localAddresses: [UInt32]) -> [UInt32] {
        let base = hosts(in: saved) ?? []
        let extra = automatic(for: localAddresses)
        guard !extra.isEmpty, let combined = hosts(in: Array(Set(saved.map(normalized)).union(extra))) else { return base }
        return combined
    }

    /// IPv4 addresses of this machine's up, non-loopback interfaces.
    public static func localIPv4Addresses() -> [UInt32] {
        var pointer: UnsafeMutablePointer<ifaddrs>?
        guard getifaddrs(&pointer) == 0, let first = pointer else { return [] }
        defer { freeifaddrs(pointer) }
        var result: [UInt32] = []
        for item in sequence(first: first, next: { $0.pointee.ifa_next }) {
            let flags = Int32(item.pointee.ifa_flags)
            guard flags & IFF_UP != 0, flags & IFF_LOOPBACK == 0, let addr = item.pointee.ifa_addr,
                  addr.pointee.sa_family == UInt8(AF_INET) else { continue }
            let address = addr.withMemoryRebound(to: sockaddr_in.self, capacity: 1) { UInt32(bigEndian: $0.pointee.sin_addr.s_addr) }
            result.append(address)
        }
        return result
    }

    /// Canonical form of `entry`: trimmed, with a three-octet shorthand such as `10.80.1` expanded to `10.80.1.0/24`.
    public static func normalized(_ entry: String) -> String {
        let trimmed = entry.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.contains("/"), trimmed.split(separator: ".", omittingEmptySubsequences: false).count == 3 else { return trimmed }
        return trimmed + ".0/24"
    }

    /// Returns the host addresses of `entry`, or nil when it is not an allowed private target.
    public static func hosts(in entry: String) -> [UInt32]? {
        let parts = normalized(entry).split(separator: "/", omittingEmptySubsequences: false)
        guard parts.count == 1 || parts.count == 2, let base = parseIPv4(String(parts[0])), isPrivate(base) else { return nil }
        guard parts.count == 2 else { return [base] }
        guard parts[1].allSatisfy(\.isASCII), let prefix = Int(parts[1]), (24...32).contains(prefix) else { return nil }
        guard prefix < 31 else { return prefix == 32 ? [base] : [base & ~1, base | 1] }
        let mask = UInt32.max << UInt32(32 - prefix)
        let network = base & mask
        let broadcast = network | ~mask
        return Array((network + 1)..<broadcast)
    }

    /// Returns the deduplicated hosts of `entries`, or nil when the list exceeds the limits or has an invalid entry.
    public static func hosts(in entries: [String]) -> [UInt32]? {
        guard entries.count <= maxEntries else { return nil }
        var seen = Set<UInt32>()
        var result: [UInt32] = []
        for entry in entries {
            guard let hosts = hosts(in: entry) else { return nil }
            for host in hosts where seen.insert(host).inserted { result.append(host) }
        }
        return result.count <= maxHosts ? result : nil
    }

    public static func isPrivate(_ address: UInt32) -> Bool {
        let a = address >> 24, b = (address >> 16) & 0xff
        return a == 10 || (a == 172 && (16...31).contains(b)) || (a == 192 && b == 168) || (a == 169 && b == 254)
    }

    static func parseIPv4(_ text: String) -> UInt32? {
        let octets = text.split(separator: ".", omittingEmptySubsequences: false)
        guard octets.count == 4 else { return nil }
        var value: UInt32 = 0
        for octet in octets {
            guard !octet.isEmpty, octet.count <= 3, octet.allSatisfy({ $0.isASCII && $0.isNumber }),
                  octet.count == 1 || octet.first != "0", let number = UInt32(octet), number <= 255 else { return nil }
            value = value << 8 | number
        }
        return value
    }

    static func format(_ address: UInt32) -> String {
        "\(address >> 24).\((address >> 16) & 0xff).\((address >> 8) & 0xff).\(address & 0xff)"
    }
}

/// Probe datagrams. Their content is untrusted, exactly like mDNS TXT data.
public enum ProbeMessage {
    public static let datagramSize = 512

    struct Request: Codable { var feige = "probe"; var v = 1; var nonce: String }

    struct Reply: Codable {
        var feige = "reply"; var v = 1; var nonce: String
        var name: String; var fp: String; var caps: String; var port: UInt16; var avatar: String?
    }

    public static func request(nonce: String) -> Data? {
        guard var data = try? JSONEncoder().encode(Request(nonce: nonce)), data.count <= datagramSize else { return nil }
        data.append(Data(count: datagramSize - data.count))
        return data
    }

    public static func reply(to request: Data, advertisement: UnicastProbe.Advertisement) -> Data? {
        guard request.count == datagramSize, let decoded: Request = decode(request), decoded.feige == "probe", decoded.v == 1,
              (Data(base64Encoded: decoded.nonce)?.count ?? 0) == 16 else { return nil }
        let reply = Reply(nonce: decoded.nonce, name: advertisement.name, fp: advertisement.fingerprint,
                          caps: advertisement.capabilities, port: advertisement.port, avatar: advertisement.avatarId)
        guard let data = try? JSONEncoder().encode(reply), data.count <= datagramSize else { return nil }
        return data
    }

    public static func peer(from datagram: Data, address: String) -> (peer: UnicastProbe.Peer, nonce: String)? {
        guard datagram.count <= datagramSize, let reply: Reply = decode(datagram), reply.feige == "reply", reply.v == 1,
              reply.port > 0, !reply.name.isEmpty, reply.name.utf8.count <= 128,
              !reply.name.unicodeScalars.contains(where: { CharacterSet.controlCharacters.contains($0) }),
              reply.fp.count == 64, reply.fp.allSatisfy(\.isHexDigit) else { return nil }
        let versions = Set(reply.caps.split(separator: ",").compactMap { UInt16($0.trimmingCharacters(in: .whitespaces)) })
            .filter { (4...6).contains($0) }
        guard !versions.isEmpty else { return nil }
        return (UnicastProbe.Peer(address: address, name: reply.name, fingerprint: reply.fp.uppercased(),
                                  protocolVersions: versions, port: reply.port, avatarId: reply.avatar), reply.nonce)
    }

    private static func decode<T: Decodable>(_ datagram: Data) -> T? {
        let end = datagram.lastIndex(where: { $0 != 0 }).map { datagram.index(after: $0) } ?? datagram.startIndex
        return try? JSONDecoder().decode(T.self, from: datagram[datagram.startIndex..<end])
    }
}

/// Finds Feige devices at user-configured addresses over one UDP socket. All state lives on `queue`.
public final class UnicastProbe: @unchecked Sendable {
    public static let defaultPort: UInt16 = 52526
    public static let hostsPerSecond = 50
    public static let repliesPerSecond = 100

    public struct Advertisement: Sendable {
        public var name: String
        public var fingerprint: String
        public var capabilities: String
        public var port: UInt16
        public var avatarId: String?
        public init(name: String, fingerprint: String, capabilities: String, port: UInt16, avatarId: String?) {
            self.name = name
            self.fingerprint = fingerprint
            self.capabilities = capabilities
            self.port = port
            self.avatarId = avatarId
        }
    }

    public struct Peer: Sendable, Equatable {
        public let address: String
        public let name: String
        public let fingerprint: String
        public let protocolVersions: Set<UInt16>
        public let port: UInt16
        public let avatarId: String?
    }

    private let queue = DispatchQueue(label: "com.feige.unicast-probe")
    private let advertisement: Advertisement
    private let respondsToProbes: Bool
    private let onPeer: @Sendable (Peer) -> Void
    private var descriptor: Int32 = -1
    private var source: DispatchSourceRead?
    private var nonces: [String] = []
    private var scanID = 0
    private var replyWindow = 0
    private var repliesInWindow = 0
    private var destinationPort: UInt16
    /// False when the fixed port was taken: this device can still probe others but cannot be found.
    public private(set) var canRespond = false

    public init(advertisement: Advertisement, respondsToProbes: Bool, port: UInt16 = UnicastProbe.defaultPort,
                onPeer: @escaping @Sendable (Peer) -> Void) {
        self.advertisement = advertisement
        self.respondsToProbes = respondsToProbes
        self.onPeer = onPeer
        destinationPort = port
    }

    deinit { stop() }

    /// Opens the socket. Falls back to an ephemeral port so outgoing probes keep working.
    public func start() throws {
        try queue.sync {
            guard descriptor < 0 else { return }
            let fd = socket(AF_INET, SOCK_DGRAM, 0)
            guard fd >= 0 else { throw POSIXError(.init(rawValue: errno) ?? .EIO) }
            _ = fcntl(fd, F_SETFD, FD_CLOEXEC)
            _ = fcntl(fd, F_SETFL, fcntl(fd, F_GETFL) | O_NONBLOCK)
            if Self.bind(fd, port: destinationPort) { canRespond = respondsToProbes }
            else if !Self.bind(fd, port: 0) {
                let code = errno
                close(fd)
                throw POSIXError(.init(rawValue: code) ?? .EIO)
            }
            descriptor = fd
            let source = DispatchSource.makeReadSource(fileDescriptor: fd, queue: queue)
            source.setEventHandler { [weak self] in self?.drain() }
            source.setCancelHandler { close(fd) }
            self.source = source
            source.resume()
        }
    }

    /// Stops any running scan and closes the socket.
    public func stop() {
        queue.async { [source] in source?.cancel() }
        queue.sync {
            scanID += 1
            source = nil
            descriptor = -1
        }
    }

    /// Probes `hosts` at the paced rate, then calls `completion`. A newer scan replaces this one.
    public func scan(hosts: [UInt32], completion: (@Sendable () -> Void)? = nil) {
        queue.async { [self] in
            scanID += 1
            var nonceBytes = [UInt8](repeating: 0, count: 16)
            arc4random_buf(&nonceBytes, 16)
            let nonce = Data(nonceBytes).base64EncodedString()
            // Late replies from the previous round still count.
            nonces = Array((nonces + [nonce]).suffix(2))
            guard let request = ProbeMessage.request(nonce: nonce) else { completion?(); return }
            step(id: scanID, hosts: hosts[...], request: request, completion: completion)
        }
    }

    private func step(id: Int, hosts: ArraySlice<UInt32>, request: Data, completion: (@Sendable () -> Void)?) {
        guard id == scanID, descriptor >= 0 else { completion?(); return }
        let batch = max(1, Self.hostsPerSecond / 10)
        for host in hosts.prefix(batch) { send(request, to: host.bigEndian, port: destinationPort.bigEndian) }
        let rest = hosts.dropFirst(batch)
        guard !rest.isEmpty else { completion?(); return }
        queue.asyncAfter(deadline: .now() + .milliseconds(100)) { [self] in
            step(id: id, hosts: rest, request: request, completion: completion)
        }
    }

    private func drain() {
        var buffer = [UInt8](repeating: 0, count: ProbeMessage.datagramSize + 1)
        while descriptor >= 0 {
            var from = sockaddr_in()
            var length = socklen_t(MemoryLayout<sockaddr_in>.size)
            let count = withUnsafeMutablePointer(to: &from) { pointer in
                pointer.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                    recvfrom(descriptor, &buffer, buffer.count, 0, $0, &length)
                }
            }
            guard count >= 0 else { return }
            let sourceAddress = UInt32(bigEndian: from.sin_addr.s_addr)
            guard count <= ProbeMessage.datagramSize,
                  ProbeTargets.isPrivate(sourceAddress) || sourceAddress >> 24 == 127 else { continue }
            handle(Data(buffer[0..<count]), address: sourceAddress, port: from.sin_port)
        }
    }

    private func handle(_ datagram: Data, address: UInt32, port: UInt16) {
        if let (peer, nonce) = ProbeMessage.peer(from: datagram, address: ProbeTargets.format(address)) {
            guard nonces.contains(nonce), peer.fingerprint != advertisement.fingerprint else { return }
            onPeer(peer)
        } else if canRespond, allowReply(), let reply = ProbeMessage.reply(to: datagram, advertisement: advertisement) {
            send(reply, to: address.bigEndian, port: port)
        }
    }

    private func allowReply() -> Bool {
        let window = Int(Date().timeIntervalSince1970)
        if window != replyWindow { replyWindow = window; repliesInWindow = 0 }
        repliesInWindow += 1
        return repliesInWindow <= Self.repliesPerSecond
    }

    /// `address` and `port` are already in network byte order.
    private func send(_ data: Data, to address: UInt32, port: UInt16) {
        var target = sockaddr_in()
        target.sin_len = UInt8(MemoryLayout<sockaddr_in>.size)
        target.sin_family = sa_family_t(AF_INET)
        target.sin_port = port
        target.sin_addr.s_addr = address
        _ = data.withUnsafeBytes { bytes in
            withUnsafePointer(to: &target) { pointer in
                pointer.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                    sendto(descriptor, bytes.baseAddress, bytes.count, 0, $0, socklen_t(MemoryLayout<sockaddr_in>.size))
                }
            }
        }
    }

    private static func bind(_ fd: Int32, port: UInt16) -> Bool {
        var address = sockaddr_in()
        address.sin_len = UInt8(MemoryLayout<sockaddr_in>.size)
        address.sin_family = sa_family_t(AF_INET)
        address.sin_port = port.bigEndian
        address.sin_addr.s_addr = INADDR_ANY
        return withUnsafePointer(to: &address) { pointer in
            pointer.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                Darwin.bind(fd, $0, socklen_t(MemoryLayout<sockaddr_in>.size)) == 0
            }
        }
    }
}
