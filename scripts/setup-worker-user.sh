#!/bin/bash
# setup-worker-user.sh — one-time, idempotent setup of the Dark Factory worker sandbox (E5).
#
#   sudo scripts/setup-worker-user.sh [--owner <user>] [--worker <user>] [--slots <n>] [--work-root <dir>] [--swift] [--remove-worker-login]
#
# --swift (sc-26130; for a factory with Worker:Swift=true, which works Swift/Xcode repositories): makes the Swift toolchain
# check below required for every slot's user - swift, xcodebuild, Xcode's first launch done and its license accepted, and
# the macOS and iOS Simulator SDKs listed by `xcodebuild -showsdks` (CoreSimulator and simulator runtimes are not checked),
# each run as that user through its own helper; every slot is checked, then the users that failed are named and it exits 1.
# Without it the same check only reports (a .NET/Rust-only factory needs no Xcode), and is skipped when no Xcode is
# selected (`xcode-select -p` fails). The xcodebuild wrapper is installed either way.
#
# --slots <n> (1-8, default 1; the orchestrator's Worker:Slots, which must not be larger): how many sandbox
# users to set up, one per worker slot. The first is the worker user itself (default _factory, helper
# factory-worker-launch); slot 2 is <worker>2 (_factory2, helper factory-worker-launch-2), slot 3 <worker>3,
# and so on. Each gets everything below for itself, and its own copy of the launch helper pinned to its own
# name and uid. A re-run with a smaller <n> leaves the higher slots' accounts (and their homes) in place but
# drops their sudoers rules and removes their installed helpers, so nothing can be run as them and a factory
# whose Worker:Slots still names them fails at start-up ("cannot read the launch helper ...").
#
# --remove-worker-login (opt-in, standalone): deletes only each worker user's own Claude Code login — its
# ~/.claude/.credentials.json and its 'Claude Code-credentials' keychain item — so the worker holds no
# provider credential (E5; Worker:Auth=router-key, the default). It skips the setup entirely (no installs,
# no helper, no toolchain check), so it signals no process. With --slots <n> it does so for each of the n users.
#
# A normal (re)run KILLS EVERY PROCESS OF EVERY WORKER USER: its toolchain check runs commands through each
# launch helper, and each helper exit kills all of its user's processes (a running `factory work` worker,
# an interactive `sudo -u _factory` session). Close those sessions and stop `factory work` first.
#
# Creates a hidden role account per slot (default _factory, _factory2, …) whose workers:
#   - cannot traverse the owner's home (so no ~/.ssh, ~/.config/gh or login keychain),
#   - get dotnet, git and their own Claude Code install,
#   - run only through their own root-owned launch helper, which the owner may start as that
#     worker user without a password (one sudoers.d rule per slot, nothing else).
# The work root (clones + worktrees) is owned by the owner under a root-owned parent and is
# readable by every worker user; the orchestrator grants write access per worktree, to one slot's user.
# Re-running it changes nothing that is already in place.
set -euo pipefail

owner=${SUDO_USER:-}
worker=_factory
slots=1
work_root=/opt/dark-factory/work
helper_dir=/usr/local/libexec/dark-factory
helper=$helper_dir/factory-worker-launch
# The only xcodebuild command a worker is allowed (scripts/factory-xcodebuild); one copy for every slot, it pins nothing per user.
xcodebuild_wrapper=$helper_dir/factory-xcodebuild
sudoers=/private/etc/sudoers.d/dark-factory
script_dir=$(cd "$(dirname "$0")" && pwd)
remove_worker_login=0
swift=0

while [ $# -gt 0 ]; do
    case "$1" in
        --owner) owner=$2; shift 2 ;;
        --worker) worker=$2; shift 2 ;;
        --slots) slots=$2; shift 2 ;;
        --work-root) work_root=$2; shift 2 ;;
        --remove-worker-login) remove_worker_login=1; shift ;;
        --swift) swift=1; shift ;;
        *) echo "usage: sudo $0 [--owner <user>] [--worker <user>] [--slots <n>] [--work-root <dir>] [--swift] [--remove-worker-login]" >&2; exit 64 ;;
    esac
done

[ "$(uname -s)" = Darwin ] || { echo "macOS only." >&2; exit 1; }
[ "$(id -u)" -eq 0 ] || { echo "Run with sudo." >&2; exit 1; }
[[ $worker =~ ^_?[a-z][a-z0-9_-]*$ ]] || { echo "Invalid worker user name: $worker" >&2; exit 64; }
[[ $slots =~ ^[1-8]$ ]] || { echo "--slots must be a whole number from 1 to 8 (got '$slots')." >&2; exit 64; }

# The slots: slot 1 is the worker user and the helper as they were before slots; slot n adds n to both names
# (as the orchestrator does: WorkerSlots.UserOf, WorkerSlots.HelperOf).
first_worker=$worker
first_helper=$helper
slot_worker() { if [ "$1" -eq 1 ]; then echo "$first_worker"; else echo "$first_worker$1"; fi; }
slot_helper() { if [ "$1" -eq 1 ]; then echo "$first_helper"; else echo "$first_helper-$1"; fi; }
# Everything below works on one slot at a time, through these three.
use_slot() {
    worker=$(slot_worker "$1")
    helper=$(slot_helper "$1")
    worker_home=/Users/$worker
}
use_slot 1

say() { echo "==> $*"; }

# Commands run as the worker start in a directory it can enter (sudo keeps our cwd, which
# may be under the owner's home that step 2 closes to it).
cd /
as_worker() { sudo -u "$worker" env -i "HOME=$worker_home" PATH=/usr/bin:/bin:/usr/sbin:/sbin "$@"; }

# --remove-worker-login: delete each worker's own Claude login and nothing else. Never through the
# launch helper (its exit kills every process of the worker user), and done as the worker, never as
# root (root following a symlink the worker planted in its home could delete anything).
remove_login() {
    local kc status
    id "$worker" >/dev/null 2>&1 || { echo "No such worker user: $worker" >&2; exit 1; }
    say "Removing $worker's own Claude login (nothing else runs; no process is signalled)"
    as_worker /bin/rm -f "$worker_home/.claude/.credentials.json"
    kc=$worker_home/Library/Keychains/login.keychain-db
    if as_worker /bin/test -e "$kc"; then
        # One item per call; 44 = no (more) such item.
        while :; do
            status=0
            as_worker /usr/bin/security delete-generic-password -s 'Claude Code-credentials' "$kc" >/dev/null 2>&1 || status=$?
            [ "$status" -eq 0 ] && { echo "    deleted keychain item 'Claude Code-credentials'"; continue; }
            [ "$status" -eq 44 ] && break
            echo "Could not delete the 'Claude Code-credentials' item from $kc (security exit $status)." >&2
            echo "Delete it by hand, then re-run with --remove-worker-login." >&2
            exit 1
        done
    fi
    if as_worker /bin/test -e "$worker_home/.claude/.credentials.json"; then
        echo "$worker_home/.claude/.credentials.json still exists." >&2
        exit 1
    fi
    echo "    ok    $worker holds no Claude login"
}
if [ "$remove_worker_login" -eq 1 ]; then
    for slot in $(seq 1 "$slots"); do
        use_slot "$slot"
        remove_login
    done
    exit 0
fi

[ -n "$owner" ] && [ "$owner" != root ] || { echo "Pass --owner <your user> (or run via sudo from your account)." >&2; exit 64; }
id "$owner" >/dev/null 2>&1 || { echo "No such owner user: $owner" >&2; exit 1; }
owner_home=$(dscl . -read "/Users/$owner" NFSHomeDirectory | awk '{print $2}')
case "$work_root/" in
    /*) ;;
    *) echo "--work-root must be absolute." >&2; exit 64 ;;
esac
for slot in $(seq 1 "$slots"); do
    use_slot "$slot"
    case "$work_root/" in
        "$owner_home"/* | "$worker_home"/*)
            echo "--work-root must be outside every home (a worker can't enter the owner's; it could swap paths in its own)." >&2
            exit 64 ;;
    esac
done

# Removes then adds an ACL entry so re-runs don't stack duplicates. Never recursive: chmod -R
# applies ACLs to symlink targets, and this runs as root over trees the worker can write.
set_acl() { # set_acl <entry> <path>
    chmod -a "$1" "$2" 2>/dev/null || true
    chmod +a "$1" "$2"
}

free_id() { # lowest id in 400-499 unused as both a UID and a GID
    local used id
    used=$( (dscl . -list /Users UniqueID; dscl . -list /Groups PrimaryGroupID) | awk '{print $2}')
    for id in $(seq 499 -1 400); do
        if ! grep -qx "$id" <<<"$used"; then echo "$id"; return; fi
    done
    echo "No free id in 400-499" >&2; exit 1
}

# --- 1. Hidden role account with its own group (not staff), one per slot ---------------
# Sets uid to the slot's worker's.
create_account() {
    local gid
    if ! dscl . -read "/Groups/$worker" >/dev/null 2>&1; then
        gid=$(free_id)
        say "Creating group $worker ($gid)"
        dscl . -create "/Groups/$worker"
        dscl . -create "/Groups/$worker" PrimaryGroupID "$gid"
        dscl . -create "/Groups/$worker" RealName "Dark Factory worker"
        dscl . -create "/Groups/$worker" Password '*'
    fi
    gid=$(dscl . -read "/Groups/$worker" PrimaryGroupID | awk '{print $2}')

    if ! dscl . -read "/Users/$worker" >/dev/null 2>&1; then
        uid=$(free_id)
        say "Creating hidden user $worker ($uid)"
        dscl . -create "/Users/$worker"
        dscl . -create "/Users/$worker" UniqueID "$uid"
        dscl . -create "/Users/$worker" PrimaryGroupID "$gid"
        dscl . -create "/Users/$worker" RealName "Dark Factory worker"
        dscl . -create "/Users/$worker" NFSHomeDirectory "$worker_home"
        dscl . -create "/Users/$worker" UserShell /usr/bin/false
        dscl . -create "/Users/$worker" Password '*'
        dscl . -create "/Users/$worker" IsHidden 1
    fi
    # The helper's uid sweep runs only as this uid, which it pins (step 4): it must be one this script allocates.
    uid=$(dscl . -read "/Users/$worker" UniqueID | awk '{print $2}')
    if ! [[ $uid =~ ^[0-9]+$ ]] || [ "$uid" -lt 400 ] || [ "$uid" -gt 499 ]; then
        echo "$worker has uid '$uid', outside 400-499: not a role account this script created. Pick another --worker." >&2
        exit 1
    fi

    if [ ! -d "$worker_home" ]; then
        say "Creating $worker_home"
        mkdir -p "$worker_home"
    fi
    chown "$worker:$worker" "$worker_home"
    chmod 0700 "$worker_home"
}

# Before anything is installed or changed for any slot: every slot's account exists with a uid of its own
# in the range. Each helper sweeps by its pinned uid, so two slots sharing one would kill each other's workers.
uids=()
for slot in $(seq 1 "$slots"); do
    use_slot "$slot"
    create_account
    for other in ${uids[@]+"${uids[@]}"}; do
        if [ "$other" = "$uid" ]; then
            echo "$worker has uid $uid, which another slot's worker user already has: each slot needs a uid of its own." >&2
            exit 1
        fi
    done
    uids+=("$uid")
done

# --- 2. Close the owner's home to each worker -----------------------------------------
# Denying traversal at the top covers ~/.ssh, ~/.config/gh, ~/Library/Keychains and the rest.
# (Each worker's own home is 0700, so no worker enters another's.)
for slot in $(seq 1 "$slots"); do
    use_slot "$slot"
    say "Denying $worker access to $owner_home"
    set_acl "user:$worker deny list,search,read,add_file,add_subdirectory,delete_child,readattr,readextattr,readsecurity" "$owner_home"
done

# --- 3. Work root: owner-owned, worker-readable, under a root-owned parent -------------
say "Preparing work root $work_root"
parent=$(dirname "$work_root")
if [ ! -d "$parent" ]; then
    install -d -o root -g wheel -m 0755 "$parent"
fi
[ "$(stat -f %Su "$parent")" = root ] || { echo "$parent must be owned by root so the worker can't swap the work root." >&2; exit 1; }
mkdir -p "$work_root"
chown "$owner:staff" "$work_root"
chmod 0700 "$work_root"
# Only the work root itself: clones and worktrees created under it later inherit the entry.
# (A clone made before this ran stays unreadable to the worker; delete $work_root/repos to re-clone.)
for slot in $(seq 1 "$slots"); do
    use_slot "$slot"
    set_acl "user:$worker allow list,search,read,readattr,readextattr,readsecurity,file_inherit,directory_inherit" "$work_root"
done

# --- 4. Launch helpers (root-owned, one per slot) and one sudoers rule each ------------
install -d -o root -g wheel -m 0755 "$helper_dir"
rule=$(mktemp)
for slot in $(seq 1 "$slots"); do
    use_slot "$slot"
    uid=${uids[$((slot - 1))]}
    say "Installing $helper"
    # The helper kills every process of sandbox_user when a run ends, so each copy must name its own worker and
    # pin its uid. The orchestrator compares each installed file with the repo's after these same two edits.
    helper_src=$(mktemp)
    sed -e "s/^sandbox_user=.*/sandbox_user=$worker/" -e "s/^sandbox_uid=.*/sandbox_uid=$uid/" \
        "$script_dir/factory-worker-launch" >"$helper_src"
    grep -qx "sandbox_user=$worker" "$helper_src" || { echo "Could not set sandbox_user in the helper." >&2; exit 1; }
    grep -qx "sandbox_uid=$uid" "$helper_src" || { echo "Could not set sandbox_uid in the helper." >&2; exit 1; }
    install -o root -g wheel -m 0755 "$helper_src" "$helper"
    rm -f "$helper_src"
    # One rule per slot: this helper, as this worker only (never another slot's helper as this user).
    cat >>"$rule" <<EOF
# Dark Factory: $owner may run only the worker launch helper $helper, only as $worker.
Defaults!$helper !use_pty, !log_output
$owner ALL=($worker) NOPASSWD: $helper
EOF
done
visudo -cqf "$rule"
install -o root -g wheel -m 0440 "$rule" "$sudoers"
rm -f "$rule"

# The xcodebuild wrapper (sc-26130): root-owned and unedited (the orchestrator compares it with the repo's when Worker:Swift is
# on). Workers run it as themselves by its absolute path, the one xcodebuild rule they are allowed; it needs no sudoers rule.
say "Installing $xcodebuild_wrapper"
install -o root -g wheel -m 0755 "$script_dir/factory-xcodebuild" "$xcodebuild_wrapper"

# Slots above <n> (a re-run with a smaller --slots): the sudoers file just installed holds no rule for them, so
# their installed helpers are removed too. Left in place they would still pass the orchestrator's start-up
# check (it only reads the files) with Worker:Slots unchanged, and the factory would fail later, in its first
# worker phase, with sudo's "a password is required". This runs as root, so it names only the fixed paths this
# script itself installs for slots 2-8 - never slot 1's helper, never a glob, never a directory (no -r).
remove_slot_helper() { # remove_slot_helper <slot 2-8>
    local stale
    [[ $1 =~ ^[2-8]$ ]] || { echo "Refusing to remove the helper of slot '$1': not a slot from 2 to 8." >&2; exit 1; }
    stale=$(slot_helper "$1")
    [ "$stale" = "$helper_dir/factory-worker-launch-$1" ] \
        || { echo "Refusing to remove '$stale': not slot $1's helper in $helper_dir." >&2; exit 1; }
    rm -f -- "$stale"
}
unused_workers=()
for ((slot = slots + 1; slot <= 8; slot++)); do
    remove_slot_helper "$slot"
    if dscl . -read "/Users/$(slot_worker "$slot")" >/dev/null 2>&1; then
        unused_workers+=("$(slot_worker "$slot")")
    fi
done
if [ "${#unused_workers[@]}" -gt 0 ]; then
    say "No longer set up (more slots than --slots $slots): ${unused_workers[*]}"
    echo "    Each still exists on this machine, with its home and whatever it owns under $work_root, but has no"
    echo "    launch helper and no sudoers rule now: nothing can be run as it. Keep Worker:Slots at $slots or lower,"
    echo "    or set them up again with: sudo $0 --slots <n>"
fi
grep -q '^#includedir /private/etc/sudoers.d' /etc/sudoers \
    || echo "warning: /etc/sudoers has no '#includedir /private/etc/sudoers.d'; the rule is inactive." >&2

# --- 5. Toolchain: dotnet and git are system-wide; Claude Code installs per user ------
for slot in $(seq 1 "$slots"); do
    use_slot "$slot"
    if [ ! -x "$worker_home/.local/bin/claude" ]; then
        say "Installing Claude Code for $worker (native installer)"
        as_worker /bin/bash -c 'cd ~ && curl -fsSL https://claude.ai/install.sh | bash'
    fi
done

# Runs <command...> (the helper) the way the orchestrator does: the empty variable block on
# stdin, then stdin held open until the command exits. The helper takes stdin EOF as Stop and
# kills the worker, so piping the block in (stdin closes at once) kills the tool mid-run.
with_open_stdin() {
    local dir pid status=0
    dir=$(mktemp -d)
    mkfifo "$dir/stdin"
    "$@" <"$dir/stdin" &
    pid=$!
    exec 3>"$dir/stdin"
    printf '\n' >&3
    wait "$pid" || status=$?
    exec 3>&-
    rm -rf "$dir"
    return "$status"
}

# check_toolchain <command line>... - runs each as the worker through the installed helper and
# reports its version; explains and returns 1 if any fails.
check_toolchain() {
    local tool out status failed=0
    for tool in "$@"; do
        # shellcheck disable=SC2086 # tool is a command line
        if out=$(with_open_stdin sudo -u "$owner" sudo -n -u "$worker" "$helper" $tool 2>&1); then
            echo "    ok    $tool: $out"
        else
            status=$?
            echo "    FAIL  $tool (exit $status): $out" >&2
            failed=1
        fi
    done
    [ "$failed" -eq 0 ] && return 0
    cat >&2 <<EOF
The toolchain check failed. By exit code:
  1 with 'a password is required': the sudoers rule is inactive - check that /etc/sudoers
     has '#includedir /private/etc/sudoers.d' and that $sudoers exists.
  127 or 'No such file': the tool is missing for $worker - dotnet must be at
     /usr/local/share/dotnet (the .NET SDK installer), git needs the Xcode Command Line Tools
     (xcode-select --install), Claude Code must be at $worker_home/.local/bin/claude (delete
     it and re-run this script to reinstall).
  137 (Killed): the helper stopped the tool because its stdin closed - this check must
     hold stdin open until the tool exits.
Fix the cause and re-run this script; every step is safe to repeat.
EOF
    return 1
}

# check_optional_toolchain <what needs it> <command line>... - like check_toolchain for tools only
# some target repositories need: reports each one's version, or that it is missing and what
# needs it. Never fails the setup.
check_optional_toolchain() {
    local needs=$1 tool out status
    shift
    for tool in "$@"; do
        # shellcheck disable=SC2086 # tool is a command line
        if out=$(with_open_stdin sudo -u "$owner" sudo -n -u "$worker" "$helper" $tool 2>&1); then
            echo "    ok    $tool: $out"
        else
            status=$?
            echo "    none  $tool (exit $status): not available to $worker ($needs)"
        fi
    done
    return 0
}

swift_tools=("/usr/bin/swift --version" "/usr/bin/xcodebuild -version" "/usr/bin/xcodebuild -checkFirstLaunchStatus" "/usr/bin/xcodebuild -license check")
# The SDKs a Swift factory builds with (SwiftToolchain.RequiredSdks): macOS apps, and iOS apps on the simulator.
swift_sdks="macosx iphonesimulator"

# check_swift_sdks - `xcodebuild -showsdks` as the worker through the helper must list every SDK of $swift_sdks.
# It lists SDKs and starts no simulator (CoreSimulator is not checked here). Returns 1 naming each one missing.
check_swift_sdks() {
    local out sdk missing=0
    if ! out=$(with_open_stdin sudo -u "$owner" sudo -n -u "$worker" "$helper" /usr/bin/xcodebuild -showsdks 2>&1); then
        echo "    FAIL  /usr/bin/xcodebuild -showsdks: $out" >&2
        return 1
    fi
    for sdk in $swift_sdks; do
        case "$out" in
            *"-sdk $sdk"*) echo "    ok    SDK $sdk" ;;
            *) echo "    FAIL  /usr/bin/xcodebuild -showsdks lists no $sdk SDK (iphonesimulator: sudo xcodebuild -downloadPlatform iOS)" >&2; missing=1 ;;
        esac
    done
    [ "$missing" -eq 0 ]
}

# --swift: the slots whose Swift toolchain failed, collected across every slot and named at the end.
swift_failed_users=

for slot in $(seq 1 "$slots"); do
    use_slot "$slot"
    echo "warning: the toolchain check runs commands as $worker through the helper, and each helper exit KILLS EVERY" >&2
    echo "         $worker process (a running factory work worker, any sudo -u $worker session). Close those first." >&2
    say "Checking the toolchain as $worker through the helper"
    check_toolchain "/usr/local/share/dotnet/dotnet --version" "git --version" "claude --version" || exit 1

    # Rust target repositories only (sc-25875): the merge gate's new-tests check runs cargo as the
    # worker. cargo and rustc must be on the helper's fixed PATH - /opt/homebrew/bin or
    # /usr/local/bin (e.g. `brew install rust`, or rustup installed as $worker with its cargo and
    # rustc linked into /usr/local/bin). Without them every Rust pull request's check is an error
    # (toolchain-missing), never a pass; a factory that builds only .NET needs neither.
    say "Checking the optional Rust toolchain as $worker through the helper"
    check_optional_toolchain "Rust repos only: the new-tests check of a Rust pull request fails without it" "cargo --version" "rustc --version"

    # Swift/Xcode target repositories only (sc-26130): the orchestrator with Worker:Swift=true probes the same four commands as
    # each slot's user before a worker runs in that slot. Xcode itself is system-wide; its first launch and license are too
    # (sudo xcodebuild -runFirstLaunch, sudo xcodebuild -license accept), and each user's DerivedData and SwiftPM caches are
    # under its own home.
    if [ "$swift" -eq 1 ]; then
        say "Checking the Swift toolchain as $worker through the helper (--swift: required)"
        if ! check_toolchain "${swift_tools[@]}" || ! check_swift_sdks; then
            swift_failed_users="$swift_failed_users $worker (worker slot $slot);"
        fi
    elif ! xcode-select -p >/dev/null 2>&1; then
        say "Checking the optional Swift toolchain as $worker: skipped: no Xcode selected (xcode-select -p fails)"
    else
        say "Checking the optional Swift toolchain as $worker through the helper"
        check_optional_toolchain "Swift repos only (Worker:Swift=true; then re-run with --swift)" "${swift_tools[@]}" "/usr/bin/xcodebuild -showsdks"
    fi

    say "Done. Worker user: $worker  Helper: $helper  Work root: $work_root"
done

if [ -n "$swift_failed_users" ]; then
    cat >&2 <<EOF
The Swift toolchain is not usable by:${swift_failed_users%;}
which Worker:Swift=true needs (each FAIL line above names the command). By command:
  swift / xcodebuild -version: install Xcode and select it (sudo xcode-select -s /Applications/Xcode.app).
  xcodebuild -checkFirstLaunchStatus: sudo xcodebuild -runFirstLaunch
  xcodebuild -license check: sudo xcodebuild -license accept
  xcodebuild -showsdks without iphonesimulator: sudo xcodebuild -downloadPlatform iOS
Then re-run this script with --swift (or set Worker:Swift=false if the factory works no Swift repository).
EOF
    exit 1
fi

slots_flag=
[ "$slots" -eq 1 ] || slots_flag=" --slots $slots"
use_slot 1
cat <<EOF

Workers use Worker:Auth=router-key (the default) and hold only the router key: enroll your plans on the
router instead (router login claude, router login codex). If a worker user still has a Claude login of its
own, remove it (that runs nothing else and signals no process):
    sudo $0$slots_flag --remove-worker-login
Worker:Auth=claude-login (weaker: the worker holds a Claude login, which E5 forbids) needs each worker's
own login instead (once per worker user):
    sudo -u $worker -H $worker_home/.local/bin/claude      # then /login, then /exit
EOF
if [ "$slots" -gt 1 ]; then
    echo
    echo "Set Worker__Slots=$slots for the factory (a Worker:Slots larger than the slots set up here fails start-up:"
    echo "the higher slots have no launch helper)."
fi
