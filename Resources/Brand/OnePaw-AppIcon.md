# 一爪 OnePaw 产品图标

用户于 2026-09-09 确认「猫咪伸爪」拟物方向，并要求替换产品图标和打包发布。

- `OnePaw-AppIcon.png` 是已确认的内置生图原图，1254 × 1254，白底。没有代码绘制、重绘、裁切或抠图。
- `scripts/build-app-icon.sh` 仅使用系统 `sips` / `iconutil` 转为 macOS 必需的多尺寸 ICNS。
- `Resources/AppIcon.icns` 是实际 App 图标；现有开发及发布脚本把它放进主 Bundle，并对 ZIP、DMG、PKG 载荷逐一比较源文件。
- v0.6.5 首先更新产品图标；从 v0.6.6 起，应用显示名和实体统一为 `一爪.app`。兼容 Bundle ID、安装收据、菜单栏运行方式及用户数据目录保持不变。旧名称安装使用 PKG 安全迁移。
- 原 `CrosioIcon.png` 保留作既有图片压缩回归用例素材，不再代表当前产品图标。

品牌名称：一爪 OnePaw。口号：电脑小事，一爪搞定。
