#!/usr/bin/env bash

set -euo pipefail

PROJECT_DIR="$(cd "$(dirname "$0")/.." && pwd)"
APP_BUNDLE_NAME="Crosio"
APP_BUNDLE="$PROJECT_DIR/dist/development/$APP_BUNDLE_NAME.app"
CONTENTS_DIR="$APP_BUNDLE/Contents"
MACOS_DIR="$CONTENTS_DIR/MacOS"
RESOURCES_DIR="$CONTENTS_DIR/Resources"
ICON_SOURCE="$PROJECT_DIR/Resources/AppIcon.icns"

if [[ ! -f "$ICON_SOURCE" ]]; then
    echo "找不到 App 图标：$ICON_SOURCE" >&2
    exit 1
fi

AVAILABLE_IDENTITIES="$(security find-identity -v -p codesigning 2>&1)"

if [[ -n "${CROSSTOOL_CODESIGN_IDENTITY:-}" ]]; then
    CODESIGN_IDENTITY="$CROSSTOOL_CODESIGN_IDENTITY"
else
    CODESIGN_IDENTITY="$(
        awk -F '"' '
            $2 ~ /^Apple Development:/ {
                identity_hash = $1
                sub(/^.*\) /, "", identity_hash)
                gsub(/^[[:space:]]+|[[:space:]]+$/, "", identity_hash)
                print identity_hash
                exit
            }
        ' <<< "$AVAILABLE_IDENTITIES"
    )"
    if [[ -z "$CODESIGN_IDENTITY" ]]; then
        CODESIGN_IDENTITY="-"
        echo "未找到 Apple Development 证书，将使用 ad-hoc 签名生成本地开发 App。" >&2
        echo "如需稳定签名，请设置 CROSSTOOL_CODESIGN_IDENTITY。" >&2
    fi
fi

swift build --package-path "$PROJECT_DIR" -c release
BIN_DIR="$(swift build --package-path "$PROJECT_DIR" -c release --show-bin-path)"
RESOURCE_BUNDLE="$(find "$BIN_DIR" -maxdepth 1 -type d -name '*CrossToolApp.bundle' -print -quit)"

if [[ -z "$RESOURCE_BUNDLE" || ! -d "$RESOURCE_BUNDLE" ]]; then
    echo "找不到 CrossToolApp 的 SwiftPM 资源包" >&2
    exit 1
fi

if [[ "$APP_BUNDLE" != "$PROJECT_DIR/dist/development/Crosio.app" ]]; then
    echo "拒绝清理意外的 App 路径：$APP_BUNDLE" >&2
    exit 1
fi

rm -rf "$APP_BUNDLE"
mkdir -p "$MACOS_DIR" "$RESOURCES_DIR"

ditto "$PROJECT_DIR/Resources/Info.plist" "$CONTENTS_DIR/Info.plist"
ditto "$RESOURCE_BUNDLE" "$RESOURCES_DIR/$(basename "$RESOURCE_BUNDLE")"
ditto "$ICON_SOURCE" "$RESOURCES_DIR/AppIcon.icns"
ditto "$BIN_DIR/CrossToolApp" "$MACOS_DIR/CrossToolApp"
chmod +x "$MACOS_DIR/CrossToolApp"

plutil -lint "$CONTENTS_DIR/Info.plist" >/dev/null
ICON_DECLARATION="$(plutil -extract CFBundleIconFile raw -o - "$CONTENTS_DIR/Info.plist" 2>/dev/null || true)"
if [[ "$ICON_DECLARATION" != "AppIcon.icns" ]]; then
    echo "Info.plist 必须声明 CFBundleIconFile=AppIcon.icns，当前值：${ICON_DECLARATION:-<未设置>}" >&2
    exit 1
fi
AGENT_APP="$(plutil -extract LSUIElement raw -expect bool -o - "$CONTENTS_DIR/Info.plist" 2>/dev/null || true)"
BACKGROUND_ONLY="$(plutil -extract LSBackgroundOnly raw -expect bool -o - "$CONTENTS_DIR/Info.plist" 2>/dev/null || true)"
if [[ "$AGENT_APP" != "true" || "$BACKGROUND_ONLY" == "true" ]]; then
    echo "Info.plist 必须以 LSUIElement=true、LSBackgroundOnly!=true 声明菜单栏 App" >&2
    exit 1
fi
if [[ ! -f "$RESOURCES_DIR/AppIcon.icns" ]] || ! cmp -s "$ICON_SOURCE" "$RESOURCES_DIR/AppIcon.icns"; then
    echo "App 图标未正确复制到主 Bundle" >&2
    exit 1
fi
codesign --force --deep --sign "$CODESIGN_IDENTITY" "$APP_BUNDLE"
codesign --verify --deep --strict "$APP_BUNDLE"
SIGNATURE_DETAILS="$(codesign --display --verbose=2 "$APP_BUNDLE" 2>&1)"
if [[ "$CODESIGN_IDENTITY" == "-" ]]; then
    grep -q '^Signature=adhoc$' <<< "$SIGNATURE_DETAILS" \
        || { echo "ad-hoc 签名验证失败" >&2; exit 1; }
else
    if grep -q '^Signature=adhoc$' <<< "$SIGNATURE_DETAILS"; then
        echo "签名验证失败：请求了开发证书但生成的 App 是 ad-hoc 签名" >&2
        exit 1
    fi
fi
grep -E '^(Identifier|Authority|TeamIdentifier|Signature)=' <<< "$SIGNATURE_DETAILS"

echo "$APP_BUNDLE"
