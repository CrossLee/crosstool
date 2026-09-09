# 同一主干，两端独立验证与发布

从本次合并起，macOS 与 Windows 统一在 `main` 维护。`codex/windows-native-preview` 保留历史提交，不再作为 Windows 当前源码或发行入口；日常功能使用短期分支，经测试和审查后合回 `main`。

本次代码完整性、测试、构建和云端运行凭据见[主干合并验证记录](platform-merge-verification.md)。

## 目录与共享边界

| 内容 | 位置 |
| --- | --- |
| macOS 原生程序和测试 | `Package.swift`、`Sources/`、`Tests/` |
| Windows 原生程序、测试与安装器 | `windows/` |
| 两端共用品牌图标原图 | `Resources/Brand/OnePaw-AppIcon.png` |
| 两端共用浏览器页面 | `Sources/CrossToolApp/Resources/Web/` |
| 产品首页、截图和验证记录 | `README.md`、`docs/` |
| 分平台 CI | `.github/workflows/macos-ci.yml`、`.github/workflows/windows-ci.yml` |

Windows 通过项目文件嵌入同一份 HTML/CSS/JS，因此公共网页的修改必须同时验证两端。此轮不搬迁 macOS 工程或网页路径，不重写 Swift/C# 实现，不改变 Bundle ID、Windows 包身份、数据目录和用户设置。

## 日常验证

- macOS：按实际发现的测试分组执行并核对汇总数量；继续检查安装器迁移逻辑与开发 App 构建。零匹配、测试进程提前结束或崩溃不能当作通过。
- Windows：保留 x64 自动测试、x64/ARM64 MSIX 与中文 Setup 构建、Windows 11 ARM64 图形安装和启动检查。
- 修改各自代码只运行相应平台流程；公共网页和品牌原图的修改同时触发两端。文档修改本身不发布安装包。
- CI 成功不等于真实用户设备全部功能验收、Apple 公证或 Windows 可信签名完成。

## 发布入口

**合并、普通 push、PR、默认手动测试均不会发布 Release。**

Windows 预览版只能在 `Windows` 工作流中从 `main` 手动运行，并显式设置布尔输入 `publish_preview=true`。构建验证和 ARM64 安装两个 job 均成功后，才验证同轮 artifact、精确文件清单与哈希，并执行“草稿上传 → 云端回下载核对 → 公开预览”。其他分支或未勾选发布均不会进入公开步骤。

预览版固定为未签名测试包；流水线不读取证书 secrets。保留 `windows-v…-preview` tag、prerelease 与 `latest=false`，不替代 Mac stable。可信 Windows 签名需要另行完成发布主体、密钥保护、完整签名链和旧包身份迁移。

macOS 正式发行仍使用本机受控的 `scripts/build-release.sh`，需要 Developer ID、公证与发行验收。Mac CI 仅生成开发构建，不调用公证或发布脚本。本次合并不创建任何 Release、不更新公开安装附件，也不替换本机安装。

## 保留历史

历史 Release 文件名、tag、哈希和针对固定提交的审计链接保持真实；不通过改旧包文件名冒充新版本。两端可以独立决定发行时机和版本号，不必等待另一平台功能或签名进度。
