#!/usr/bin/env bash

# Package the approved artwork into macOS icon representations. This performs
# only proportional size/format conversion: no drawing, cropping or retouching.
set -euo pipefail

PROJECT_DIR="$(cd "$(dirname "$0")/.." && pwd)"
ICON_ARTWORK="$PROJECT_DIR/Resources/Brand/OnePaw-AppIcon.png"
ICON_OUTPUT="$PROJECT_DIR/Resources/AppIcon.icns"

[[ -f "$ICON_ARTWORK" ]] || { echo "Missing approved icon: $ICON_ARTWORK" >&2; exit 1; }
ICON_WIDTH="$(sips -g pixelWidth "$ICON_ARTWORK" | awk '/pixelWidth:/ { print $2 }')"
ICON_HEIGHT="$(sips -g pixelHeight "$ICON_ARTWORK" | awk '/pixelHeight:/ { print $2 }')"
[[ "$ICON_WIDTH" == "$ICON_HEIGHT" && "$ICON_WIDTH" -ge 1024 ]] \
    || { echo "Icon artwork must be square and at least 1024 pixels." >&2; exit 1; }

ICON_STAGING="$(mktemp -d "${TMPDIR:-/tmp}/onepaw-icon.XXXXXX")"
trap 'rmdir "$ICON_STAGING/AppIcon.iconset" "$ICON_STAGING" 2>/dev/null || true' EXIT
mkdir "$ICON_STAGING/AppIcon.iconset"

for POINT_SIZE in 16 32 128 256 512; do
    sips -z "$POINT_SIZE" "$POINT_SIZE" "$ICON_ARTWORK" \
        --out "$ICON_STAGING/AppIcon.iconset/icon_${POINT_SIZE}x${POINT_SIZE}.png" >/dev/null
    RETINA_SIZE=$((POINT_SIZE * 2))
    sips -z "$RETINA_SIZE" "$RETINA_SIZE" "$ICON_ARTWORK" \
        --out "$ICON_STAGING/AppIcon.iconset/icon_${POINT_SIZE}x${POINT_SIZE}@2x.png" >/dev/null
done

iconutil --convert icns "$ICON_STAGING/AppIcon.iconset" --output "$ICON_OUTPUT"
# These exact generated files are disposable packaging intermediates.
for POINT_SIZE in 16 32 128 256 512; do
    rm "$ICON_STAGING/AppIcon.iconset/icon_${POINT_SIZE}x${POINT_SIZE}.png" \
        "$ICON_STAGING/AppIcon.iconset/icon_${POINT_SIZE}x${POINT_SIZE}@2x.png"
done
echo "Packaged approved OnePaw artwork: $ICON_OUTPUT"
