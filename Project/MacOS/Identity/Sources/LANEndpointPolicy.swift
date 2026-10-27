import Foundation
import Network

/// Discovery is untrusted. Only local/private addresses may carry a LAN session.
public enum LANEndpointPolicy {
    public static func allows(_ endpoint: NWEndpoint) -> Bool {
        guard case .hostPort(let host, _) = endpoint else { return false }
        switch host {
        case .ipv4(let address):
            let b = [UInt8](address.rawValue)
            return b[0] == 10 || b[0] == 127 ||
                (b[0] == 172 && (16...31).contains(b[1])) ||
                (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254)
        case .ipv6(let address):
            let b = [UInt8](address.rawValue)
            if b.prefix(15).allSatisfy({ $0 == 0 }) && b[15] == 1 { return true }
            if b[0] & 0xfe == 0xfc || (b[0] == 0xfe && b[1] & 0xc0 == 0x80) { return true }
            if b.prefix(10).allSatisfy({ $0 == 0 }), b[10] == 0xff, b[11] == 0xff,
               let ipv4 = IPv4Address(Data(b.suffix(4))) {
                return allows(.hostPort(host: .ipv4(ipv4), port: 1))
            }
            return false
        default: return false
        }
    }
}
