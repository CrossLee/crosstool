#!/bin/bash
# Shared by development and release packaging; no build or filesystem mutation.
onepaw_verify_bundle_brand() {
    local plist_path="$1" label="$2" key expected actual
    for key in CFBundleName CFBundleDisplayName CFBundleIdentifier CFBundleExecutable; do
        case "$key" in
            CFBundleName|CFBundleDisplayName) expected="一爪" ;;
            CFBundleIdentifier) expected="com.cross.crosstool" ;;
            CFBundleExecutable) expected="CrossToolApp" ;;
        esac
        actual="$(/usr/bin/plutil -extract "$key" raw -expect string -o - "$plist_path" 2>/dev/null)" || return 1
        if [[ "$actual" != "$expected" ]]; then
            echo "$label: $key must be $expected (got $actual)" >&2
            return 1
        fi
    done
}
