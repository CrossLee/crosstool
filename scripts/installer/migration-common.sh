#!/bin/bash
# Sourced by both Installer phases. Keep this compatible with macOS Bash 3.2.
# Bundle/receipt/data identifiers intentionally keep the historical spelling.

ONEPAW_BUNDLE_ID="com.cross.crosstool"
ONEPAW_TEAM_ID="8LSY655LKD"
ONEPAW_APP_NAMES=("一爪.app" "Crosio.app" "crosstool.app")
ONEPAW_LEGACY_NAMES=("Crosio.app" "crosstool.app")

onepaw_fail() {
    echo "一爪安装器：$*" >&2
    return 1
}

onepaw_require_root() {
    [[ "$(/usr/bin/id -u)" == 0 ]] || onepaw_fail "安装迁移必须由系统安装器以 root 执行。"
}

onepaw_assert_safe_acl() {
    local path="$1" listing line first_line=true principal disposition permissions permission root_uuid
    local permission_list=()
    # Numeric ls output uses ACL UUIDs, avoiding ambiguous user/group names.
    # -b escapes unusual filenames so the single metadata line stays parseable.
    listing="$(LC_ALL=C /bin/ls -ldneb "$path")" || onepaw_fail "无法读取访问控制列表：$path" || return 1
    root_uuid="$(/usr/bin/dsmemberutil getuuid -u 0)" || onepaw_fail "无法确认 root 的访问控制身份。" || return 1
    [[ "$root_uuid" =~ ^[A-Fa-f0-9-]{36}$ ]] || onepaw_fail "root 的访问控制身份无法验证。" || return 1
    root_uuid="$(/usr/bin/tr '[:lower:]' '[:upper:]' <<< "$root_uuid")"
    while IFS= read -r line; do
        if [[ "$first_line" == true ]]; then first_line=false; continue; fi
        [[ -n "$line" ]] || continue
        if [[ ! "$line" =~ ^[[:space:]]*[0-9]+:[[:space:]]+([A-Fa-f0-9-]{36})[[:space:]]+(inherited[[:space:]]+)?(allow|deny)[[:space:]]+([a-z_,]+)$ ]]; then
            onepaw_fail "无法安全解析访问控制列表，不继续安装：$path"
            return 1
        fi
        principal="${BASH_REMATCH[1]}"
        disposition="${BASH_REMATCH[3]}"
        permissions="${BASH_REMATCH[4]}"
        principal="$(/usr/bin/tr '[:lower:]' '[:upper:]' <<< "$principal")"
        IFS=, read -r -a permission_list <<< "$permissions"
        for permission in "${permission_list[@]}"; do
            case "$permission" in
                list|search|read|execute|readattr|readextattr|readsecurity|file_inherit|directory_inherit|limit_inherit|only_inherit)
                    ;;
                add_file|add_subdirectory|delete_child|write|append|delete|writeattr|writeextattr|writesecurity|chown)
                    # A deny ACL can only remove access (e.g. everyone deny delete).
                    # Inheritance-only grants are rejected too: they would expose
                    # subsequently created pending records and ZIP archives.
                    if [[ "$disposition" == allow && "$principal" != "$root_uuid" ]]; then
                        onepaw_fail "访问控制列表允许非 root 修改备份或其子项：$path ($permission)"
                        return 1
                    fi
                    ;;
                *) onepaw_fail "访问控制列表含未知权限，不继续安装：$path ($permission)"; return 1 ;;
            esac
        done
    done <<< "$listing"
}

onepaw_secure_directory() {
    local directory="$1" owner mode
    [[ -d "$directory" && ! -L "$directory" ]] || onepaw_fail "目录缺失或是符号链接：$directory" || return 1
    owner="$(/usr/bin/stat -f %u "$directory")" || return 1
    mode="$(/usr/bin/stat -f %Lp "$directory")" || return 1
    [[ "$owner" == 0 && "$mode" =~ ^[0-7]+$ ]] || onepaw_fail "备份目录不是 root 所有：$directory" || return 1
    (( (8#$mode & 0022) == 0 )) || onepaw_fail "备份目录可被其他用户写入：$directory" || return 1
    onepaw_assert_safe_acl "$directory"
}

onepaw_secure_file() {
    local file="$1" owner mode
    [[ -f "$file" && ! -L "$file" ]] || onepaw_fail "备份文件缺失或是符号链接：$file" || return 1
    owner="$(/usr/bin/stat -f %u "$file")" || return 1
    mode="$(/usr/bin/stat -f %Lp "$file")" || return 1
    [[ "$owner" == 0 && "$mode" =~ ^[0-7]+$ ]] || onepaw_fail "备份文件不是 root 所有：$file" || return 1
    (( (8#$mode & 0022) == 0 )) || onepaw_fail "备份文件可被其他用户写入：$file" || return 1
    onepaw_assert_safe_acl "$file"
}

onepaw_initialize() {
    local requested_root="$1" component
    onepaw_require_root || return 1
    [[ "$requested_root" == /* && "$requested_root" != *$'\n'* && "$requested_root" != *$'\r'* ]] \
        || onepaw_fail "安装目标卷路径不安全。" || return 1
    ONEPAW_TARGET_ROOT="$(cd "$requested_root" && pwd -P)" || return 1
    ONEPAW_VOLUME_PREFIX="${ONEPAW_TARGET_ROOT%/}"
    ONEPAW_APPLICATIONS="$ONEPAW_VOLUME_PREFIX/Applications"
    [[ -d "$ONEPAW_APPLICATIONS" && ! -L "$ONEPAW_APPLICATIONS" ]] \
        || onepaw_fail "Applications 目录缺失或是符号链接。" || return 1
    # This is a system-owned upgrade archive, NOT the user's Application Support.
    ONEPAW_BACKUPS="$ONEPAW_VOLUME_PREFIX/Library/Application Support/crosstool/UpgradeBackups.noindex"
    for component in "$ONEPAW_VOLUME_PREFIX/Library" "$ONEPAW_VOLUME_PREFIX/Library/Application Support" \
        "$ONEPAW_VOLUME_PREFIX/Library/Application Support/crosstool" "$ONEPAW_BACKUPS"; do
        if [[ ! -e "$component" && ! -L "$component" ]]; then
            /bin/mkdir -m 700 "$component" || return 1
        fi
        onepaw_secure_directory "$component" || return 1
    done
    ONEPAW_PENDING="$ONEPAW_BACKUPS/.pending"
}

onepaw_process_snapshot() {
    /bin/ps -axww -o command=
}

onepaw_assert_not_running() {
    local app="$1" executable="$1/Contents/MacOS/CrossToolApp" process snapshot
    snapshot="$(onepaw_process_snapshot)" || onepaw_fail "无法检查正在运行的应用。" || return 1
    while IFS= read -r process; do
        # Literal path comparison, not a regex (volume names may contain .[]+).
        if [[ "$process" == "$executable" || "$process" == "$executable "* || "$process" == "$executable"$'\t'* ]]; then
            onepaw_fail "请先退出应用，再重新安装：$app"
            return 1
        fi
    done <<< "$snapshot"
}

onepaw_verify_signature() {
    local app="$1" team
    # Require a valid Apple-rooted signature, the exact signing identifier and Team.
    /usr/bin/codesign --verify --deep --strict \
        -R="anchor apple generic and identifier \"$ONEPAW_BUNDLE_ID\" and certificate leaf[subject.OU] = \"$ONEPAW_TEAM_ID\"" \
        "$app" >/dev/null 2>&1 || onepaw_fail "应用签名或开发者身份验证失败：$app" || return 1
    team="$(/usr/bin/codesign --display --verbose=4 "$app" 2>&1 | /usr/bin/awk -F= '/^TeamIdentifier=/{print $2; exit}')" || return 1
    [[ "$team" == "$ONEPAW_TEAM_ID" ]] || onepaw_fail "应用来自其他开发者团队：$app"
}

onepaw_verify_app() {
    local app="$1" part identifier executable
    for part in "$app" "$app/Contents" "$app/Contents/Info.plist" "$app/Contents/MacOS" "$app/Contents/MacOS/CrossToolApp"; do
        [[ -e "$part" && ! -L "$part" ]] || onepaw_fail "拒绝处理缺失或符号链接的应用组件：$part" || return 1
    done
    [[ -d "$app" && -f "$app/Contents/Info.plist" && -f "$app/Contents/MacOS/CrossToolApp" ]] \
        || onepaw_fail "应用结构不完整：$app" || return 1
    identifier="$(/usr/bin/plutil -extract CFBundleIdentifier raw -o - "$app/Contents/Info.plist" 2>/dev/null)" || return 1
    executable="$(/usr/bin/plutil -extract CFBundleExecutable raw -o - "$app/Contents/Info.plist" 2>/dev/null)" || return 1
    [[ "$identifier" == "$ONEPAW_BUNDLE_ID" && "$executable" == CrossToolApp ]] \
        || onepaw_fail "发现同名但身份不同的应用，不会覆盖或移动：$app" || return 1
    onepaw_verify_signature "$app"
}

onepaw_archive_app() {
    /usr/bin/ditto -c -k --sequesterRsrc --keepParent "$1" "$2"
}

onepaw_numeric_compare() {
    local left="$1" right="$2"
    # Avoid shell integer overflow and octal interpretation of leading zeroes.
    while [[ "${#left}" -gt 1 && "$left" == 0* ]]; do left="${left#0}"; done
    while [[ "${#right}" -gt 1 && "$right" == 0* ]]; do right="${right#0}"; done
    if [[ "${#left}" -gt "${#right}" ]]; then echo 1
    elif [[ "${#left}" -lt "${#right}" ]]; then echo -1
    elif [[ "$left" == "$right" ]]; then echo 0
    elif [[ "$left" > "$right" ]]; then echo 1
    else echo -1
    fi
}

onepaw_version_tuple() {
    local plist="$1" version build major minor patch
    version="$(/usr/bin/plutil -extract CFBundleShortVersionString raw -o - "$plist")" || return 1
    build="$(/usr/bin/plutil -extract CFBundleVersion raw -o - "$plist")" || return 1
    [[ "$version" =~ ^[0-9]+([.][0-9]+){1,2}$ && "$build" =~ ^[0-9]+$ ]] \
        || onepaw_fail "应用版本不是可安全比较的数字版本：$plist" || return 1
    IFS=. read -r major minor patch <<< "$version"
    echo "$major $minor ${patch:-0} $build"
}

onepaw_assert_not_downgrade() {
    local app="$1" expected_plist="$2" actual_tuple expected_tuple comparison index
    local actual_parts=() expected_parts=()
    actual_tuple="$(onepaw_version_tuple "$app/Contents/Info.plist")" || return 1
    expected_tuple="$(onepaw_version_tuple "$expected_plist")" || return 1
    read -r -a actual_parts <<< "$actual_tuple"
    read -r -a expected_parts <<< "$expected_tuple"
    for index in 0 1 2 3; do
        comparison="$(onepaw_numeric_compare "${actual_parts[$index]}" "${expected_parts[$index]}")" || return 1
        if [[ "$comparison" == 1 ]]; then
            onepaw_fail "已安装版本比此安装包更新，不会降级覆盖：$app"
            return 1
        elif [[ "$comparison" == -1 ]]; then
            return 0
        fi
    done
    return 0
}

onepaw_compare_archive() {
    local app="$1" archive="$2" verify_dir archived_app result=0
    [[ -f "$archive" && ! -L "$archive" ]] || onepaw_fail "缺少可恢复备份：$archive" || return 1
    onepaw_secure_file "$archive" || return 1
    verify_dir="$(/usr/bin/mktemp -d "$ONEPAW_TRANSACTION/.verify.XXXXXX")" || return 1
    archived_app="$verify_dir/$(basename "$app")"
    /usr/bin/ditto -x -k "$archive" "$verify_dir" || result=1
    if [[ "$result" == 0 ]]; then
        onepaw_verify_app "$archived_app" || result=1
        /usr/bin/diff -qr "$app" "$archived_app" >/dev/null || result=1
    fi
    # Only this invocation's mktemp directory is removed, never an installed app.
    if [[ "$verify_dir" == "$ONEPAW_TRANSACTION"/.verify.* && -d "$verify_dir" && ! -L "$verify_dir" ]]; then
        /bin/rm -rf -- "$verify_dir"
    fi
    [[ "$result" == 0 ]] || onepaw_fail "应用与已验证备份不一致；保留原应用，不继续迁移：$app"
}

onepaw_preinstall() {
    local expected_plist="$1" name app
    [[ -f "$expected_plist" && ! -L "$expected_plist" ]] || onepaw_fail "安装包缺少预期版本信息。" || return 1
    onepaw_version_tuple "$expected_plist" >/dev/null || return 1
    # Resolve ALL targets first. Even an unrelated 一爪.app must block overwrite.
    for name in "${ONEPAW_APP_NAMES[@]}"; do
        app="$ONEPAW_APPLICATIONS/$name"
        onepaw_assert_not_running "$app" || return 1
        if [[ -e "$app" || -L "$app" ]]; then
            onepaw_verify_app "$app" || return 1
            onepaw_assert_not_downgrade "$app" "$expected_plist" || return 1
        fi
    done
    # Atomic lock: an interrupted migration requires explicit recovery, never reuse.
    /bin/mkdir -m 700 "$ONEPAW_PENDING" 2>/dev/null \
        || onepaw_fail "存在未完成的安装记录，请先检查备份目录：$ONEPAW_BACKUPS" || return 1
    ONEPAW_TRANSACTION="$(/usr/bin/mktemp -d "$ONEPAW_BACKUPS/upgrade-$(/bin/date -u +%Y%m%dT%H%M%S).XXXXXX")" || return 1
    /usr/bin/printf '%s\n' "${ONEPAW_TRANSACTION##*/}" > "$ONEPAW_PENDING/transaction"
    /usr/bin/printf '%s\n' \
        '一爪更名升级备份。用户设置与数据目录未修改。' \
        'ZIP 是安装前的完整应用备份；.app.saved 是移出 Applications 的原应用。' \
        '恢复前先退出所有版本，保留当前应用。将所需 ZIP 解压到临时位置并核对签名，再手动移回 /Applications。' \
        '不要将多个历史版本同时放回 /Applications；不要直接删除未完成安装的 .pending 记录。' \
        '若安装中断：保留此目录及 .pending，检查安装日志并确认所有应用已退出。' \
        '确认新应用验签、版本与包一致且旧应用/ZIP完好后，可重新执行同一安装包的 postinstall 完成迁移。' \
        '若尚未写入新应用，应先恢复 ZIP 并请管理员检查后再解除 .pending；安装器不会自动丢弃恢复记录。' \
        > "$ONEPAW_TRANSACTION/恢复说明.txt"
    for name in "${ONEPAW_APP_NAMES[@]}"; do
        app="$ONEPAW_APPLICATIONS/$name"
        if [[ -e "$app" || -L "$app" ]]; then
            onepaw_assert_not_running "$app" || return 1
            onepaw_verify_app "$app" || return 1
            onepaw_assert_not_downgrade "$app" "$expected_plist" || return 1
            onepaw_archive_app "$app" "$ONEPAW_TRANSACTION/$name.zip" || return 1
            onepaw_compare_archive "$app" "$ONEPAW_TRANSACTION/$name.zip" || return 1
        fi
    done
    /usr/bin/touch "$ONEPAW_PENDING/preflight-ok"
    echo "一爪安装前检查完成。可恢复备份：$ONEPAW_TRANSACTION"
}

onepaw_read_transaction() {
    local name
    onepaw_secure_directory "$ONEPAW_PENDING" || return 1
    [[ -f "$ONEPAW_PENDING/transaction" && ! -L "$ONEPAW_PENDING/transaction" \
        && -f "$ONEPAW_PENDING/preflight-ok" && ! -L "$ONEPAW_PENDING/preflight-ok" ]] \
        || onepaw_fail "缺少已完成的安装前检查记录。" || return 1
    onepaw_secure_file "$ONEPAW_PENDING/transaction" || return 1
    onepaw_secure_file "$ONEPAW_PENDING/preflight-ok" || return 1
    name="$(/bin/cat "$ONEPAW_PENDING/transaction")" || return 1
    [[ "$name" =~ ^upgrade-[A-Za-z0-9.-]+$ ]] || onepaw_fail "安装事务路径不安全。" || return 1
    ONEPAW_TRANSACTION="$ONEPAW_BACKUPS/$name"
    onepaw_secure_directory "$ONEPAW_TRANSACTION"
}

onepaw_postinstall() {
    local expected_plist="$1" new_app="$ONEPAW_APPLICATIONS/一爪.app" name app key expected actual
    onepaw_read_transaction || return 1
    onepaw_verify_app "$new_app" || return 1
    [[ -f "$expected_plist" && ! -L "$expected_plist" ]] || onepaw_fail "安装包缺少预期版本信息。" || return 1
    for key in CFBundleShortVersionString CFBundleVersion CFBundleName CFBundleDisplayName; do
        expected="$(/usr/bin/plutil -extract "$key" raw -o - "$expected_plist")" || return 1
        actual="$(/usr/bin/plutil -extract "$key" raw -o - "$new_app/Contents/Info.plist")" || return 1
        [[ -n "$expected" && "$actual" == "$expected" ]] || onepaw_fail "新应用与安装包版本或名称不符：$key" || return 1
    done
    # Revalidate every old target before moving any. No deletion, including failures.
    for name in "${ONEPAW_LEGACY_NAMES[@]}"; do
        app="$ONEPAW_APPLICATIONS/$name"
        if [[ -e "$app" || -L "$app" ]]; then
            onepaw_assert_not_running "$app" || return 1
            onepaw_verify_app "$app" || return 1
            onepaw_compare_archive "$app" "$ONEPAW_TRANSACTION/$name.zip" || return 1
            [[ ! -e "$ONEPAW_TRANSACTION/$name.saved" && ! -L "$ONEPAW_TRANSACTION/$name.saved" ]] \
                || onepaw_fail "旧版备份目标已存在，不会覆盖：$name.saved" || return 1
        fi
    done
    for name in "${ONEPAW_LEGACY_NAMES[@]}"; do
        app="$ONEPAW_APPLICATIONS/$name"
        if [[ -e "$app" || -L "$app" ]]; then
            onepaw_assert_not_running "$app" || return 1
            # Keep both a verified ZIP and the original bundle outside Spotlight.
            /bin/mv -n "$app" "$ONEPAW_TRANSACTION/$name.saved" || return 1
            [[ ! -e "$app" && ! -L "$app" ]] || onepaw_fail "旧版移动未完成：$app" || return 1
            onepaw_verify_app "$ONEPAW_TRANSACTION/$name.saved" || return 1
            echo "已保留旧版备份：$ONEPAW_TRANSACTION/$name.saved"
        fi
    done
    /usr/bin/touch "$ONEPAW_TRANSACTION/completed"
    /bin/rm "$ONEPAW_PENDING/transaction" "$ONEPAW_PENDING/preflight-ok"
    /bin/rmdir "$ONEPAW_PENDING"
    echo "一爪安装完成。旧版备份保留在：$ONEPAW_TRANSACTION"
}
