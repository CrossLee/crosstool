import Foundation
import Network

public enum LocalHTTPServerState: Equatable, Sendable {
    case stopped
    case starting
    case running(port: UInt16)
    case failed(String)
}

public final class LocalHTTPServer: @unchecked Sendable {
    public typealias StateHandler = @Sendable (LocalHTTPServerState) -> Void

    private let router: HTTPRouter
    private let queue = DispatchQueue(label: "com.cross.crosstool.http-server", qos: .userInitiated)
    private let stateLock = NSLock()
    private var listener: NWListener?
    private var currentState: LocalHTTPServerState = .stopped
    private var stateHandler: StateHandler?
    private let requestedPort: UInt16
    private let maximumPortAttempts: Int
    private let maximumRequestBytes = SharedContentStore.maximumUploadBytes + 2 * 1_048_576
    private let maximumHeaderBytes = 64 * 1024

    public init(router: HTTPRouter, port: UInt16 = 5421, maximumPortAttempts: Int = 10) {
        self.router = router
        self.requestedPort = port
        self.maximumPortAttempts = max(1, maximumPortAttempts)
    }

    public func setStateHandler(_ handler: StateHandler?) {
        stateLock.lock()
        stateHandler = handler
        stateLock.unlock()
    }

    public func start() throws {
        stateLock.lock()
        let shouldStart = listener == nil
        stateLock.unlock()
        guard shouldStart else { return }

        try startListener(on: requestedPort, attempt: 0)
    }

    private func startListener(on rawPort: UInt16, attempt: Int) throws {
        guard let port = NWEndpoint.Port(rawValue: rawPort) else {
            throw NSError(domain: "CrossToolHTTPServer", code: 1, userInfo: [NSLocalizedDescriptionKey: "端口无效"])
        }
        let parameters = NWParameters.tcp
        parameters.allowLocalEndpointReuse = true
        parameters.acceptLocalOnly = true
        parameters.includePeerToPeer = true
        let newListener = try NWListener(using: parameters, on: port)

        newListener.stateUpdateHandler = { [weak self, weak newListener] state in
            guard let self, let newListener else { return }
            switch state {
            case .setup:
                self.updateState(.starting)
            case .waiting(let error):
                if self.isAddressInUse(error), attempt + 1 < self.maximumPortAttempts, rawPort < UInt16.max {
                    self.retry(after: newListener, on: rawPort + 1, attempt: attempt + 1)
                } else {
                    self.updateState(.failed(error.localizedDescription))
                    self.removeCurrentListener(newListener)
                    newListener.cancel()
                }
            case .ready:
                let actualPort = newListener.port?.rawValue ?? rawPort
                self.updateState(.running(port: actualPort))
            case .failed(let error):
                if self.isAddressInUse(error), attempt + 1 < self.maximumPortAttempts, rawPort < UInt16.max {
                    self.retry(after: newListener, on: rawPort + 1, attempt: attempt + 1)
                } else {
                    self.updateState(.failed(error.localizedDescription))
                    self.removeCurrentListener(newListener)
                    newListener.cancel()
                }
            case .cancelled:
                if self.removeCurrentListener(newListener) {
                    self.updateState(.stopped)
                }
            @unknown default:
                break
            }
        }
        newListener.newConnectionHandler = { [weak self] connection in
            self?.handle(connection)
        }

        stateLock.lock()
        listener = newListener
        stateLock.unlock()
        updateState(.starting)
        newListener.start(queue: queue)
    }

    public func stop() {
        stateLock.lock()
        let activeListener = listener
        listener = nil
        stateLock.unlock()
        activeListener?.cancel()
        updateState(.stopped)
    }

    private func handle(_ connection: NWConnection) {
        connection.stateUpdateHandler = { state in
            if case .failed = state {
                connection.cancel()
            }
        }
        connection.start(queue: queue)
        receive(on: connection, buffer: Data())
    }

    private func receive(on connection: NWConnection, buffer: Data) {
        connection.receive(minimumIncompleteLength: 1, maximumLength: 64 * 1024) { [weak self] data, _, isComplete, error in
            guard let self else {
                connection.cancel()
                return
            }

            var updatedBuffer = buffer
            if let data {
                updatedBuffer.append(data)
            }
            if updatedBuffer.count > self.maximumRequestBytes {
                self.send(.text("请求内容过大", statusCode: 413), on: connection)
                return
            }
            if let headerEnd = updatedBuffer.range(of: Data("\r\n\r\n".utf8))?.lowerBound {
                if headerEnd > self.maximumHeaderBytes {
                    self.send(.text("请求头过大", statusCode: 413), on: connection)
                    return
                }
            } else if updatedBuffer.count > self.maximumHeaderBytes {
                self.send(.text("请求头过大", statusCode: 413), on: connection)
                return
            }

            do {
                if let expectedLength = try HTTPRequestParser.expectedRequestLength(in: updatedBuffer),
                   updatedBuffer.count >= expectedLength {
                    let request = try HTTPRequestParser.parse(
                        updatedBuffer,
                        remoteAddress: self.remoteAddress(for: connection)
                    )
                    self.send(self.router.handle(request), on: connection)
                    return
                }
            } catch {
                self.send(.text(error.localizedDescription, statusCode: 400), on: connection)
                return
            }

            if isComplete || error != nil {
                self.send(.text("请求不完整", statusCode: 400), on: connection)
                return
            }
            self.receive(on: connection, buffer: updatedBuffer)
        }
    }

    private func send(_ response: HTTPResponse, on connection: NWConnection) {
        connection.send(content: response.serializedHead(), completion: .contentProcessed { headerError in
            guard headerError == nil else {
                connection.cancel()
                return
            }
            connection.send(content: response.body, completion: .contentProcessed { _ in
                connection.cancel()
            })
        })
    }

    private func remoteAddress(for connection: NWConnection) -> String? {
        switch connection.endpoint {
        case .hostPort(let host, _):
            return String(describing: host)
        default:
            return nil
        }
    }

    private func retry(after candidate: NWListener, on port: UInt16, attempt: Int) {
        guard removeCurrentListener(candidate) else { return }
        candidate.cancel()
        queue.async { [weak self] in
            guard let self else { return }
            do {
                try self.startListener(on: port, attempt: attempt)
            } catch {
                self.updateState(.failed(error.localizedDescription))
            }
        }
    }

    @discardableResult
    private func removeCurrentListener(_ candidate: NWListener) -> Bool {
        stateLock.lock()
        let wasCurrent = listener === candidate
        if wasCurrent {
            listener = nil
        }
        stateLock.unlock()
        return wasCurrent
    }

    private func isAddressInUse(_ error: NWError) -> Bool {
        if case .posix(let code) = error {
            return code == .EADDRINUSE
        }
        return false
    }

    private func updateState(_ state: LocalHTTPServerState) {
        stateLock.lock()
        currentState = state
        let handler = stateHandler
        stateLock.unlock()
        handler?(state)
    }
}
