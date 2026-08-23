import Foundation
import Testing
@testable import CrossToolCore

@Test func parsesRequestLineHeadersQueryAndBody() throws {
    let body = Data("{\"text\":\"你好\"}".utf8)
    var requestData = Data("POST /api/text?token=abc%20123 HTTP/1.1\r\nHost: localhost\r\nContent-Type: application/json\r\nContent-Length: \(body.count)\r\n\r\n".utf8)
    requestData.append(body)

    #expect(try HTTPRequestParser.expectedRequestLength(in: requestData) == requestData.count)
    let request = try HTTPRequestParser.parse(requestData, remoteAddress: "127.0.0.1")
    #expect(request.method == "POST")
    #expect(request.path == "/api/text")
    #expect(request.query["token"] == "abc 123")
    #expect(request.headers["content-type"] == "application/json")
    #expect(request.body == body)
    #expect(request.remoteAddress == "127.0.0.1")
}

@Test func waitsForCompleteBody() throws {
    let partial = Data("POST /api/text HTTP/1.1\r\nContent-Length: 10\r\n\r\n123".utf8)
    let expected = try HTTPRequestParser.expectedRequestLength(in: partial)
    #expect(expected == partial.count + 7)
    #expect(throws: HTTPParseError.self) {
        try HTTPRequestParser.parse(partial)
    }
}
@Test func rejectsChunkedTransferEncoding() {
    let request = Data("POST /api/upload HTTP/1.1\r\nTransfer-Encoding: chunked\r\n\r\n".utf8)
    #expect(throws: HTTPParseError.self) {
        try HTTPRequestParser.expectedRequestLength(in: request)
    }
}
