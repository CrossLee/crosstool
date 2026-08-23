import Foundation

public enum HTTPRequestParser {
    private static let headerTerminator = Data("\r\n\r\n".utf8)

    public static func expectedRequestLength(in data: Data) throws -> Int? {
        guard let headerRange = data.range(of: headerTerminator) else {
            return nil
        }
        guard let headerString = String(data: data[..<headerRange.lowerBound], encoding: .utf8) else {
            throw HTTPParseError.malformedRequest
        }
        let headers = parseHeaderLines(headerString)
        if headers["transfer-encoding"]?.lowercased().contains("chunked") == true {
            throw HTTPParseError.unsupportedTransferEncoding
        }
        let contentLength: Int
        if let rawLength = headers["content-length"] {
            guard let parsed = Int(rawLength), parsed >= 0 else {
                throw HTTPParseError.invalidContentLength
            }
            contentLength = parsed
        } else {
            contentLength = 0
        }
        return headerRange.upperBound + contentLength
    }

    public static func parse(_ data: Data, remoteAddress: String? = nil) throws -> HTTPRequest {
        guard let headerRange = data.range(of: headerTerminator),
              let headerString = String(data: data[..<headerRange.lowerBound], encoding: .utf8) else {
            throw HTTPParseError.incomplete
        }
        let lines = headerString.components(separatedBy: "\r\n")
        guard let requestLine = lines.first else {
            throw HTTPParseError.malformedRequest
        }
        let requestParts = requestLine.split(separator: " ", maxSplits: 2).map(String.init)
        guard requestParts.count == 3 else {
            throw HTTPParseError.malformedRequest
        }

        let method = requestParts[0].uppercased()
        let target = requestParts[1]
        guard target.utf8.count <= 4 * 1024 else {
            throw HTTPParseError.malformedRequest
        }
        guard let components = URLComponents(string: target) else {
            throw HTTPParseError.malformedRequest
        }
        let path = components.percentEncodedPath.removingPercentEncoding ?? components.path
        var query: [String: String] = [:]
        for item in components.queryItems ?? [] {
            query[item.name] = item.value ?? ""
        }

        let headers = parseHeaderLines(headerString)
        let expectedLength = try expectedRequestLength(in: data) ?? data.count
        guard data.count >= expectedLength else {
            throw HTTPParseError.incomplete
        }
        let body = Data(data[headerRange.upperBound..<expectedLength])
        return HTTPRequest(
            method: method,
            target: target,
            path: path,
            query: query,
            headers: headers,
            body: body,
            remoteAddress: remoteAddress
        )
    }

    private static func parseHeaderLines(_ headerString: String) -> [String: String] {
        var headers: [String: String] = [:]
        for line in headerString.components(separatedBy: "\r\n").dropFirst() {
            guard let colon = line.firstIndex(of: ":") else { continue }
            let key = line[..<colon].trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
            let value = line[line.index(after: colon)...].trimmingCharacters(in: .whitespacesAndNewlines)
            headers[key] = value
        }
        return headers
    }
}
