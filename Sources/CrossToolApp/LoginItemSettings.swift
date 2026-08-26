import AppKit
import Combine
import ServiceManagement
import SwiftUI

enum LoginItemStatus: Equatable, Sendable {
    case disabled
    case enabled
    case requiresApproval
    case unavailable

    init(
        serviceStatus: SMAppService.Status,
        isEligibleInstallation: Bool
    ) {
        switch serviceStatus {
        case .notRegistered:
            self = .disabled
        case .enabled:
            self = .enabled
        case .requiresApproval:
            self = .requiresApproval
        case .notFound:
            // A main app has no Background Task Management record before its
            // first registration, which macOS reports as `notFound`. It is an
            // ordinary off state when this process is an installed app.
            self = isEligibleInstallation ? .disabled : .unavailable
        @unknown default:
            self = .unavailable
        }
    }
}

enum LoginItemInstallation {
    static func isEligible(
        bundleURL: URL,
        homeDirectory: URL = FileManager.default.homeDirectoryForCurrentUser
    ) -> Bool {
        let resolvedBundleURL = bundleURL.resolvingSymlinksInPath().standardizedFileURL
        guard resolvedBundleURL.pathExtension.lowercased() == "app" else {
            return false
        }

        let applicationDirectories = [
            URL(fileURLWithPath: "/Applications", isDirectory: true),
            homeDirectory.appendingPathComponent("Applications", isDirectory: true),
        ]

        return applicationDirectories.contains { directory in
            let resolvedDirectory = directory.resolvingSymlinksInPath().standardizedFileURL
            return resolvedBundleURL.path.hasPrefix(resolvedDirectory.path + "/")
        }
    }
}

@MainActor
protocol LoginItemManaging {
    var status: LoginItemStatus { get }

    func register() throws
    func unregister() throws
    func openSystemSettings()
}

@MainActor
final class SystemLoginItemManager: LoginItemManaging {
    private let service: SMAppService
    private let isEligibleInstallation: Bool

    init(
        service: SMAppService = .mainApp,
        bundleURL: URL = Bundle.main.bundleURL
    ) {
        self.service = service
        self.isEligibleInstallation = LoginItemInstallation.isEligible(
            bundleURL: bundleURL
        )
    }

    var status: LoginItemStatus {
        LoginItemStatus(
            serviceStatus: service.status,
            isEligibleInstallation: isEligibleInstallation
        )
    }

    func register() throws {
        try service.register()
    }

    func unregister() throws {
        try service.unregister()
    }

    func openSystemSettings() {
        SMAppService.openSystemSettingsLoginItems()
    }
}

@MainActor
final class LoginItemSettingsModel: ObservableObject {
    @Published private(set) var status: LoginItemStatus
    @Published private(set) var errorMessage: String?

    private let manager: any LoginItemManaging

    init(manager: (any LoginItemManaging)? = nil) {
        let resolvedManager = manager ?? SystemLoginItemManager()
        self.manager = resolvedManager
        self.status = resolvedManager.status
    }

    var isRequested: Bool {
        status == .enabled || status == .requiresApproval
    }

    var canChange: Bool {
        status != .unavailable
    }

    var statusMessage: String {
        switch status {
        case .disabled:
            return "已关闭；Crosio 不会在登录 Mac 时自动启动。"
        case .enabled:
            return "已开启；登录 Mac 后会自动启动 Crosio。"
        case .requiresApproval:
            return "已登记，但需要在系统登录项中允许后才能自动启动。"
        case .unavailable:
            return "系统找不到可注册的 Crosio 登录项，请从“应用程序”文件夹运行正式版后重试。"
        }
    }

    var accessibilityValue: String {
        switch status {
        case .disabled:
            return "已关闭"
        case .enabled:
            return "已开启"
        case .requiresApproval:
            return "已开启，需要系统批准"
        case .unavailable:
            return "不可用"
        }
    }

    func setRequested(_ requested: Bool) {
        errorMessage = nil
        status = manager.status

        guard canChange, requested != isRequested else { return }

        do {
            if requested {
                try manager.register()
            } else {
                try manager.unregister()
            }
        } catch {
            status = manager.status
            guard !statusSatisfies(requested) else { return }
            errorMessage = "无法更新开机启动：\(error.localizedDescription)"
            return
        }

        status = manager.status
        if !statusSatisfies(requested) {
            errorMessage = "系统没有确认新的开机启动状态，请稍后重试。"
        }
    }

    func refresh() {
        status = manager.status
        errorMessage = nil
    }

    func openSystemSettings() {
        manager.openSystemSettings()
    }

    private func statusSatisfies(_ requested: Bool) -> Bool {
        if requested {
            return status == .enabled || status == .requiresApproval
        }
        return status == .disabled
    }
}

struct LoginItemSettingsSection: View {
    @ObservedObject var model: LoginItemSettingsModel

    var body: some View {
        Section("启动") {
            Toggle(
                "登录时自动启动 Crosio",
                isOn: Binding(
                    get: { model.isRequested },
                    set: { model.setRequested($0) }
                )
            )
            .disabled(!model.canChange)
            .accessibilityValue(model.accessibilityValue)

            Text(model.statusMessage)
                .font(.caption)
                .foregroundStyle(
                    model.status == .requiresApproval
                        ? Color.orange
                        : Color.secondary
                )

            if model.status == .requiresApproval {
                Button("打开登录项设置", systemImage: "gear") {
                    model.openSystemSettings()
                }
            }

            if let errorMessage = model.errorMessage {
                Label(errorMessage, systemImage: "exclamationmark.triangle.fill")
                    .font(.caption)
                    .foregroundStyle(.red)
            }
        }
        .onAppear {
            model.refresh()
        }
        .onReceive(
            NotificationCenter.default.publisher(
                for: NSApplication.didBecomeActiveNotification
            )
        ) { _ in
            model.refresh()
        }
    }
}
