# Windows 可信签名与预览版迁移

核验日期：2026-09-09。本文是签名前置审计和实施清单，**不表示已取得 Windows 可信证书或已发布签名版**。产品品牌为一爪 OnePaw；现有 Windows 应用身份及安装文件名仍为 Crosio。本次仓库更名不改变应用包身份。

## 当前结论

GitHub 上的 [Windows 0.1.20.0 预览版](https://github.com/CrossLee/onepaw/releases/tag/windows-v0.1.20.0-preview) 仍未签名。要提供可信安装包，需要先确认合法发布主体和可用签名服务，再补齐应用、包和安装器的完整签名链路；不能仅打开现有 PFX 配置就宣称完成。

截至核验时，`CrossLee/onepaw` 的仓库 Actions secret 名称列表、variable 名称列表及 environment 列表均为空。本次只查询名称/配置，没有读取秘密值。现有本机代码签名身份仅属于 Apple/iPhone 开发或 Developer ID，不能替代 Windows Authenticode 可信签名；没有扫描其它私人目录或声称用户在别处一定没有证书。

匿名下载的 `Crosio-Windows-0.1.20.0-Setup.exe` 为 267,204,896 字节，SHA-256 为 `bf9807b1dfc01de5bfd41a9f26259f1a416e873ee4f41ce4791ad0b48731fda0`。只读解析其 PE Security Directory，Certificate Table 的文件偏移和大小均为 `0`，确认此公开 EXE 没有嵌入 Authenticode 签名。这不是 Windows 信任链验收，也不能用于证明其它版本的签名状态。

## 各层签名缺口

审计代码基线是 Windows 分支提交 [`7fc56918c184189b9c44f062552a1071820422e3`](https://github.com/CrossLee/onepaw/tree/7fc56918c184189b9c44f062552a1071820422e3/windows)。

| 层级 | 当前实现 | 可信发行还需要 |
| --- | --- | --- |
| 自有应用 PE | `Crosio.exe`、自有托管程序集和原生 Shell 扩展没有 Authenticode 签名步骤。第三方文件可能自带上游签名，不能把它们一概称为未签名。 | 明确自有 PE 清单，在装包前签名并验证；保持上游签名，不冒用本项目身份重签第三方二进制。 |
| x64/ARM64 MSIX、MSIXBundle | `build-msixbundle.ps1` 预留 PFX 参数，只调用 MSIX 和 bundle 签名；当前没有相应 secret。`signed` 元数据由参数是否存在推导，不是完整信任证据。 | 接入选定服务；验证真实签名、发布者、代码签名用途、可信链和时间戳，再生成发行元数据。 |
| 中文 Setup.exe | `build-setup.ps1` 在 NSIS 编译后直接计算哈希，没有签名步骤。即使嵌入的是签名 bundle，外层 EXE 也不会自动有签名。 | 嵌入最终签名 payload 后再签外层 EXE，然后计算最终哈希并验签。审计安装引擎等自有脚本是否也需单独签署。 |
| 自动公开 Release | `windows-ci.yml` 的 `publish-preview` 明确下载 `unsigned-test`，并要求 `.signed == false`。 | 独立且失败即停止的可信发行路径；不得在签名失败时静默降级到未签名。保留精确文件清单、同一构建、安装验收、上传后回下载校验。 |

现有配置名称为 `WINDOWS_CERTIFICATE_BASE64`、`WINDOWS_CERTIFICATE_PASSWORD`；它们目前均未配置。现有签名函数用 SignTool 的 PFX `/f` 方式，已有 SHA-256 和时间戳参数，但没有云 HSM/硬件密钥提供程序接线。公开可信代码签名的现代密钥保护要求不能用“导出私钥成 PFX 放进仓库”解决。[DigiCert 私钥保护要求](https://www.digicert.com/order/code-signing-certificates/hsm-approval.php)

代码证据：[流水线](https://github.com/CrossLee/onepaw/blob/7fc56918c184189b9c44f062552a1071820422e3/.github/workflows/windows-ci.yml)、[包签名](https://github.com/CrossLee/onepaw/blob/7fc56918c184189b9c44f062552a1071820422e3/windows/scripts/build-msixbundle.ps1)、[签名函数](https://github.com/CrossLee/onepaw/blob/7fc56918c184189b9c44f062552a1071820422e3/windows/scripts/Packaging.Common.ps1)、[安装器构建](https://github.com/CrossLee/onepaw/blob/7fc56918c184189b9c44f062552a1071820422e3/windows/scripts/build-setup.ps1)。

## 中国大陆个人或公司的可选路径

### 继续从 GitHub 下载 Setup.exe

适合先评估受 Windows 信任的 CA 所提供的代码签名证书及云 HSM/硬件令牌方案。微软列出的传统 CA 示例包括 DigiCert、Sectigo、GlobalSign。具体是否接受中国大陆个人、独资经营者或公司，必须由选定 CA 核验，不能把“全球提供”解释成所有个人都符合条件。以 DigiCert 当前流程为例，需要有效的组织代码签名验证、已验证的授权联系人及合规硬件密钥保护。[微软签名路径比较](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/code-signing-options)、[DigiCert 申请条件](https://docs.digicert.com/en/certcentral/order-and-manage-certificates/request-certificates/request-a-code-signing-or-ev-code-signing-certificate/request-code-signing-certificate.html)

本项目倾向在主体获准后使用支持 CI 的托管签名/HSM，避免把私钥复制到构建节点。USB 令牌通常需要受控签名机器，不能假定 GitHub 托管 runner 能直接使用。具体产品、费用及申请由用户确认，不在此次审计中购买或注册。

### Azure Artifact Signing（原 Trusted Signing）

当前公共信任资格**不含中国大陆主体**。微软快速入门列出的组织地区为：美国、加拿大、欧盟、英国、澳大利亚、新西兰、日本、韩国、新加坡、瑞士、挪威及以色列；个人仍限美国、加拿大。身份类型还必须匹配 Azure 账单账户类型。仅把资源部署到海外 Azure 区域，不会改变申请主体的资格。[微软当前资格清单](https://learn.microsoft.com/en-us/azure/artifact-signing/quickstart#prerequisites)

部分微软概览页仍列较短的组织地区清单；地区判断以服务快速入门的当前前置条件为准，并在实际申请时再次核验。Private Trust 不受同样地域限制，但不是面向普通用户的公共可信签名替代方案。服务还要求付费 Azure 订阅，免费、试用及赞助订阅不支持。证书 CN/O 不能随意填品牌名，应来自验证后的合法身份。[Artifact Signing FAQ](https://learn.microsoft.com/en-us/azure/artifact-signing/faq)

### Microsoft Store 的 MSIX 路线

微软可在 MSIX 通过商店认证后重新签名，不需要开发者自行购买该分发路径的代码签名证书；但需要开发者账户、真实个人/组织身份及应用审核。这是另一种分发路线，**不会替现有 GitHub Setup.exe 签名**。如果向商店提交 MSI/EXE，仍由开发者签署安装器及相关 PE。当前新开户流程说明个人和公司可免费开户，具体可用市场与身份要求以实际注册流程为准，不沿用旧费用表。[微软签名说明](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/code-signing-options)、[开发者开户流程](https://learn.microsoft.com/en-us/windows/apps/publish/partner-center/open-a-developer-account)

### 开源项目的 SignPath Foundation

微软也列出该免费候选途径，但不等于本项目已经获得资格。其条款要求 OSI 认可许可证、活跃维护、公开发布、可验证构建和签名审批等；证书发布主体是 SignPath Foundation，并由基金会决定接纳。审计时 Windows 分支没有项目自身的 LICENSE，只有第三方 NSIS 许可证；公开 GitHub 仓库本身不等于已授予开源许可。不能为了申请而未经所有者确认自动加许可证。[基金会条件](https://signpath.org/terms)

无论选 OV、EV 还是 Artifact Signing，可信签名都不等于每次首次下载必定没有 SmartScreen 提示。微软当前说明 EV 也不再提供立即绕过信誉检查的保证；应持续使用稳定发布者，并报告真实验证结果。[微软 SmartScreen 现状](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/code-signing-options#ev-certificates--no-longer-recommended-for-smartscreen)

## 未签名预览版转签名版：不能当普通升级

当前包为 `Name=Crosio.Windows`，Publisher 含微软未签名 OID，已发布预览版的 Package Family Name 为 `Crosio.Windows_a9eatqxyd72b4`。改成可信证书 Subject 后，包身份会变化。微软明确未签名包不会与签名包具有相同身份；普通包更新要求同一 package family。增加版本号或只保留 Name 都不足以维持原身份。[未签名包规则](https://learn.microsoft.com/en-us/windows/msix/package/unsigned-package)、[应用更新约束](https://learn.microsoft.com/en-us/windows/msix/app-package-updates)

Windows 11 的 publisher bridging/persistent identity 需要用旧签名证书签署桥接 catalog。当前预览版没有旧签名证书，因此不能把这个机制直接当作 unsigned → signed 的无缝升级办法。[Persistent Identity 要求](https://learn.microsoft.com/en-us/windows/msix/package/persistent-identity)

现有 `Install-Payload.ps1` 会枚举同名已安装包，并在 Publisher 不同时主动停止，提示保护现有数据。因此只加可信签名后，旧预览用户会被此保护规则阻止；不能删除判断后声称迁移已经解决。[安装器身份检查](https://github.com/CrossLee/onepaw/blob/7fc56918c184189b9c44f062552a1071820422e3/windows/installer/Install-Payload.ps1#L91)

数据也不能仅凭源代码中的 `%LOCALAPPDATA%\Crosio` 路径判断为一定保留：当前清单没有关闭 AppData 写入虚拟化；MSIX 对 AppData 的新建文件可能使用按用户、按包隔离的位置，而已有普通文件可能继续使用未虚拟化路径，卸载还可能移除虚拟化数据。应在 Windows 实机/受控 VM 观察实际路径，区分 packaged 和 unpackaged 安装，不能先卸载再寻找数据。[微软文件虚拟化细节](https://learn.microsoft.com/en-us/windows/msix/desktop/flexible-virtualization)

本项目需要纳入迁移盘点的内容包括：设置/快捷键、翻译历史及离线模型、Inbox、截图、录屏与 Drafts；用户选定的外部输出目录只读取既有配置，不移动或删除。新旧 package family 的开机启动、AUMID、协议/图片关联、Shell 扩展也要重新验证。

建议的迁移边界（尚未实现）：

1. 在当前登录账户内，只识别已知旧预览 Publisher/Package Family，拒绝把任意同名异发布者当作可迁移来源。
2. 关闭应用写入，在卸载/替换前导出实际数据；备份写入用户确认的独立位置，不放在将被卸载的包目录，记录清单和哈希。
3. 用户明确同意后执行受控迁移；选择并测试安装顺序，检查相同协议/COM 标识是否冲突，不预先承诺可安全并存。
4. 可信签名版启动后一次性导入、验证设置与文件；迁移失败时停止并保留备份，不覆盖更新的数据。
5. 验收通过后才讨论旧包清理，保留可恢复备份；不得自动清空整个 AppData、Packages 或 Crosio 目录。

## 实施前还缺什么

- 用户确认发布主体：个人还是公司，以及真实注册/居住地区；品牌显示名称与证书法定发布者不是同一概念。
- 是否已有可用 Windows 代码签名证书或云签名服务；先提供服务名称、证书公开 Subject/到期信息、托管方式即可，**不要把私钥、密码、令牌发到聊天或提交 Git**。
- 没有证书时，选择 CA/商店/符合条件的基金会路线；新账户、付费、合同、身份提交和许可选择需用户参与。
- 获准后的最小签名访问权限、负责审核发布的人，以及旧预览数据迁移的确认。

只有签名主体和服务确定后，才应选择具体 SDK/action/密钥提供程序，不能现在预设一份不可用的服务实现。

## 可信发行的实施与验证清单

- [ ] 建立受审批保护的签名环境，PR/不可信分支拿不到签名权限；使用短期身份认证或服务支持的 OIDC，限制到准确仓库、环境和签名权限。仓库现名为 `CrossLee/onepaw`，联合身份约束不能继续绑定旧仓库名。[GitHub Actions 与 Azure OIDC](https://learn.microsoft.com/en-us/azure/developer/github/connect-from-azure-openid-connect)
- [ ] 固定可信证书 Subject 和 RSA 代码签名配置；MSIX Publisher 与证书匹配，删除未签名 OID。Smart App Control 当前不支持 ECC 代码签名，应按目标兼容要求验证。[微软 Smart App Control 签名要求](https://learn.microsoft.com/en-us/windows/apps/develop/smart-app-control/code-signing-for-smart-app-control)
- [ ] 严格按“构建/测试 → 自有 PE 签名 → 装包及 MSIX/bundle 签名 → 构建 Setup → Setup 签名 → 最终哈希”处理，不在签署后修改内容；保留第三方原始签名和许可证。
- [ ] 在 Windows 使用 SignTool/Authenticode 验证每一层的真实签名、预期发布者、可信链、时间戳及撤销检查结果。不是只检查命令退出码、文件存在或 `.signed=true`。
- [ ] 在干净 x64/ARM64 Windows 11 环境测试首次、重复安装和启动，确认不用导入自签名根证书、不关闭安全功能、不走 `-AllowUnsigned`；记录安装器 UAC 的实际发布者。
- [ ] 用当前 0.1.20.0 预览包构造旧用户数据，验证迁移、失败恢复和不会丢失数据；另验签名版到下一签名版的普通升级。
- [ ] 复验开机启动、右键复制路径、图片打开方式、截图/录屏/OCR/翻译等关键路径，区分自动 smoke、托管环境和真实设备验收。
- [ ] 公开前核对同一 SHA 的精确文件清单、架构、版本、签名和 SHA-256；上传草稿后回下载，再验签和哈希，通过后才公开。
- [ ] 修改发行说明时明确写出已完成的签名层、真实发行者、适用平台和仍需人工验证的项目。未通过全部门禁时继续标记未签名预览，不把“签名准备完成”写成“可信版本已发布”。

本文未修改程序、版本或发布流水线，没有创建证书、购买服务、提交身份材料、触发 CI 或变更 Release。
