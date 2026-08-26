import Foundation
import ServiceManagement
import Testing
@testable import CrossToolApp

@MainActor
@Suite("Login item settings model")
struct LoginItemSettingsTests {
    @Test("Service status maps a first registration according to app location")
    func serviceStatusMapping() {
        #expect(
            LoginItemStatus(
                serviceStatus: .notFound,
                isEligibleInstallation: true
            ) == .disabled
        )
        #expect(
            LoginItemStatus(
                serviceStatus: .notFound,
                isEligibleInstallation: false
            ) == .unavailable
        )
        #expect(
            LoginItemStatus(
                serviceStatus: .notRegistered,
                isEligibleInstallation: true
            ) == .disabled
        )
        #expect(
            LoginItemStatus(
                serviceStatus: .enabled,
                isEligibleInstallation: true
            ) == .enabled
        )
        #expect(
            LoginItemStatus(
                serviceStatus: .requiresApproval,
                isEligibleInstallation: true
            ) == .requiresApproval
        )
    }

    @Test("Only packaged apps in an Applications directory are eligible")
    func loginItemInstallationEligibility() {
        let homeDirectory = URL(fileURLWithPath: "/Users/tester", isDirectory: true)

        #expect(
            LoginItemInstallation.isEligible(
                bundleURL: URL(fileURLWithPath: "/Applications/Crosio.app"),
                homeDirectory: homeDirectory
            )
        )
        #expect(
            LoginItemInstallation.isEligible(
                bundleURL: URL(fileURLWithPath: "/Users/tester/Applications/Crosio.app"),
                homeDirectory: homeDirectory
            )
        )
        #expect(
            !LoginItemInstallation.isEligible(
                bundleURL: URL(fileURLWithPath: "/Users/tester/Downloads/Crosio.app"),
                homeDirectory: homeDirectory
            )
        )
        #expect(
            !LoginItemInstallation.isEligible(
                bundleURL: URL(fileURLWithPath: "/Applications/Crosio"),
                homeDirectory: homeDirectory
            )
        )
    }

    @Test("Initial system status drives every published and derived value")
    func initialStatusMatrix() {
        let cases: [(
            status: LoginItemStatus,
            isRequested: Bool,
            canChange: Bool
        )] = [
            (.disabled, false, true),
            (.enabled, true, true),
            (.requiresApproval, true, true),
            (.unavailable, false, false),
        ]

        for testCase in cases {
            let manager = FakeLoginItemManager(status: testCase.status)
            let model = LoginItemSettingsModel(manager: manager)

            #expect(model.status == testCase.status)
            #expect(model.errorMessage == nil)
            #expect(model.isRequested == testCase.isRequested)
            #expect(model.canChange == testCase.canChange)
            #expect(!model.statusMessage.isEmpty)
        }

        let disabledMessage = LoginItemSettingsModel(
            manager: FakeLoginItemManager(status: .disabled)
        ).statusMessage
        let enabledMessage = LoginItemSettingsModel(
            manager: FakeLoginItemManager(status: .enabled)
        ).statusMessage
        let approvalMessage = LoginItemSettingsModel(
            manager: FakeLoginItemManager(status: .requiresApproval)
        ).statusMessage
        let unavailableMessage = LoginItemSettingsModel(
            manager: FakeLoginItemManager(status: .unavailable)
        ).statusMessage

        #expect(disabledMessage.contains("启动"))
        #expect(enabledMessage.contains("开启"))
        #expect(approvalMessage.contains("系统"))
        #expect(unavailableMessage.contains("登录项"))
        #expect(Set([
            disabledMessage,
            enabledMessage,
            approvalMessage,
            unavailableMessage,
        ]).count == 4)
    }

    @Test("Enabling and disabling follow the system-reported status")
    func enableAndDisable() {
        let manager = FakeLoginItemManager(status: .disabled)
        let model = LoginItemSettingsModel(manager: manager)

        model.setRequested(true)

        #expect(manager.registerCallCount == 1)
        #expect(manager.unregisterCallCount == 0)
        #expect(model.status == .enabled)
        #expect(model.isRequested)
        #expect(model.errorMessage == nil)

        model.setRequested(false)

        #expect(manager.registerCallCount == 1)
        #expect(manager.unregisterCallCount == 1)
        #expect(model.status == .disabled)
        #expect(!model.isRequested)
        #expect(model.errorMessage == nil)
    }

    @Test("Registration awaiting approval remains requested and changeable")
    func registrationRequiresApproval() {
        let manager = FakeLoginItemManager(status: .disabled)
        manager.statusAfterRegister = .requiresApproval
        let model = LoginItemSettingsModel(manager: manager)

        model.setRequested(true)

        #expect(manager.registerCallCount == 1)
        #expect(model.status == .requiresApproval)
        #expect(model.isRequested)
        #expect(model.canChange)
        #expect(model.errorMessage == nil)
        #expect(model.statusMessage.contains("系统"))
    }

    @Test("Requests already satisfied by system truth are idempotent")
    func satisfiedRequestsAreIdempotent() {
        let manager = FakeLoginItemManager(status: .enabled)
        let model = LoginItemSettingsModel(manager: manager)

        model.setRequested(true)
        model.setRequested(true)

        #expect(manager.registerCallCount == 0)
        #expect(manager.unregisterCallCount == 0)

        manager.status = .requiresApproval
        model.setRequested(true)

        #expect(model.status == .requiresApproval)
        #expect(manager.registerCallCount == 0)
        #expect(manager.unregisterCallCount == 0)

        manager.status = .disabled
        model.setRequested(false)

        #expect(model.status == .disabled)
        #expect(manager.registerCallCount == 0)
        #expect(manager.unregisterCallCount == 0)
    }

    @Test("Unavailable login items cannot attempt registration changes")
    func unavailableStatusCannotChange() {
        let manager = FakeLoginItemManager(status: .unavailable)
        let model = LoginItemSettingsModel(manager: manager)

        model.setRequested(true)
        model.setRequested(false)

        #expect(model.status == .unavailable)
        #expect(!model.isRequested)
        #expect(!model.canChange)
        #expect(model.errorMessage == nil)
        #expect(manager.registerCallCount == 0)
        #expect(manager.unregisterCallCount == 0)
    }

    @Test("A thrown operation still accepts the resulting system truth")
    func thrownOperationUsesSystemTruth() {
        let manager = FakeLoginItemManager(status: .disabled)
        manager.statusAfterRegister = .enabled
        manager.registerError = FakeLoginItemFailure()
        let model = LoginItemSettingsModel(manager: manager)

        model.setRequested(true)

        #expect(manager.registerCallCount == 1)
        #expect(model.status == .enabled)
        #expect(model.isRequested)
        #expect(model.errorMessage == nil)

        manager.statusAfterUnregister = .disabled
        manager.unregisterError = FakeLoginItemFailure()
        model.setRequested(false)

        #expect(manager.unregisterCallCount == 1)
        #expect(model.status == .disabled)
        #expect(!model.isRequested)
        #expect(model.errorMessage == nil)
    }

    @Test("A failed operation reports an error when system truth did not change")
    func failedOperationReportsMismatch() {
        let manager = FakeLoginItemManager(status: .disabled)
        manager.statusAfterRegister = .disabled
        manager.registerError = FakeLoginItemFailure()
        let model = LoginItemSettingsModel(manager: manager)

        model.setRequested(true)

        #expect(manager.registerCallCount == 1)
        #expect(model.status == .disabled)
        #expect(!model.isRequested)
        #expect(model.errorMessage == "无法更新开机启动：测试失败")
    }

    @Test("A successful call without a matching system status is not optimistic")
    func successfulCallWithoutStatusChangeReportsMismatch() {
        let manager = FakeLoginItemManager(status: .disabled)
        manager.statusAfterRegister = .disabled
        let model = LoginItemSettingsModel(manager: manager)

        model.setRequested(true)

        #expect(manager.registerCallCount == 1)
        #expect(model.status == .disabled)
        #expect(!model.isRequested)
        #expect(model.errorMessage == "系统没有确认新的开机启动状态，请稍后重试。")
    }

    @Test("Refresh adopts external status changes and clears stale errors")
    func refreshUsesLatestSystemStatus() {
        let manager = FakeLoginItemManager(status: .disabled)
        manager.statusAfterRegister = .disabled
        manager.registerError = FakeLoginItemFailure()
        let model = LoginItemSettingsModel(manager: manager)

        model.setRequested(true)
        #expect(model.errorMessage != nil)

        manager.status = .requiresApproval
        model.refresh()

        #expect(model.status == .requiresApproval)
        #expect(model.isRequested)
        #expect(model.canChange)
        #expect(model.errorMessage == nil)
    }

    @Test("Opening system settings is delegated to the injected manager")
    func openSystemSettingsDelegates() {
        let manager = FakeLoginItemManager(status: .requiresApproval)
        let model = LoginItemSettingsModel(manager: manager)

        model.openSystemSettings()
        model.openSystemSettings()

        #expect(manager.openSystemSettingsCallCount == 2)
    }
}

@MainActor
private final class FakeLoginItemManager: LoginItemManaging {
    var status: LoginItemStatus
    var statusAfterRegister: LoginItemStatus = .enabled
    var statusAfterUnregister: LoginItemStatus = .disabled
    var registerError: Error?
    var unregisterError: Error?

    private(set) var registerCallCount = 0
    private(set) var unregisterCallCount = 0
    private(set) var openSystemSettingsCallCount = 0

    init(status: LoginItemStatus) {
        self.status = status
    }

    func register() throws {
        registerCallCount += 1
        status = statusAfterRegister
        if let registerError {
            throw registerError
        }
    }

    func unregister() throws {
        unregisterCallCount += 1
        status = statusAfterUnregister
        if let unregisterError {
            throw unregisterError
        }
    }

    func openSystemSettings() {
        openSystemSettingsCallCount += 1
    }
}

private struct FakeLoginItemFailure: LocalizedError {
    var errorDescription: String? { "测试失败" }
}
