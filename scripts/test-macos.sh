#!/usr/bin/env bash
# Complete macOS regression with an independently verified result for each group.
set -euo pipefail
PROJECT_DIR="$(cd "$(dirname "$0")/.." && pwd -P)"
exec node "$PROJECT_DIR/scripts/test-macos.mjs" "$@"
