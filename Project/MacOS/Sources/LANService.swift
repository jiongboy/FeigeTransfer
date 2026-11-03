import FeigeIdentity
import Foundation
import Network

@MainActor
final class LANService {
    private var listener: NWListener?
    private var browser: NWBrowser?
    private let queue = DispatchQueue(label: "com.feige.discovery")
    private var generation = UUID()
    private var probe: UnicastProbe?
    private var probeTask: Task<Void, Never>?
    private var bonjourDevices: [LANDevice] = []
    private var probedDevices: [String: ProbedDevice] = [:]
    var onDevices: (([LANDevice]) -> Void)?
    var onStatus: ((String, UInt16?) -> Void)?
    var onConnection: ((NWConnection) -> Void)?
    var onProbePortUnavailable: (() -> Void)?

    private struct ProbedDevice {
        let device: LANDevice
        var missedRounds = 0
        var seenThisRound = true
    }

    func start(deviceName: String, fingerprint: String, avatarId: String? = nil,
               probeTargets: [String] = [], allowsProbes: Bool = true) throws {
        stop()
        let token = generation
        let serviceName = "Feige-" + fingerprint.replacingOccurrences(of: " ", with: "").prefix(20)
        let txtValues = Self.txtRecordValues(deviceName: deviceName, fingerprint: fingerprint, avatarId: avatarId)
        let listener = try NWListener(using: .tcp, on: .any)
        listener.service = NWListener.Service(name: serviceName, type: "_feige._tcp",
                                              txtRecord: NWTXTRecord(txtValues))
        listener.stateUpdateHandler = { [weak self, weak listener] state in
            let port = listener?.port?.rawValue
            Task { @MainActor in
                guard let self, self.generation == token else { return }
                switch state {
                case .ready:
                    self.onStatus?("正在发现附近设备", port)
                    if let port {
                        self.startProbe(txtValues: txtValues, port: port, targets: probeTargets, allowsProbes: allowsProbes, token: token)
                    }
                case .waiting(let error), .failed(let error):
                    self.onStatus?("局域网服务不可用：\(error.localizedDescription)", nil)
                default: break
                }
            }
        }
        listener.newConnectionHandler = { [weak self] connection in
            Task { @MainActor in
                guard let self, self.generation == token else { connection.cancel(); return }
                self.onConnection?(connection)
            }
        }
        let browser = NWBrowser(for: .bonjourWithTXTRecord(type: "_feige._tcp", domain: "local."), using: .tcp)
        browser.browseResultsChangedHandler = { [weak self] results, _ in
            let discovered = results.compactMap { result -> LANDevice? in
                guard case .service(let name, _, _, _) = result.endpoint, name != serviceName else { return nil }
                var displayName = name
                var protocolVersions: Set<UInt16> = [3]
                var fingerprintHint: String?
                var avatarId: String?
                if case .bonjour(let txt) = result.metadata {
                    guard let parsedVersions = Self.protocolVersions(from: txt.dictionary) else { return nil }
                    protocolVersions = parsedVersions
                    fingerprintHint = AvatarID.normalizeFingerprint(txt.dictionary["fp"])
                    avatarId = Self.avatarId(from: txt.dictionary)
                    if let value = txt.dictionary["name"], !value.isEmpty, value.utf8.count <= 128,
                       !value.unicodeScalars.contains(where: { CharacterSet.controlCharacters.contains($0) }) {
                        displayName = value
                    }
                }
                return LANDevice(serviceName: name, displayName: displayName, endpoint: result.endpoint,
                                 protocolVersions: protocolVersions, fingerprintHint: fingerprintHint, avatarId: avatarId)
            }
            let v4Prefixes = Set(discovered.compactMap { device -> String? in
                guard device.serviceName.hasPrefix("Feige4-") else { return nil }
                return String(device.serviceName.dropFirst("Feige4-".count))
            })
            let devices = discovered.filter { device in
                guard device.serviceName.hasPrefix("Feige-") else { return true }
                return !v4Prefixes.contains(String(device.serviceName.dropFirst("Feige-".count)))
            }.sorted { $0.displayName.localizedStandardCompare($1.displayName) == .orderedAscending }
            Task { @MainActor in
                guard let self, self.generation == token else { return }
                self.bonjourDevices = devices
                self.publishDevices()
            }
        }
        browser.stateUpdateHandler = { [weak self] state in
            if case .failed(let error) = state {
                Task { @MainActor in
                    guard let self, self.generation == token else { return }
                    self.onStatus?("设备搜索失败：\(error.localizedDescription)", nil)
                }
            }
        }
        self.listener = listener
        self.browser = browser
        listener.start(queue: queue)
        browser.start(queue: queue)
    }

    nonisolated static func txtRecordValues(deviceName: String, fingerprint: String, avatarId: String?) -> [String: String] {
        var values = ["name": deviceName, "v": "3", "caps": "3,4,5,6",
                      "fp": fingerprint.replacingOccurrences(of: " ", with: "").uppercased()]
        if AvatarID.isKnown(avatarId), let avatarId { values["avatar"] = avatarId }
        return values
    }

    nonisolated static func avatarId(from txt: [String: String]) -> String? {
        guard let value = txt["avatar"], AvatarID.isKnown(value) else { return nil }
        return value
    }

    nonisolated static func protocolVersions(from txt: [String: String]) -> Set<UInt16>? {
        guard let rawVersion = txt["v"] ?? txt["version"],
              let version = UInt16(rawVersion), version == 3 || version == 4 || version == 5 else { return nil }
        var versions: Set<UInt16> = [version]
        if let rawCapabilities = txt["caps"] ?? txt["capabilities"] {
            for part in rawCapabilities.split(separator: ",") {
                if let capability = UInt16(part.trimmingCharacters(in: .whitespacesAndNewlines)),
                   (3...6).contains(capability) {
                    versions.insert(capability)
                }
            }
        }
        return versions
    }

    /// Merges discovery sources by fingerprint; Bonjour wins because it follows address changes.
    private func publishDevices() {
        let known = Set(bonjourDevices.compactMap(\.fingerprintHint))
        let probed = probedDevices.filter { !known.contains($0.key) }.values.map(\.device)
        let devices = (bonjourDevices + probed).sorted { $0.displayName.localizedStandardCompare($1.displayName) == .orderedAscending }
        onDevices?(Array(devices.prefix(100)))
    }

    private func startProbe(txtValues: [String: String], port: UInt16, targets: [String], allowsProbes: Bool, token: UUID) {
        guard probe == nil, let name = txtValues["name"], let fingerprint = txtValues["fp"], let capabilities = txtValues["caps"] else { return }
        let hosts = ProbeTargets.hosts(in: targets) ?? []
        guard allowsProbes || !hosts.isEmpty else { return }
        let advertisement = UnicastProbe.Advertisement(name: name, fingerprint: fingerprint, capabilities: capabilities,
                                                       port: port, avatarId: txtValues["avatar"])
        let probe = UnicastProbe(advertisement: advertisement, respondsToProbes: allowsProbes) { [weak self] peer in
            Task { @MainActor in
                guard let self, self.generation == token else { return }
                self.record(peer)
            }
        }
        do { try probe.start() } catch {
            onStatus?("跨网段探测不可用：\(error.localizedDescription)", port)
            return
        }
        self.probe = probe
        if allowsProbes && !probe.canRespond { onProbePortUnavailable?() }
        guard !hosts.isEmpty else { return }
        probeTask = Task { [weak self] in
            while !Task.isCancelled {
                await withCheckedContinuation { continuation in
                    probe.scan(hosts: hosts) { continuation.resume() }
                }
                try? await Task.sleep(for: .seconds(2))
                guard !Task.isCancelled else { return }
                self?.finishProbeRound()
                try? await Task.sleep(for: .seconds(58))
            }
        }
    }

    private func record(_ peer: UnicastProbe.Peer) {
        guard let port = NWEndpoint.Port(rawValue: peer.port) else { return }
        let endpoint = NWEndpoint.hostPort(host: NWEndpoint.Host(peer.address), port: port)
        guard LANEndpointPolicy.allows(endpoint) else { return }
        let avatar = peer.avatarId.flatMap { AvatarID.isKnown($0) ? $0 : nil }
        let device = LANDevice(serviceName: "Feige4-" + peer.fingerprint.prefix(20), displayName: peer.name, endpoint: endpoint,
                               protocolVersions: peer.protocolVersions, fingerprintHint: peer.fingerprint, avatarId: avatar)
        let changed = probedDevices[peer.fingerprint]?.device != device
        probedDevices[peer.fingerprint] = ProbedDevice(device: device)
        if changed { publishDevices() }
    }

    /// A device disappears after three rounds without a reply.
    private func finishProbeRound() {
        var removed = false
        for (fingerprint, var entry) in probedDevices {
            if entry.seenThisRound {
                entry.missedRounds = 0
            } else {
                entry.missedRounds += 1
            }
            entry.seenThisRound = false
            if entry.missedRounds >= 3 {
                probedDevices[fingerprint] = nil
                removed = true
            } else {
                probedDevices[fingerprint] = entry
            }
        }
        if removed { publishDevices() }
    }

    func stop() {
        generation = UUID()
        probeTask?.cancel()
        probeTask = nil
        probe?.stop()
        probe = nil
        bonjourDevices = []
        probedDevices = [:]
        listener?.cancel()
        browser?.cancel()
        listener = nil
        browser = nil
    }
}
