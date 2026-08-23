import Foundation

public struct MultipartFile: Equatable, Sendable {
    public let fieldName: String
    public let filename: String
    public let contentType: String?
    public let data: Data
}

public enum MultipartFormData {
    public static func boundary(from contentType: String) -> String? {
        for component in contentType.split(separator: ";") {
            let trimmed = component.trimmingCharacters(in: .whitespacesAndNewlines)
            guard trimmed.lowercased().hasPrefix("boundary=") else { continue }
            var value = String(trimmed.dropFirst("boundary=".count))
            if value.hasPrefix("\"") && value.hasSuffix("\"") {
                value.removeFirst()
                value.removeLast()
            }
            return value.isEmpty ? nil : value
        }
        return nil
    }

    public static func firstFile(in body: Data, boundary: String) -> MultipartFile? {
        let marker = Data("--\(boundary)".utf8)
        let separator = Data("\r\n\r\n".utf8)
        var searchStart = body.startIndex

        while let markerRange = body.range(of: marker, in: searchStart..<body.endIndex) {
            var partStart = markerRange.upperBound
            if body.count >= partStart + 2,
               body[partStart] == 45,
               body[partStart + 1] == 45 {
                break
            }
            if body.count >= partStart + 2,
               body[partStart] == 13,
               body[partStart + 1] == 10 {
                partStart += 2
            }
            guard let nextMarker = body.range(of: marker, in: partStart..<body.endIndex) else {
                break
            }
            var partEnd = nextMarker.lowerBound
            if partEnd >= 2, body[partEnd - 2] == 13, body[partEnd - 1] == 10 {
                partEnd -= 2
            }
            let part = Data(body[partStart..<partEnd])
            if let headerRange = part.range(of: separator),
               let headerText = String(data: part[..<headerRange.lowerBound], encoding: .utf8),
               let dispositionLine = headerText.components(separatedBy: "\r\n")
                    .first(where: { $0.lowercased().hasPrefix("content-disposition:") }),
               let filename = quotedValue(named: "filename", in: dispositionLine) {
                let fieldName = quotedValue(named: "name", in: dispositionLine) ?? "file"
                let contentType = headerText.components(separatedBy: "\r\n")
                    .first(where: { $0.lowercased().hasPrefix("content-type:") })
                    .flatMap { line -> String? in
                        guard let colon = line.firstIndex(of: ":") else { return nil }
                        return line[line.index(after: colon)...].trimmingCharacters(in: .whitespacesAndNewlines)
                    }
                return MultipartFile(
                    fieldName: fieldName,
                    filename: filename,
                    contentType: contentType,
                    data: Data(part[headerRange.upperBound...])
                )
            }
            searchStart = nextMarker.upperBound
        }
        return nil
    }

    private static func quotedValue(named name: String, in line: String) -> String? {
        let prefix = "\(name)=\""
        guard let range = line.range(of: prefix, options: .caseInsensitive) else { return nil }
        let start = range.upperBound
        guard let end = line[start...].firstIndex(of: "\"") else { return nil }
        return String(line[start..<end])
    }
}
