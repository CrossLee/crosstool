# crosstool

`crosstool` 是一款原生 macOS 工具箱，面向课堂和小型局域网协作：老师在 Mac 上启动共享，学生无需安装 App，使用浏览器即可查看、下载、上传文件和发送文字。

## 下载

- [下载正式安装包（推荐）](https://github.com/CrossLee/crosstool/releases/download/v0.1.0/crosstool-0.1.0-macos-universal2.pkg)
- [下载 ZIP 版本](https://github.com/CrossLee/crosstool/releases/download/v0.1.0/crosstool-0.1.0-macos-universal2.zip)
- [查看 v0.1.0 发布说明与校验文件](https://github.com/CrossLee/crosstool/releases/tag/v0.1.0)

系统要求：macOS 14 或更高版本。正式版同时支持 Apple Silicon 与 Intel Mac，不需要 Rosetta。

> 请下载文件名以 `crosstool-` 开头的 `.pkg` 或 `.zip`。GitHub 自动显示的 “Source code” 压缩包不是 App 安装包。

## 安装

1. 下载 `.pkg` 文件并双击。
2. 按安装器提示安装到“应用程序”。
3. 打开 `crosstool`。
4. 首次截图时，在“系统设置 → 隐私与安全性 → 屏幕与系统音频录制”中允许 `crosstool`，然后完全退出并重新打开 App。

安装包使用 Developer ID 签名，已通过 Apple 公证并装订公证票据。Release 同时提供 `SHA256SUMS.txt`，可用于校验下载完整性。

## 主要功能

- 老师和学生上传的文件进入同一课堂公共列表
- 浏览器查看、下载、上传文件以及发送文字
- 区域、窗口、全屏截图
- 全局快捷键：`⌃⇧1` 区域、`⌃⇧2` 窗口、`⌃⇧3` 全屏
- 无账号、无云端服务，文件在当前 Mac 与局域网内流转

## 使用提示

- 学生设备需要与老师的 Mac 连接同一局域网。
- 分享链接包含当前会话的随机令牌，请只发给课堂内可信人员。
- 公共列表会在 App 重启后清空；浏览器上传的原始文件仍保存在 Mac 的 crosstool 接收箱中。
- 当前单文件上传上限为 256 MB。

本仓库仅作为 crosstool 正式安装包的公开分发页，不代表项目源码仓库。
