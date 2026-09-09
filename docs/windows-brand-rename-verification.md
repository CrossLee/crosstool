# 一爪 Windows 更名验证记录

记录日期：2026-09-09。本记录只对应下面指定提交和 CI 运行，不沿用 `0.1.20.0` 的测试或发布凭据。

## 源码、版本与发布状态

- 仓库：`CrossLee/onepaw`；分支：`codex/windows-native-preview`。
- 源码提交：[`58b582504b5d504d058533d9f335a824c8df63a6`](https://github.com/CrossLee/onepaw/commit/58b582504b5d504d058533d9f335a824c8df63a6)。
- [Windows CI #21 / run 34332562611](https://github.com/CrossLee/onepaw/actions/runs/34332562611)，触发类型为 `push`，生成版本 `0.1.21.0`。
- `prepare-installer-tool`、`verify-windows`、`install-windows11-arm64` 均为 `success`；`publish-preview` 为 `skipped`。
- 本轮仅生成 Actions 测试产物，**没有创建 GitHub Release，没有安装到用户电脑，也没有完成可信代码签名**。核查时 `windows-v0.1.21.0-preview` Release 查询返回 404。

## Windows 自动测试

下面的结果来自本次 [verify-windows job](https://github.com/CrossLee/onepaw/actions/runs/34332562611/job/102404389077) 的九条测试汇总，不是此前 Mac 本地的 190 passed / 2 skipped 结果。

| 测试项目 | Passed | Failed | Skipped | 本地原始日志行 |
| --- | ---: | ---: | ---: | ---: |
| Core | 39 | 0 | 0 | 181 |
| Intelligence | 14 | 0 | 0 | 194 |
| Translation | 36 | 0 | 0 | 209 |
| Sharing | 32 | 0 | 0 | 222 |
| Hotkeys | 13 | 0 | 0 | 237 |
| LongCapture | 22 | 0 | 0 | 251 |
| Media | 57 | 0 | 0 | 264 |
| Capture | 52 | 0 | 0 | 277 |
| Platform | 28 | 0 | 0 | 290 |
| **合计** | **293** | **0** | **0** | |

Platform 的两项 Windows 文件系统测试在本次 CI 中实际执行，没有跳过：已有原图、旧 `-crosio` 副本和新 `-一爪` 副本不被覆盖；16 个并发输出使用不同的新名称并保留扩展名大小写。共享网页测试验证了“一爪”标题、品牌和页脚，并继续检查令牌访问控制。

## 构建与名称检查

- `Test-PackageLayout.ps1` 与新增 `Test-Branding.ps1` 在 Windows 上通过：当前显示名、中文脚本编码，以及包身份、启动注册和数据目录的兼容约束一致。
- x64 unpackaged 应用真实启动探测通过，原始日志第 503 行记录 `Title='一爪'`、`OSBuild=20348`。该步骤运行在 `windows-2022` 托管 runner，不是 Windows 11 x64 实体机的完整交互验收。
- 原生复制路径 smoke 通过：普通文件、文件夹、多选、`CF_UNICODETEXT`，且没有启动 `Crosio.exe`（日志第 447 行）。这不替代安装后资源管理器菜单位置和用户交互验收。
- x64、ARM64 MSIX 均构建和解包检查成功。每个包的 `DisplayName`、PE `ProductName / FileDescription` 必须为“一爪”，否则构建脚本会失败；[本次提交中的实际检查](https://github.com/CrossLee/onepaw/blob/58b582504b5d504d058533d9f335a824c8df63a6/windows/scripts/build-msix.ps1#L213)已在两次构建中通过。
- 58 份图标导出校验分别在源资源、x64 unpackaged 输出及 x64 / ARM64 解包内容中通过；两架构包的日志位置为第 3083、7240 行。没有重新设计图标。

### 现存 SDK 目标警告

CI 构建成功不等于零警告。`verify-windows.log` 第 766、1035、3451、5204 行记录了 `MSB3851`：主应用目标为 `Windows 10.0.19041.0`，所引用的 Shell DLL 在本次 runner 上解析为 `Windows 10.0.26100.0`；两架构的构建输出与汇总均出现该警告。

与本次更名前基线 `7fc56918c184189b9c44f062552a1071820422e3` 对比，App 的 `TargetFramework / TargetPlatformMinVersion` 仍为 `19041`，Shell 的 `WindowsTargetPlatformVersion` 仍为 `10.0`；Shell `.vcxproj` 无差异，App `.csproj` 仅新增产品显示元数据。因此这组目标配置不是本次改名引入的变化，本轮没有扩展修改它。该现存警告和 Windows 11 最低版本兼容性仍需单独验证，不能声称已覆盖所有受声明支持的 Windows 11 版本。

## ARM64 图形安装验收

本次 [Windows 11 ARM64 job](https://github.com/CrossLee/onepaw/actions/runs/34332562611/job/102406369684) 使用 Windows PowerShell 5.1 运行安装验收，没有通过终端安装命令替代点击向导。

1. 首次安装：日志第 119 行确认 GUI attempt 1 完成。
2. 同版本重复安装：日志第 120 行确认 GUI attempt 2 完成。**这不是从 `0.1.20.0` 升级的实测。**
3. 实际脚本按“一爪 安装”定位向导，核对完成页搜索提示和“立即打开 一爪”可选项；默认不勾选自动启动，安装结束后未意外启动应用。
4. 安装清单的包显示名、应用列表显示名和启动项显示名均强制等于“一爪”；应用启动后须出现精确标题“一爪”的稳定可见主窗口。[本次验收脚本](https://github.com/CrossLee/onepaw/blob/58b582504b5d504d058533d9f335a824c8df63a6/windows/scripts/Test-MsixInstallation.ps1#L1073)执行通过。
5. 日志第 121、122 行记录安装身份 `Crosio.Windows_0.1.21.0_arm64__a9eatqxyd72b4`，AUMID 为 `Crosio.Windows_a9eatqxyd72b4!App`；启动成功后，测试进程停止，测试包移除。

`Crosio.exe`、Package Name / Publisher / PFN / AUMID、协议、COM GUID、注册键和原有数据目录保留兼容，并非用户界面漏改。此验收证明托管 Windows 11 ARM64 环境中的安装与启动链路，不代表所有功能或实体设备已验收。

## 本次产物与哈希证据

[Actions 产物 `一爪-windows-0.1.21.0-unsigned-test`](https://github.com/CrossLee/onepaw/actions/runs/34332562611/artifacts/10096591299)：artifact ID `10096591299`，ZIP 大小 `801,933,179` 字节。上传和 ARM64 下载记录的 ZIP SHA-256 相同：

```text
3242e8e33c3aa5b840aedf4704a4417ee9a28de583d91be69072f24da6372232
```

ARM64 日志第 107、111 行分别给出预期和实际下载摘要。该下载一致性验证已完成；不是匿名 Release 回下载。

从同一 artifact 的 ZIP 目录和小型元数据条目只读提取，确认恰有 9 个文件：x64 MSIX、ARM64 MSIX、MSIXBundle、Setup.exe、`安装一爪.ps1`、`SHA256SUMS.txt`、`TESTING.md`、`THIRD_PARTY_NOTICES.md`、`build-info.json`。元数据条目的解压长度和 ZIP CRC 均核对。`build-info.json` 标明 `0.1.21.0`、x64 / ARM64、self-contained、`signed: false`、`artifactKind: unsigned-test`；内部 `product: Crosio` 保留为安装身份检查字段。

`SHA256SUMS.txt` 中的主要负载记录如下。这些是 **CI 生成的摘要**，本地没有重新下载和独立重算大型安装文件：

| 文件 | CI 记录的 SHA-256 |
| --- | --- |
| `一爪-Windows-0.1.21.0-Setup.exe` | `499d1114d00ef44963e345b8a5b59d1f7679283d2e9838ce393931cf61a17d8c` |
| `一爪-Windows-0.1.21.0-x64-unsigned-test.msix` | `0dc3121b4569ef188b77a2728509e261c7deff6faabe14e2d371f00ae5de2cc1` |
| `一爪-Windows-0.1.21.0-arm64-unsigned-test.msix` | `0b4745d9ceca6aa4734dd7d138acff150f76415bcbdaa9f6233de5f1b5138d73` |
| `一爪-Windows-0.1.21.0-unsigned-test.msixbundle` | `959fba7455ef726f5b1ceb478cc7179d1c112d791ccd1ff01acdbd04c647eb8a` |

Setup.exe 为 `267,192,443` 字节。清单的全部 7 个哈希条目名称与目录中的预期文件一致；另 3 项为安装脚本、第三方说明和测试指南。

验证边界：Setup 封装前，构建脚本复验 6 份原始负载哈希，再计算新增 Setup 哈希；实际安装器校验其内嵌 bundle。由于 `publish-preview` 本轮跳过，**未执行该发布 job 的 7 份负载整体复验、Release 上传及云端回下载**，不能将其写成已完成的公开发布门禁。

## 本地保存的独立证据

以下路径相对于 Windows 工作树根目录，仅用于本地留存，不应作为发布资产提交：

- `.artifacts/run-34332562611/verify-windows.log`：完整构建、测试与打包日志。
- `.artifacts/run-34332562611/install-windows11-arm64.log`：完整下载、两次 GUI 安装和启动日志。
- `.artifacts/run-34332562611/summary.json`：从上述原始日志解析的九组测试计数和 293 / 0 / 0 汇总，包含各组源日志行号。
- 同目录 `SHA256SUMS.txt`、`build-info.json`、`artifact-directory.json`：本次 artifact 提取的元数据和文件目录；没有混入历史 `0.1.20.0` 记录。

两份原始日志的本地 SHA-256：

```text
bdfaba0d5f777d911894d652ef650a73a70afbd09fb860253f2350111cf0212b  verify-windows.log
d1f8a0f26cfb58dfbecc217c257cb7c833d74344cc184a12b1799ead38a2eab9  install-windows11-arm64.log
```

## 尚未验收

旧版原位升级及用户设置/模型/文件保留、Windows 11 x64 图形安装、多屏与缩放、实际截图/录屏/OCR/翻译质量、资源管理器菜单交互和可信签名仍需对应验收。当前成功的代码、单元测试、构建和 ARM64 安装证据不替代这些项目；后续发布仍需另行授权。
