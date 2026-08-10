# Crosio

`Crosio` 是一款原生 macOS 工具箱，面向课堂和小型局域网协作：老师在 Mac 上启动共享，学生无需安装 App，使用浏览器即可查看、下载、上传文件和发送文字。

## 下载

- [下载 Crosio v0.2.0 正式安装包（推荐）](https://github.com/CrossLee/crosstool/releases/download/v0.2.0/Crosio-0.2.0-macos-universal2.pkg)
- [下载 Crosio v0.2.0 ZIP 版本](https://github.com/CrossLee/crosstool/releases/download/v0.2.0/Crosio-0.2.0-macos-universal2.zip)
- [查看 v0.2.0 发布说明与校验文件](https://github.com/CrossLee/crosstool/releases/tag/v0.2.0)

系统要求：macOS 14 或更高版本。正式版同时支持 Apple Silicon 与 Intel Mac，不需要 Rosetta。

> 请下载文件名以 `Crosio-` 开头的 `.pkg` 或 `.zip`。GitHub 自动显示的 “Source code” 压缩包不是 App 安装包。

## 安装

1. 下载 `.pkg` 文件并双击。
2. 按安装器提示安装；Crosio 会安装到 `/Applications/Crosio.app`。
3. 在“应用程序”中打开 `Crosio`。
4. 首次截图会申请一次屏幕录制权限；若系统没有弹窗，按 App 提示打开“录屏与系统音频录制”。列表没有 Crosio 时点“+”选择 `/Applications/Crosio.app`，开启后完全退出并重新打开 App。

本次提供的 App 与 PKG 均已完成 Developer ID 签名、Apple 公证、staple 与 Gatekeeper 验证；同一 Release 提供 `SHA256SUMS.txt`，用于校验下载完整性。

从 v0.1.x 使用 PKG 升级时，安装器会先安装并验证新的 `Crosio.app`，再核验旧 `/Applications/crosstool.app` 的 Bundle ID、签名与开发团队；全部匹配后才移除旧实体。身份无法确认、签名不匹配或 App 仍在运行时不会删除并会中止安装。ZIP 内同样是 `Crosio.app`，但不执行升级清理，因此旧版用户建议使用 PKG。

## 主要功能

- 老师和学生上传的文件进入同一课堂公共列表
- 浏览器查看、下载、上传文件以及发送文字
- 区域、窗口、全屏截图
- 全局快捷键：`⌃⇧1` 区域、`⌃⇧2` 窗口、`⌃⇧3` 全屏
- 无账号、无云端服务，文件在当前 Mac 与局域网内流转

## 使用提示

- 学生设备需要与老师的 Mac 连接同一局域网。
- 分享链接包含当前会话的随机令牌，请只发给课堂内可信人员。
- 公共列表会在 App 重启后清空；浏览器上传的原始文件仍保存在 Mac 的 Crosio 兼容接收箱 `~/Library/Application Support/crosstool/Inbox` 中。
- 当前单文件上传上限为 256 MB。

本仓库仅作为 Crosio 正式安装包的公开分发页，不代表项目源码仓库；仓库地址继续沿用 `CrossLee/crosstool` 以保持旧版链接可用。
