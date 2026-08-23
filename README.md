# Crosio

Crosio 是一款本地优先的原生 macOS 工具箱，集截图与标注、屏幕录制、屏幕取色、本机翻译和课堂局域网共享于一体。无需账号或云端服务；学生无需安装 App，使用浏览器即可参与文件和文字互传。

## 下载

- [DMG 安装包（推荐）](https://github.com/CrossLee/crosstool/releases/download/v0.4.0/Crosio-0.4.0-macos-universal2.dmg)
- [PKG 安装包](https://github.com/CrossLee/crosstool/releases/download/v0.4.0/Crosio-0.4.0-macos-universal2.pkg)
- [ZIP 备用包](https://github.com/CrossLee/crosstool/releases/download/v0.4.0/Crosio-0.4.0-macos-universal2.zip)
- [v0.4.0 发布说明与 SHA-256 校验](https://github.com/CrossLee/crosstool/releases/tag/v0.4.0)

> 只想安装 Crosio 时，请下载文件名以 `Crosio-` 开头的 DMG、PKG 或 ZIP。GitHub 自动生成的“Source code”压缩包不是可直接运行的安装包。

## 系统要求

- macOS 14 或更高版本
- Universal 2：同时支持 Apple Silicon 和 Intel Mac，无需 Rosetta
- 本机文本翻译需要 macOS 15 或更高版本；macOS 14 可继续使用其他功能

## 主要功能

- 区域、窗口、全屏、5 秒延时、带壳、多窗口和长截图
- 截图完成后立即复制原图，再进入标注编辑器
- 画笔、马赛克、矩形、箭头、撤销、重做、复制、另存和明确加入课堂共享区
- 将截图创建为独立置顶小贴图，支持快捷键、拖动和鼠标滚轮或触控板缩放
- 录制当前屏幕、窗口或框选区域，可选系统音频和鼠标光标
- 实时屏幕取色，提供 HEX、RGB、HSL 和最近颜色
- macOS 15 本机双栏翻译，以及“选中文字 → 快捷键 → 自动中英互译”
- 自定义全局快捷键
- 课堂局域网公共共享盘：浏览器查看、下载、上传文件和发送文字
- 菜单栏常驻，运行时不占用 Dock 或 `Command+Tab`

## 安装

安装或升级前，请先退出正在运行的 Crosio。

### DMG（推荐）

双击打开 DMG，将 `Crosio.app` 拖入“应用程序”，然后从 `/Applications/Crosio.app` 启动。

### PKG

适合学校或企业集中部署，也适合仍安装着 v0.1.x `/Applications/crosstool.app` 的用户。安装器会在核验旧 App 身份后安全迁移。

### ZIP

解压后手动将 `Crosio.app` 放入“应用程序”。ZIP 不执行 v0.1.x 旧实体迁移。

## 权限与隐私

- 截图、录屏和 ScreenCaptureKit 实时取色需要“屏幕与系统音频录制”权限；取色在无权限或实时流失败时会回退系统取色器。
- 只有读取其他 App 当前选中文字的快捷翻译需要“辅助功能”权限；普通翻译、快捷键录入和注册不需要该权限。
- 课堂共享需要本地网络访问，首次使用时 macOS 防火墙或本地网络权限可能弹出确认。
- macOS 15 的翻译使用 Apple Translation 本机能力，不接入 Crosio 云端翻译服务。
- 当前录屏不采集麦克风。
- 截图、录屏和翻译历史默认保存在本机；截图不会因为自动复制而自动公开。
- 浏览器上传内容会进入当前公共列表，并保存在本机接收箱。持有当前分享链接的人可以查看、下载和上传，请只分享给可信参与者。
- 停止共享会立即停止本地 HTTP 服务。

## 本地数据

Crosio 数据保存在：

```text
~/Library/Application Support/crosstool
```

其中包含接收箱、截图、录屏及其受管草稿。当前单文件上传上限为 256 MB。

## 从源码构建

需要 Swift 6.1 / Xcode 16.3 或更高版本：

```bash
swift test
./scripts/build-app.sh
```

开发 App 输出到 `dist/development/Crosio.app`。

开发脚本会优先使用钥匙串中的 Apple Development 证书；没有开发证书时会自动使用 ad-hoc 签名。需要指定稳定身份时，可设置 `CROSSTOOL_CODESIGN_IDENTITY`。

正式签名、公证与安装介质构建需要 Developer ID 证书及已配置的 `notarytool` 钥匙串 profile：

```bash
CROSSTOOL_NOTARY_PROFILE=<profile> ./scripts/build-release.sh
```

## 校验下载

将安装包与 `SHA256SUMS.txt` 放在同一目录后执行：

```bash
shasum -a 256 -c SHA256SUMS.txt
```
