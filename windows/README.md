# Crosio Windows

这里是 Crosio 的 Windows 11 原生版本工程。它复用 macOS 版的产品行为和验收标准，但所有系统能力都使用 Windows 原生 API 重新实现，不直接移植 SwiftUI/AppKit 代码。

## 当前边界

已经落盘并接入工程的部分包括：

- WinUI 3 主程序、单实例激活、协议/文件/后台启动路由；
- 截图后立即复制原图、截图编辑器和置顶贴图的 Windows 模块；
- 3 秒倒计时的通用 Crosio 设备壳截图、用户勾选可见顶层窗口的多窗口合成，以及 12 项可编辑全局快捷键（前三项截图快捷键默认必填，其余默认留空）；
- 图片压缩、压缩后在资源管理器定位、启动项和通知区域模块；
- Windows 本地 OCR、UI Automation 读取当前选中文本；
- Windows Graphics Capture 录制屏幕、区域或窗口，本机 H.264/AAC MP4、系统声音和鼠标指针开关，以及录制后的播放、定位与局域网分享入口；
- 局域网文件/文字共享服务；
- 原生 `IExplorerCommand`：“复制路径”直接在资源管理器进程外的 COM surrogate 中完成，不弹 Crosio 主界面；
- 图片文件关联：“打开方式 → Crosio”走后台压缩激活；
- 单项目 MSIX 清单、x64/ARM64 原生 DLL 打包、MSIXBundle 脚本和 Windows CI。

翻译入口已经接入真实 Marian ONNX 推理引擎和协调器。界面可一键下载固定 revision、逐文件校验大小与 SHA-256 后安装中英双向模型，也保留选择本地模型 ZIP 的高级入口。仓库与安装包不捆绑数百 MB 的模型权重；首次下载需要联网，安装后原文与推理全程留在本机。所需方向未安装模型时，界面会明确提示，而不是假装翻译成功。当前模型卡许可按方向区分：中文到英文（zh-en）是 `CC-BY-4.0`，英文到简体中文（en-zh）是 `Apache-2.0`。模型包格式、安全校验与许可字段见 `src/Crosio.Windows.Translation/MODEL_PACK_FORMAT.md`。

这仍是 Windows 开发版源码，不等于已完成真机验收。当前开发机是 macOS，无法执行 WinUI 的 `XamlCompiler.exe`、MSVC v143、Windows Desktop testhost、MSIX 安装、资源管理器菜单、屏幕捕获和 OCR。仓库中的 Windows CI 会补齐编译与打包检查；安装后的交互仍需在 Windows 11 x64 和 ARM64 真机分别验收。

## 工程结构

```text
windows/
├── Crosio.Windows.sln
├── scripts/
│   ├── verify-windows.ps1        # 测试、Windows 编译、unpackaged 测试构建
│   ├── Test-AppStartup.ps1       # 实际启动 unpackaged 主程序并验证可见主窗
│   ├── Test-PackageLayout.ps1    # 清单/CLSID/打包载荷静态一致性检查
│   ├── build-msix.ps1            # 单架构 MSIX
│   ├── build-msixbundle.ps1      # x64 + ARM64 MSIXBundle
│   └── Install-Crosio.ps1        # 签名包或 unsigned CI 包安装入口
├── src/
│   ├── Crosio.Windows.App/
│   ├── Crosio.Windows.Core/
│   ├── Crosio.Windows.Capture/
│   ├── Crosio.Windows.LongCapture/
│   ├── Crosio.Windows.Hotkeys/
│   ├── Crosio.Windows.Media/
│   ├── Crosio.Windows.Platform/
│   ├── Crosio.Windows.Intelligence/
│   ├── Crosio.Windows.Translation/
│   ├── Crosio.Windows.Ocr/
│   ├── Crosio.Windows.Accessibility/
│   └── Crosio.Windows.Sharing/
├── shell/Crosio.Windows.ShellExtension/
└── tests/
```

## Windows 开发环境

- Windows 11（build 22000 或更高，x64 或 ARM64）；
- Visual Studio 2022，安装“.NET 桌面开发”“Windows 应用 SDK C#”和“使用 C++ 的桌面开发”；
- .NET 8 SDK；
- Windows 10/11 SDK 与 MSVC v143。

主应用使用 Windows App SDK `1.8.260804001`。MSIX 同时包含匹配架构的 .NET 8、Windows App Runtime，以及录屏模块所需的 VC143 C++ 运行库，不要求用户另外预装这些运行时。构建脚本从 Visual Studio 的对应 x64/ARM64 redistributable 目录复制完整 `Microsoft.VC143.CRT` DLL 集合，并在解包后核对录屏 DLL、VC++ 依赖和 PE 架构。正式包清单在 `src/Crosio.Windows.App/Package.appxmanifest`，其中已经合并：

- 图片文件关联；
- `CrosioStartupTask`；
- `windows.comServer`；
- 文件和文件夹的 `windows.fileExplorerContextMenus`。

Explorer 扩展采用系统 COM surrogate：`com:SurrogateServer` 直接注册 DLL 的 `com:Class`，不声明进程外 `Executable`/`Arguments`；可选 `AppId` 目前也不需要。清单、原生 CLSID、菜单 Verb 和包内 DLL 路径由 `Test-PackageLayout.ps1` 交叉检查。

图标暂时复用仓库现有的 `Resources/Brand/CrosioIcon.png`，打包时映射到 `Assets\CrosioIcon.png`，没有重新绘制品牌图。这里有一个明确的发布门槛：现有文件是 1024×1024，而清单同时把它用作 Windows 的 44×44 与 150×150 基准资产（Store Logo 也要求独立的 50×50 基准资产）。仓库没有用代码缩放或生成替代图片；Windows CI 必须执行真实打包/安装验证，但即使测试包可以生成，也不能把它当成图标资源已经符合发布规范。正式发布前需要由品牌源文件导出独立的 Windows 尺寸与 scale/targetsize 资产。

## 验证与开发构建

在 Windows Developer PowerShell 中：

```powershell
.\scripts\verify-windows.ps1 -Configuration Release -Platform x64
```

该命令依次执行打包结构静态检查、所有测试项目、Windows 专属模块编译、x64 原生 Explorer 扩展编译、WinUI 编译，以及一个 unpackaged 自包含测试构建。它不会把 unpackaged 目录冒充安装包。

生成 x64 unpackaged 目录后，可在隔离的 Windows runner 上执行真实启动 smoke：

```powershell
.\scripts\Test-AppStartup.ps1
```

脚本实际启动 `artifacts\publish\win-x64\Crosio.exe`，在 30 秒总预算内通过 PID 枚举可见顶层窗口，要求出现并稳定保持标题为 `Crosio` 的主窗口。它仅终止自己通过 `Start-Process` 启动并持有句柄的进程，不搜索或结束其他 Crosio 实例，也不操作注册表、配置和剪贴板。Windows App SDK 1.8 [支持 Windows Server 2022](https://learn.microsoft.com/windows/apps/windows-app-sdk/support)，因此该检查会在 GitHub `windows-2022` runner 上实际执行而不是跳过；但它只证明 unpackaged 主程序可以启动，Crosio 的发行与交互验收边界仍是 Windows 11 build 22000 以上。

只检查清单与 Explorer 扩展一致性：

```powershell
.\scripts\Test-PackageLayout.ps1
```

## 生成 MSIX / MSIXBundle

单独生成一个架构的包：

```powershell
.\scripts\build-msix.ps1 -Configuration Release -Architecture x64 -Version 0.1.1.0
.\scripts\build-msix.ps1 -Configuration Release -Architecture ARM64 -Version 0.1.1.0
```

一次生成 x64、ARM64 和组合安装包：

```powershell
.\scripts\build-msixbundle.ps1 -Configuration Release -Version 0.1.1.0
```

构建成功后的输出在 `windows\artifacts\release`。其中包括两个单架构 `.msix`、一个 `.msixbundle`、SHA-256 校验值、构建信息、安装脚本和 `THIRD_PARTY_NOTICES.md`；第三方声明也包含在每个 MSIX 及 unpackaged 测试目录内。

例如版本 `0.1.1.0` 在无证书时生成：

- `Crosio-Windows-0.1.1.0-x64-unsigned-test.msix`；
- `Crosio-Windows-0.1.1.0-arm64-unsigned-test.msix`；
- `Crosio-Windows-0.1.1.0-unsigned-test.msixbundle`。

有证书时相同三个文件把 `unsigned-test` 改为 `signed`。Actions 中对应的归档名称为 `Crosio-windows-<版本>-unsigned-test` 或 `Crosio-windows-<版本>-signed`，另有 `Crosio-windows-x64-test-build` unpackaged 归档。

无证书时生成的是文件名、Actions artifact 名和 `build-info.json` 都明确标记为 `unsigned-test` 的 Windows 11 测试包。它的 Publisher 含微软规定的 `OID.2.25` 标记；管理员 PowerShell 可运行同目录的：

```powershell
.\Install-Crosio.ps1
```

脚本只会对真正未签名的包使用 `Add-AppxPackage -AllowUnsigned`。若包有无效或不受信任的签名，它会停止并提示处理证书，不会绕过签名错误。unsigned 包只用于开发测试，不作为公开发行包。

有代码签名 PFX 时：

```powershell
.\scripts\build-msixbundle.ps1 `
  -Configuration Release `
  -Version 0.1.1.0 `
  -CertificatePath C:\secure\Crosio.pfx `
  -CertificatePassword $env:CROSIO_CERT_PASSWORD
```

脚本从证书读取 Subject，在构建期间写入临时清单，构建结束即恢复仓库清单；之后用 SHA-256 签名单架构包和 bundle。签名证书必须具有代码签名用途。密码不应写进仓库或命令历史。

## GitHub Actions

`.github/workflows/windows-ci.yml` 在 `windows/**` 变更、PR 和手动触发时运行：

1. 固定使用带 VS 2022/v143 ARM64 工具的 Windows 2022 runner，完成测试及 x64 编译验证；
2. 构建 x64 与 ARM64 MSIX；
3. 用 `MakeAppx` 生成 MSIXBundle；
4. 上传 unpackaged 测试目录和可安装包两组 artifacts。

未配置 secret 时，CI 上传 unsigned 测试包。仓库若配置下面两个 Actions secrets，非 PR 构建会输出签名包：

- `WINDOWS_CERTIFICATE_BASE64`：PFX 文件的 Base64；
- `WINDOWS_CERTIFICATE_PASSWORD`：PFX 密码，可为空字符串用于无密码 PFX。

CI 不会把证书提交到仓库，并在结束步骤删除 runner 上的临时 PFX。

## 安装后专项验收

Windows 真机至少需要逐项确认：

- 右键文件/文件夹只出现一个“复制路径”，多选顺序和 Unicode 路径正确，主界面不弹出；
- 图片“打开方式 → Crosio”立即后台压缩，后缀与真实编码不变，源文件不覆盖，并自动定位输出文件；
- 设置里的开机启动可启用/禁用，用户在系统设置禁用后不会被程序强行打开；
- 截图先写入剪贴板再显示编辑器，`S` 贴图、滚轮按指针缩放、`Esc` 只关闭贴图；
- 带壳截图倒计时、设备壳尺寸和保存/剪贴板结果正确；多窗口 `PrintWindow` 合成需覆盖 Chromium、UWP 和传统 Win32 窗口验证；
- 12 项全局快捷键可编辑、冲突时整组回滚，前三项必填且其余项目可以清除；
- 录屏在 x64/ARM64 上均能启动和完成 MP4，包内 `ScreenRecorderLib.dll` 与 VC143 CRT 的架构正确；Windows N/KN 版本需要先启用系统的 Media Feature Pack；
- Explorer 重启后菜单和托盘恢复；
- x64 与 ARM64 包内分别包含同架构的 `ShellExtensions\Crosio.Windows.ShellExtension.dll`。
