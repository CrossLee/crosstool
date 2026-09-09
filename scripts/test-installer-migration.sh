#!/bin/bash
# Isolated migration logic regression: never installs, launches or touches real apps.
# Signing and root ownership are mocked ONLY inside generated fixture volumes.
set -euo pipefail
export PATH=/usr/bin:/bin:/usr/sbin:/sbin
umask 077
PROJECT_DIR="$(cd "$(dirname "$0")/.." && pwd -P)"
COMMON="$PROJECT_DIR/scripts/installer/migration-common.sh"

fail() { echo "FAIL: $*" >&2; exit 1; }
expect_failure() { if "$@"; then fail "unexpected success: $*"; fi; }

if [[ "${1:-}" != --case ]]; then
    /bin/mkdir -p "$PROJECT_DIR/.build"
    LOG_DIR="$(/usr/bin/mktemp -d "$PROJECT_DIR/.build/onepaw-migration-tests.XXXXXX")"
    cases=(fresh legacy_crosio legacy_crosstool both_legacy new_name_upgrade wrong_identity_new wrong_identity_old \
        wrong_signature symlink_app dangling_symlink symlink_component running_old running_new literal_process_paths \
        invalid_new missing_expected wrong_version changed_legacy appeared_legacy backup_conflict pending_conflict \
        archive_failure unsafe_transaction backup_symlink protected_directories real_unsigned_signature brand_metadata \
        newer_version newer_build invalid_version leading_zero_version huge_version package_layout \
        acl_none acl_deny_delete acl_read_only acl_root_write acl_user_write acl_group_write \
        acl_inherit_only acl_inherited_child acl_metadata_writes acl_file_writes acl_deny_then_allow)
    for name in "${cases[@]}"; do
        if /bin/bash "$0" --case "$name" > "$LOG_DIR/$name.log" 2>&1; then
            echo "PASS $name"
        else
            /bin/cat "$LOG_DIR/$name.log"
            fail "$name (logs: $LOG_DIR)"
        fi
    done
    echo "PASS ${#cases[@]} isolated migration cases. Logs: $LOG_DIR"
    echo "Scope: fixture filesystem/plist/archive/state tests; not real Installer or Gatekeeper acceptance."
    exit 0
fi

CASE_NAME="${2:?missing case}"
FIXTURE="$(/usr/bin/mktemp -d "$PROJECT_DIR/.build/onepaw-migration-fixture.XXXXXX")"
cleanup() {
    if [[ "$FIXTURE" == "$PROJECT_DIR/.build/onepaw-migration-fixture."* && -d "$FIXTURE" && ! -L "$FIXTURE" ]]; then
        # Real ACL fixtures include deny-delete entries. Clear only our own tree.
        /usr/bin/find "$FIXTURE" \( -type d -o -type f \) -exec /bin/chmod -N {} +
        /bin/rm -rf -- "$FIXTURE"
    fi
}
trap cleanup EXIT
source "$COMMON"

if [[ "$CASE_NAME" == acl_* ]]; then
    # Real macOS chmod ACLs + the unmocked production ACL reader. These do not
    # require root ownership, so no privilege escalation or host ACL edits occur.
    ACL_PATH="$FIXTURE/acl-object"
    /bin/mkdir "$ACL_PATH"
    case "$CASE_NAME" in
        acl_none) onepaw_assert_safe_acl "$ACL_PATH" ;;
        acl_deny_delete)
            /bin/chmod +a 'everyone deny delete' "$ACL_PATH"
            onepaw_assert_safe_acl "$ACL_PATH" ;;
        acl_read_only)
            /bin/chmod +a 'group:staff allow list,search,readattr,readextattr,readsecurity,file_inherit,directory_inherit' "$ACL_PATH"
            onepaw_assert_safe_acl "$ACL_PATH" ;;
        acl_root_write)
            /bin/chmod +a 'user:root allow add_file,add_subdirectory,delete_child,delete,writeattr,writeextattr,writesecurity,chown,file_inherit,directory_inherit,only_inherit' "$ACL_PATH"
            onepaw_assert_safe_acl "$ACL_PATH" ;;
        acl_user_write)
            /bin/chmod +a "user:$(/usr/bin/id -un) allow add_file" "$ACL_PATH"
            expect_failure onepaw_assert_safe_acl "$ACL_PATH" ;;
        acl_group_write)
            /bin/chmod +a 'group:staff allow add_subdirectory' "$ACL_PATH"
            expect_failure onepaw_assert_safe_acl "$ACL_PATH" ;;
        acl_inherit_only)
            /bin/chmod +a 'group:staff allow write,file_inherit,directory_inherit,only_inherit,limit_inherit' "$ACL_PATH"
            expect_failure onepaw_assert_safe_acl "$ACL_PATH" ;;
        acl_inherited_child)
            /bin/chmod +a 'group:staff allow write,file_inherit,directory_inherit' "$ACL_PATH"
            /bin/mkdir "$ACL_PATH/child"
            /usr/bin/touch "$ACL_PATH/record"
            /bin/ls -ldneb "$ACL_PATH/child" "$ACL_PATH/record"
            expect_failure onepaw_assert_safe_acl "$ACL_PATH/child"
            expect_failure onepaw_assert_safe_acl "$ACL_PATH/record" ;;
        acl_metadata_writes)
            for permission in writeattr writeextattr writesecurity chown delete delete_child; do
                /bin/chmod -N "$ACL_PATH"
                /bin/chmod +a "group:staff allow $permission" "$ACL_PATH"
                /bin/ls -ldneb "$ACL_PATH"
                expect_failure onepaw_assert_safe_acl "$ACL_PATH"
            done ;;
        acl_file_writes)
            /usr/bin/touch "$ACL_PATH/record"
            for permission in write append delete writeattr writeextattr writesecurity chown; do
                /bin/chmod -N "$ACL_PATH/record"
                /bin/chmod +a "group:staff allow $permission" "$ACL_PATH/record"
                /bin/ls -ldneb "$ACL_PATH/record"
                expect_failure onepaw_assert_safe_acl "$ACL_PATH/record"
            done ;;
        acl_deny_then_allow)
            /bin/chmod +a 'everyone deny add_file' "$ACL_PATH"
            /bin/chmod +a 'group:staff allow add_file,file_inherit,directory_inherit' "$ACL_PATH"
            expect_failure onepaw_assert_safe_acl "$ACL_PATH" ;;
        *) fail "unknown ACL case: $CASE_NAME" ;;
    esac
    /bin/ls -ldneb "$ACL_PATH"
    exit 0
fi

if [[ "$CASE_NAME" == protected_directories ]]; then
    onepaw_secure_directory /Library
    onepaw_secure_file /private/etc/hosts
    expect_failure onepaw_secure_directory "$FIXTURE"
    exit 0
fi

# Host-boundary overrides are deliberately absent from production installer code.
onepaw_require_root() { :; }
onepaw_secure_directory() { [[ -d "$1" && ! -L "$1" ]]; }
onepaw_secure_file() { [[ -f "$1" && ! -L "$1" ]]; }
onepaw_verify_signature() { [[ "$(/bin/cat "$1/Contents/test-signature" 2>/dev/null)" == trusted ]]; }
TEST_PROCESSES=""
onepaw_process_snapshot() { /usr/bin/printf '%s\n' "$TEST_PROCESSES"; }

/bin/mkdir -p "$FIXTURE/Applications" "$FIXTURE/Library/Application Support" "$FIXTURE/UserData"
/usr/bin/printf '%s\n' 'settings must stay unchanged' > "$FIXTURE/UserData/settings"
onepaw_initialize "$FIXTURE"
/usr/bin/plutil -create xml1 "$FIXTURE/expected-before.plist"
/usr/bin/plutil -insert CFBundleShortVersionString -string 0.6.6 "$FIXTURE/expected-before.plist"
/usr/bin/plutil -insert CFBundleVersion -string 47 "$FIXTURE/expected-before.plist"
run_preinstall() { onepaw_preinstall "$FIXTURE/expected-before.plist"; }

make_app() {
    local app="$ONEPAW_APPLICATIONS/$1" plist
    /bin/mkdir -p "$app/Contents/MacOS"
    plist="$app/Contents/Info.plist"
    /usr/bin/plutil -create xml1 "$plist"
    /usr/bin/plutil -insert CFBundleIdentifier -string com.cross.crosstool "$plist"
    /usr/bin/plutil -insert CFBundleExecutable -string CrossToolApp "$plist"
    /usr/bin/plutil -insert CFBundlePackageType -string APPL "$plist"
    /usr/bin/plutil -insert CFBundleName -string 一爪 "$plist"
    /usr/bin/plutil -insert CFBundleDisplayName -string 一爪 "$plist"
    /usr/bin/plutil -insert CFBundleShortVersionString -string 0.6.6 "$plist"
    /usr/bin/plutil -insert CFBundleVersion -string 47 "$plist"
    /usr/bin/printf '%s\n' 'fixture, never executed' > "$app/Contents/MacOS/CrossToolApp"
    /usr/bin/printf '%s\n' trusted > "$app/Contents/test-signature"
}
install_new_fixture() {
    make_app 一爪.app
    /bin/cp "$ONEPAW_APPLICATIONS/一爪.app/Contents/Info.plist" "$FIXTURE/expected.plist"
}
assert_preserved() {
    [[ -d "$ONEPAW_APPLICATIONS/$1" && ! -L "$ONEPAW_APPLICATIONS/$1" ]] || fail "original not preserved: $1"
}
assert_migrated() {
    [[ ! -e "$ONEPAW_APPLICATIONS/$1" && -d "$ONEPAW_TRANSACTION/$1.saved" && -f "$ONEPAW_TRANSACTION/$1.zip" ]] \
        || fail "missing recoverable migration: $1"
}
finish_install() {
    install_new_fixture
    onepaw_postinstall "$FIXTURE/expected.plist"
    [[ -f "$ONEPAW_TRANSACTION/completed" && ! -e "$ONEPAW_PENDING" ]] || fail 'missing successful transaction state'
    [[ "$(/bin/cat "$FIXTURE/UserData/settings")" == 'settings must stay unchanged' ]] || fail 'user data changed'
}

case "$CASE_NAME" in
    fresh)
        run_preinstall; finish_install ;;
    legacy_crosio|legacy_crosstool|both_legacy)
        if [[ "$CASE_NAME" != legacy_crosstool ]]; then make_app Crosio.app; fi
        if [[ "$CASE_NAME" != legacy_crosio ]]; then make_app crosstool.app; fi
        run_preinstall; finish_install
        if [[ "$CASE_NAME" != legacy_crosstool ]]; then assert_migrated Crosio.app; fi
        if [[ "$CASE_NAME" != legacy_crosio ]]; then assert_migrated crosstool.app; fi ;;
    new_name_upgrade)
        make_app 一爪.app
        /usr/bin/plutil -replace CFBundleVersion -string 46 "$ONEPAW_APPLICATIONS/一爪.app/Contents/Info.plist"
        run_preinstall; finish_install
        [[ -f "$ONEPAW_TRANSACTION/一爪.app.zip" ]] || fail 'old new-name version was not archived' ;;
    wrong_identity_new|wrong_identity_old)
        name=一爪.app; if [[ "$CASE_NAME" == wrong_identity_old ]]; then name=Crosio.app; fi
        make_app "$name"
        /usr/bin/plutil -replace CFBundleIdentifier -string org.unrelated.app "$ONEPAW_APPLICATIONS/$name/Contents/Info.plist"
        expect_failure run_preinstall; assert_preserved "$name"
        [[ ! -e "$ONEPAW_PENDING" ]] || fail 'conflict created a pending transaction' ;;
    wrong_signature)
        make_app Crosio.app
        /usr/bin/printf '%s\n' other-team > "$ONEPAW_APPLICATIONS/Crosio.app/Contents/test-signature"
        expect_failure run_preinstall; assert_preserved Crosio.app ;;
    symlink_app|dangling_symlink)
        target="$FIXTURE/not-there"
        if [[ "$CASE_NAME" == symlink_app ]]; then /bin/mkdir "$target"; fi
        /bin/ln -s "$target" "$ONEPAW_APPLICATIONS/Crosio.app"
        expect_failure run_preinstall
        [[ -L "$ONEPAW_APPLICATIONS/Crosio.app" ]] || fail 'symlink was touched' ;;
    symlink_component)
        make_app Crosio.app
        /bin/mv "$ONEPAW_APPLICATIONS/Crosio.app/Contents/Info.plist" "$FIXTURE/original.plist"
        /bin/ln -s "$FIXTURE/original.plist" "$ONEPAW_APPLICATIONS/Crosio.app/Contents/Info.plist"
        expect_failure run_preinstall; assert_preserved Crosio.app ;;
    running_old|running_new)
        name=Crosio.app; if [[ "$CASE_NAME" == running_new ]]; then name=一爪.app; fi
        make_app "$name"
        TEST_PROCESSES="$ONEPAW_APPLICATIONS/$name/Contents/MacOS/CrossToolApp --argument"
        expect_failure run_preinstall; assert_preserved "$name" ;;
    literal_process_paths)
        unusual="$FIXTURE/disk.[name]+/Applications/Crosio.app"
        TEST_PROCESSES="$unusual/Contents/MacOS/CrossToolApp-helper"
        onepaw_assert_not_running "$unusual"
        TEST_PROCESSES="$unusual/Contents/MacOS/CrossToolApp --argument"
        expect_failure onepaw_assert_not_running "$unusual" ;;
    invalid_new|missing_expected|wrong_version)
        make_app Crosio.app; run_preinstall; install_new_fixture
        if [[ "$CASE_NAME" == invalid_new ]]; then
            /usr/bin/printf '%s\n' invalid > "$ONEPAW_APPLICATIONS/一爪.app/Contents/test-signature"
        elif [[ "$CASE_NAME" == missing_expected ]]; then
            /bin/mv "$FIXTURE/expected.plist" "$FIXTURE/unrelated.plist"
        else
            /usr/bin/plutil -replace CFBundleVersion -string 46 "$ONEPAW_APPLICATIONS/一爪.app/Contents/Info.plist"
        fi
        expect_failure onepaw_postinstall "$FIXTURE/expected.plist"; assert_preserved Crosio.app ;;
    changed_legacy)
        make_app Crosio.app; make_app crosstool.app; run_preinstall; install_new_fixture
        /usr/bin/printf '%s\n' changed > "$ONEPAW_APPLICATIONS/crosstool.app/Contents/MacOS/CrossToolApp"
        expect_failure onepaw_postinstall "$FIXTURE/expected.plist"
        assert_preserved Crosio.app; assert_preserved crosstool.app ;;
    appeared_legacy)
        run_preinstall; install_new_fixture; make_app Crosio.app
        expect_failure onepaw_postinstall "$FIXTURE/expected.plist"; assert_preserved Crosio.app ;;
    backup_conflict)
        make_app Crosio.app; run_preinstall; install_new_fixture
        /bin/mkdir "$ONEPAW_TRANSACTION/Crosio.app.saved"
        expect_failure onepaw_postinstall "$FIXTURE/expected.plist"; assert_preserved Crosio.app ;;
    pending_conflict)
        make_app Crosio.app; run_preinstall
        expect_failure run_preinstall; assert_preserved Crosio.app ;;
    archive_failure)
        onepaw_archive_app() { return 1; }
        make_app Crosio.app; expect_failure run_preinstall; assert_preserved Crosio.app
        [[ ! -e "$ONEPAW_PENDING/preflight-ok" ]] || fail 'failed backup marked ready' ;;
    unsafe_transaction)
        run_preinstall; install_new_fixture
        /usr/bin/printf '%s\n' ../escape > "$ONEPAW_PENDING/transaction"
        expect_failure onepaw_postinstall "$FIXTURE/expected.plist" ;;
    backup_symlink)
        /bin/mv "$ONEPAW_BACKUPS" "$FIXTURE/real-backups"
        /bin/ln -s "$FIXTURE/real-backups" "$ONEPAW_BACKUPS"
        expect_failure onepaw_initialize "$FIXTURE" ;;
    real_unsigned_signature)
        make_app Crosio.app
        source "$COMMON"
        expect_failure onepaw_verify_app "$ONEPAW_APPLICATIONS/Crosio.app" ;;
    brand_metadata)
        source "$PROJECT_DIR/scripts/verify-app-brand.sh"
        make_app 一爪.app
        onepaw_verify_bundle_brand "$ONEPAW_APPLICATIONS/一爪.app/Contents/Info.plist" fixture
        /usr/bin/plutil -replace CFBundleName -string Crosio "$ONEPAW_APPLICATIONS/一爪.app/Contents/Info.plist"
        expect_failure onepaw_verify_bundle_brand "$ONEPAW_APPLICATIONS/一爪.app/Contents/Info.plist" fixture ;;
    newer_version|newer_build|invalid_version|huge_version)
        make_app 一爪.app
        if [[ "$CASE_NAME" == newer_build ]]; then
            /usr/bin/plutil -replace CFBundleVersion -string 48 "$ONEPAW_APPLICATIONS/一爪.app/Contents/Info.plist"
        elif [[ "$CASE_NAME" == newer_version ]]; then
            /usr/bin/plutil -replace CFBundleShortVersionString -string 0.7.0 "$ONEPAW_APPLICATIONS/一爪.app/Contents/Info.plist"
        elif [[ "$CASE_NAME" == huge_version ]]; then
            /usr/bin/plutil -replace CFBundleShortVersionString -string 999999999999999999999.1.0 "$ONEPAW_APPLICATIONS/一爪.app/Contents/Info.plist"
        else
            /usr/bin/plutil -replace CFBundleShortVersionString -string unknown "$ONEPAW_APPLICATIONS/一爪.app/Contents/Info.plist"
        fi
        expect_failure run_preinstall; assert_preserved 一爪.app
        [[ ! -e "$ONEPAW_PENDING" ]] || fail 'downgrade created a pending transaction' ;;
    leading_zero_version)
        make_app Crosio.app
        /usr/bin/plutil -replace CFBundleShortVersionString -string 00.06.006 "$ONEPAW_APPLICATIONS/Crosio.app/Contents/Info.plist"
        /usr/bin/plutil -replace CFBundleVersion -string 00046 "$ONEPAW_APPLICATIONS/Crosio.app/Contents/Info.plist"
        run_preinstall; finish_install; assert_migrated Crosio.app ;;
    package_layout)
        make_app 一爪.app
        /bin/chmod 755 "$ONEPAW_APPLICATIONS/一爪.app/Contents/MacOS/CrossToolApp"
        /bin/mkdir "$FIXTURE/scripts"
        /bin/cp "$PROJECT_DIR/scripts/installer/preinstall" "$PROJECT_DIR/scripts/installer/postinstall" \
            "$COMMON" "$FIXTURE/scripts/"
        /bin/cp "$ONEPAW_APPLICATIONS/一爪.app/Contents/Info.plist" "$FIXTURE/scripts/expected-app.plist"
        /usr/bin/pkgbuild --analyze --root "$ONEPAW_APPLICATIONS" "$FIXTURE/components.plist"
        [[ "$(/usr/bin/plutil -extract 0.RootRelativeBundlePath raw -o - "$FIXTURE/components.plist")" == 一爪.app ]] \
            || fail 'noncanonical PKG root'
        /usr/bin/plutil -replace 0.BundleIsRelocatable -bool NO "$FIXTURE/components.plist"
        /usr/bin/plutil -replace 0.BundleIsVersionChecked -bool YES "$FIXTURE/components.plist"
        /usr/bin/plutil -replace 0.BundleHasStrictIdentifier -bool YES "$FIXTURE/components.plist"
        /usr/bin/plutil -replace 0.BundleOverwriteAction -string upgrade "$FIXTURE/components.plist"
        /usr/bin/pkgbuild --root "$ONEPAW_APPLICATIONS" --scripts "$FIXTURE/scripts" \
            --component-plist "$FIXTURE/components.plist" --identifier com.cross.crosstool.pkg \
            --version 0.6.6.47 --install-location /Applications "$FIXTURE/一爪-fixture.pkg"
        /usr/sbin/pkgutil --expand "$FIXTURE/一爪-fixture.pkg" "$FIXTURE/expanded"
        for name in preinstall postinstall migration-common.sh expected-app.plist; do
            /usr/bin/cmp "$FIXTURE/scripts/$name" "$FIXTURE/expanded/Scripts/$name"
        done
        /usr/bin/grep -Fq 'identifier="com.cross.crosstool.pkg"' "$FIXTURE/expanded/PackageInfo"
        /usr/bin/grep -Fq 'install-location="/Applications"' "$FIXTURE/expanded/PackageInfo"
        /usr/bin/grep -Fq 'relocatable="false"' "$FIXTURE/expanded/PackageInfo"
        /usr/sbin/pkgutil --payload-files "$FIXTURE/一爪-fixture.pkg" \
            | /usr/bin/grep -Eq '^(\./)?一爪[.]app/Contents/MacOS/CrossToolApp$' ;;
    *) fail "unknown fixture case: $CASE_NAME" ;;
esac
