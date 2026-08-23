import Darwin
import Foundation

public enum LocalNetworkAddress {
    public static func bestIPv4Address() -> String? {
        var interfacesPointer: UnsafeMutablePointer<ifaddrs>?
        guard getifaddrs(&interfacesPointer) == 0,
              let firstInterface = interfacesPointer else {
            return nil
        }
        defer { freeifaddrs(interfacesPointer) }

        var candidates: [(name: String, address: String)] = []
        var pointer: UnsafeMutablePointer<ifaddrs>? = firstInterface

        while let interface = pointer?.pointee {
            defer { pointer = interface.ifa_next }
            guard let socketAddress = interface.ifa_addr,
                  socketAddress.pointee.sa_family == UInt8(AF_INET) else {
                continue
            }

            let flags = Int32(interface.ifa_flags)
            guard flags & IFF_UP != 0, flags & IFF_LOOPBACK == 0 else {
                continue
            }

            var hostname = [CChar](repeating: 0, count: Int(NI_MAXHOST))
            let result = getnameinfo(
                socketAddress,
                socklen_t(socketAddress.pointee.sa_len),
                &hostname,
                socklen_t(hostname.count),
                nil,
                0,
                NI_NUMERICHOST
            )
            guard result == 0 else { continue }

            let name = String(cString: interface.ifa_name)
            let address = String(cString: hostname)
            candidates.append((name, address))
        }

        let preferredNames = ["en0", "en1", "en2", "bridge100"]
        for name in preferredNames {
            if let candidate = candidates.first(where: { $0.name == name && isPrivateIPv4($0.address) }) {
                return candidate.address
            }
        }
        return candidates.first(where: { isPrivateIPv4($0.address) })?.address
            ?? candidates.first?.address
    }

    public static func isPrivateIPv4(_ address: String) -> Bool {
        if address.hasPrefix("10.") || address.hasPrefix("192.168.") {
            return true
        }
        let parts = address.split(separator: ".").compactMap { Int($0) }
        return parts.count == 4 && parts[0] == 172 && (16...31).contains(parts[1])
    }
}
