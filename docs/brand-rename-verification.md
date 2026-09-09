# 一爪更名验证记录

日期：2026-09-09。验证对象为 macOS 0.6.6（47）及 Windows 0.1.21.0 更名开发版。此记录不是公开发行或本机升级成功的声明。

## macOS 代码与界面

- `swift test list` 共列出 193 项测试，按 22 个隔离组执行后 193/193 通过，逐组核对实际汇总数量。
- 分组包含 Core 80、应用配置 9、取色窗口 2、复制路径 10、问候语 1、图片压缩 24、登录项 12、菜单布局 1、贴图 19、自动复制 11、截图编辑窗口 10、OCR 4、独立快捷翻译/辅助功能测试 10。
- Core 测试中包含本机 HTTP 服务的浏览器流程测试，涵盖列表、下载、上传、文字与无效令牌拒绝。
- 本机单进程汇总测试有提前退出或崩溃现象，未把其退出码当作全部通过证据；分组执行均有完整测试汇总。此轮没有为规避该现象更改产品逻辑。
- `swift build -c release --arch arm64 --arch x86_64 -Xswiftc -strict-concurrency=complete -Xswiftc -warnings-as-errors` 成功，生成 Intel / Apple Silicon 双架构可执行文件。
- 开发包独立启动后核对了主窗口、菜单、设置、登录项说明、截图与取色页显示“一爪”。没有重新授权屏幕录制，也没有把首次授权页面当作截图功能验收。
- README 两张图片来自该开发包的原生窗口截图，未使用代码修改图片。采集后退出开发进程并撤销其系统应用注册，保留原安装版运行和用户设置。

## macOS 安装器与本地测试包

`./scripts/test-installer-migration.sh`：44/44 通过。包括新安装、两个旧实体、新名称升级、签名/身份冲突、符号链接、运行中拒绝、备份失败、事务中断、新版本防降级、中文 PKG 结构等。

其中 11 类 ACL 测试使用真实 macOS `chmod` 访问控制条目，覆盖允许的 root 写权限/只读/deny-delete 条目，以及被拒绝的非 root 写权限、继承写权限和元数据修改权限。普通迁移逻辑测试只在临时模拟卷中运行，root/签名检查在这些夹具中有替代实现；另有真实不受信签名拒绝和系统路径只读检查。**没有执行真实系统安装。**

`./scripts/build-release.sh --skip-notarization` 成功生成以下本地 QA 文件：

| 文件 | SHA-256 |
| --- | --- |
| `一爪-0.6.6-macos-universal2-unnotarized.zip` | `145a378aad3e514db586b7690d51dcbf7ffc8fa541ecc97b4c0a9a3dfdbeead8` |
| `一爪-0.6.6-macos-universal2-unnotarized.dmg` | `73af889858a5803676d560742ab23b964ea309cce4feec424152f0c96a7d06e8` |
| `一爪-0.6.6-macos-universal2-unnotarized.pkg` | `6d6be7a33487c83f57e08ca7afcc6f7dee31d90b147cc544180fbd0427b74e76` |

这三个文件不是下载区的正式安装包。App/DMG 使用 Developer ID Application，PKG 使用 Developer ID Installer，均含时间戳；逐层检查名称、兼容身份、图标、双架构、PKG 内安装脚本与预期元数据。DMG 挂载检查后已卸载，SHA-256 回读校验通过。**此轮明确跳过 Apple 公证、stapling 与 Gatekeeper 发行验收，因此不得将这些 QA 包公开为正式发行。**

## Windows

更名源码提交：[`58b582504b5d504d058533d9f335a824c8df63a6`](https://github.com/CrossLee/onepaw/commit/58b582504b5d504d058533d9f335a824c8df63a6)。

[Windows CI #21](https://github.com/CrossLee/onepaw/actions/runs/34332562611) 正在执行 x64/ARM64 构建、测试及 ARM64 安装检查，最终结果以该运行完成状态为准。它不满足 `Publish Windows preview` 提交标题门槛，不会自动公开 Release。Windows 可信签名未完成。

## 尚未包含的验收

- 未在当前 Mac 替换原安装应用，未验收真实旧名称 PKG 升级及实际登录重启后的自启动。
- 未在用户设备检查改名后的 Finder 缓存、复制路径冷启动、图片打开方式与旧权限迁移。
- 未公开 macOS 0.6.6 或 Windows 0.1.21.0 Release；README 保留真实已发布版本的原资产链接。
- 历史资产 URL、哈希、旧迁移路径和内部兼容标识按原记录保留，不做机械全文替换。
