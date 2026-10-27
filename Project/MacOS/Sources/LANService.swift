import Foundation
import Network

@MainActor
final class LANService {
    private var listener: NWListener?
    private var browser: NWBrowser?
    private let queue = DispatchQueue(label: "com.feige.discovery")
    private var generation = UUID()
    var onDevices: (([LANDevice]) -> Void)?
    var onStatus: ((String, UInt16?) -> Void)?
    var onConnection: ((NWConnection) -> Void)?

    func start(deviceName: String, fingerprint: String) throws {
        stop()
        let token = generation
        let serviceName = "Feige-" + fingerprint.replacingOccurrences(of: " ", with: "").prefix(20)
        let listener = try NWListener(using: .tcp, on: .any)
        listener.service = NWListener.Service(name: serviceName, type: "_feige._tcp",
                                              txtRecord: NWTXTRecord(["name": deviceName, "version": "3"]))
        listener.stateUpdateHandler = { [weak self, weak listener] state in
            let port = listener?.port?.rawValue
            Task { @MainActor in
                guard let self, self.generation == token else { return }
                switch state {
                case .ready: self.onStatus?("正在发现附近设备", port)
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
            let devices = results.compactMap { result -> LANDevice? in
                guard case .service(let name, _, _, _) = result.endpoint, name != serviceName else { return nil }
                var displayName = name
                if case .bonjour(let txt) = result.metadata {
                    guard txt.dictionary["version"] == "3" else { return nil }
                    if let value = txt.dictionary["name"], !value.isEmpty, value.utf8.count <= 128,
                       !value.unicodeScalars.contains(where: { CharacterSet.controlCharacters.contains($0) }) {
                        displayName = value
                    }
                }
                return LANDevice(serviceName: name, displayName: displayName, endpoint: result.endpoint)
            }.sorted { $0.displayName.localizedStandardCompare($1.displayName) == .orderedAscending }
            Task { @MainActor in
                guard let self, self.generation == token else { return }
                self.onDevices?(Array(devices.prefix(100)))
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

    func stop() {
        generation = UUID()
        listener?.cancel()
        browser?.cancel()
        listener = nil
        browser = nil
    }
}
