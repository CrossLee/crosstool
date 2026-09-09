# macOS / Windows 主干合并验证

日期：2026-09-09。

## 合并范围

- 双父合并提交：`f4c04726cd8561782f6261598863cb1e1f4a590e`。
- 原 main：`c81bfbf5296fe50e7b2939c7726917ca6e096ba1`。
- Windows 分支：`8eb92e18b5111cf640843d5c1c9cf73ac117c77f`。
- CI 环境修正：`b690209019f51dc01295b57ad0856690f6b26e78`。
- AppKit 测试夹具初始化修正：`e746f6219a8dc5a8282f5392a2b810efec8d4224`。
- 另一个 Escape 测试的独立初始化修正：`76327190c1734d68ed68d323db4b781d715a3337`。

两端现共同维护于 `main`；Windows 源码保留在 `windows/`。旧 Windows 分支和提交历史保留，没有删除。原本地未跟踪的 Windows 旧副本与流水线已完整归档后再同步主工作区，未覆盖或删除其内容。

逐文件核对确认：macOS 产品的 `Sources/`、`Resources/`、`Package.swift` 相对原 main 无差异；Windows 产品代码、测试、安装器、Shell 扩展相对原 Windows 提交无差异。调整集中于文档、CI 与检查脚本，并修复两处贴图测试夹具的 AppKit 初始化和窗口关闭生命周期；没有删除行为断言或跳过测试。README 与共享网页冲突采用 main 最新内容，当前文档入口统一到主干目录。

## 本机验证

- 动态发现并逐组核对 193/193 项测试：22 组，XCTest 4 项、Swift Testing 189 项。
- 回归入口的提前退出、零匹配、计数、崩溃及超时等门禁测试：20/20。
- 安装器迁移隔离夹具：44/44。
- strict concurrency、warnings-as-errors 的 release 编译通过。
- ad-hoc 开发 App 构建及签名完整性验证通过；没有启动或安装该开发包。
- 两个原先依赖其他用例初始化 `NSApp` 的贴图测试，修前分别在新进程单独运行复现 nil 崩溃，修后各自完整 1/1 通过，再跑全量分组回归核对总数。
- Windows 工作流契约与 LF/CRLF 真实文件输入测试：15/15，含 1,200 个发布条件组合、16 个版本号边界输入，以及两种换行下拒绝默认公开发布的负向测试。
- 两条工作流均通过官方 actionlint 1.7.12。本机未提供 ShellCheck/Pyflakes；内嵌 Bash 另行通过语法检查。

## 云端验证

失败运行及后续修正如下，不将未完成运行计为全部测试通过：

- [首次 macOS CI](https://github.com/CrossLee/onepaw/actions/runs/34341164239)：Xcode 16.4 SDK 不能解析现有 macOS 26 翻译错误 API，测试尚未执行。现固定 Xcode 26.3；运行部署目标仍为 macOS 14。
- [首次 Windows CI](https://github.com/CrossLee/onepaw/actions/runs/34341164297)：新增政策检查未规范化 Windows CRLF。现已规范化，并用实际 LF/CRLF 文件运行原入口进行回归。
- [第二次 macOS CI](https://github.com/CrossLee/onepaw/actions/runs/34341434420)：已进入真实测试，贴图套件中“临时隐藏窗口恢复”用例崩溃。本机单独运行该用例，明确复现其首次读取 `NSApp.windows` 时 `NSApp` 为 nil；修正夹具为显式初始化 `NSApplication.shared`，并对由 Swift 持有的普通测试窗口关闭自动释放。远端日志未提供精确崩溃栈，不将本地复现位置冒充远端栈证据。

### macOS：通过

[最终 macOS CI](https://github.com/CrossLee/onepaw/actions/runs/34342062980)，提交 `76327190c1734d68ed68d323db4b781d715a3337`，终态 `success`。

- 下载并读回 `summary.json`：193/193，22 组全部通过，XCTest 4 + Swift Testing 189。
- 回归门禁测试 20/20、安装迁移 44/44。
- strict concurrency + warnings-as-errors release 编译通过。
- ad-hoc App 构建通过，无启动、安装或公证步骤。

### Windows：通过

[最终 Windows CI #23](https://github.com/CrossLee/onepaw/actions/runs/34341434416)，提交 `b690209019f51dc01295b57ad0856690f6b26e78`，终态 `success`。后续提交只修改 Mac 测试和仓库文档；Windows 源码、资源、构建与工作流输入未变，因此没有重复运行 Windows 流程。

- 产品测试 293/293，0 失败、0 跳过：Core 39、Intelligence 14、Translation 36、Sharing 32、Hotkeys 13、LongCapture 22、Media 57、Capture 52、Platform 28。
- 工作流契约与换行兼容回归 15/15。
- 原生 Shell/剪贴板检查、WinUI 编译、测试载荷及启动检查通过。
- x64/ARM64 MSIX、Bundle 与中文 Setup 构建通过，版本 `0.1.23.0` 仅为本次 CI 测试构建。
- Windows 11 ARM64 实际完成两次图形向导（首次安装与同版本重复安装）、注册 AUMID 启动及主窗口验证；验收日志确认测试进程停止并移除测试包。
- `publish-preview` 为 `skipped`，没有创建预览 Release。

## 发布与验收边界

本轮没有创建 Release、向 Release 上传附件、替换本机已安装 App，或更改持久化身份与数据目录。CI 构建产物仅作为该次 Actions 运行的测试 artifact 保留。Windows 普通 push/PR 的发布 job 必须跳过；预览发布要求 main 手动显式开启且构建和 ARM64 安装验收均成功。

自动测试、托管 Windows 安装验证和开发构建不等同于所有功能的用户实机验收，也不代表 Windows 可信签名或 macOS 新版本公证完成。现行流程见[仓库结构与发布约定](platform-workflow.md)。
