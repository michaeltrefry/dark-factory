#!/bin/bash
# setup-worker-user.sh — one-time, idempotent setup of the Dark Factory worker sandbox (E5).
#
#   sudo scripts/setup-worker-user.sh [--owner <user>] [--worker <user>] [--slots <n>] [--work-root <dir>] [--home-root <dir>] [--swift] [--remove-worker-login]
#
# --home-root <dir> (sc-26579; default none: each worker's home is /Users/<worker>, as before): each slot's user is
# created with its home at <dir>/<worker> (mode 0700, owned by that user) - e.g. on a second volume, since the homes hold
# the large per-user caches (DerivedData, SwiftPM, Cargo, NuGet, simulator devices). <dir> must be on a local APFS volume
# that honours ownership (diskutil info: Owners: Enabled), outside the owner's home and the work root, and owned by root
# (created root:wheel 0755 when missing); all of it is checked before any account is created or changed. A worker that
# already exists keeps its home: a run whose home for it (with or without --home-root) differs from the one its account
# records is refused, never moved. The orchestrator and the launch helper read HOME from the password database.
# --remove-worker-login uses each worker's recorded home and ignores --home-root.
#
# --swift (sc-26130; for a factory with Worker:Swift=true, which works Swift/Xcode repositories): makes the Swift toolchain
# check below required for every slot's user - swift, xcodebuild, Xcode's first launch done and its license accepted, and
# the macOS and iOS Simulator SDKs listed by `xcodebuild -showsdks`, and (sc-26134) a GUI login session (`launchctl
# managername` is Aqua) in which `xcrun simctl list runtimes -j` lists an available iOS runtime and `xcrun simctl list
# devicetypes -j` an iPhone device type (no simulator is booted), each run as that user through its own helper; every slot is checked, then the users that failed are named and it exits 1.
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
# The workers' factory-prepare (scripts/factory-prepare, sc-26232; Worker:Prepare=true): one copy for every slot.
prepare_tool=$helper_dir/factory-prepare
sudoers=/private/etc/sudoers.d/dark-factory
script_dir=$(cd "$(dirname "$0")" && pwd)
remove_worker_login=0
swift=0
home_root=

while [ $# -gt 0 ]; do
    case "$1" in
        --owner) owner=$2; shift 2 ;;
        --worker) worker=$2; shift 2 ;;
        --slots) slots=$2; shift 2 ;;
        --work-root) work_root=$2; shift 2 ;;
        --home-root) home_root=$2; shift 2 ;;
        --remove-worker-login) remove_worker_login=1; shift ;;
        --swift) swift=1; shift ;;
        *) echo "usage: sudo $0 [--owner <user>] [--worker <user>] [--slots <n>] [--work-root <dir>] [--home-root <dir>] [--swift] [--remove-worker-login]" >&2; exit 64 ;;
    esac
done

[ "$(uname -s)" = Darwin ] || { echo "macOS only." >&2; exit 1; }
[ "$(id -u)" -eq 0 ] || { echo "Run with sudo." >&2; exit 1; }
[[ $worker =~ ^_?[a-z][a-z0-9_-]*$ ]] || { echo "Invalid worker user name: $worker" >&2; exit 64; }
[[ $slots =~ ^[1-8]$ ]] || { echo "--slots must be a whole number from 1 to 8 (got '$slots')." >&2; exit 64; }
if [ -n "$home_root" ]; then
    # Absolute, not /, no . or .. component, no space (dscl's NFSHomeDirectory is read back word by word).
    while [ "${home_root%/}" != "$home_root" ]; do home_root=${home_root%/}; done
    if ! [[ $home_root =~ ^(/[A-Za-z0-9._-]+)+$ ]] || [[ $home_root/ =~ /\.\.?/ ]]; then
        echo "--home-root must be an absolute directory other than /, with no . or .. component and no space (got '$home_root')." >&2
        exit 64
    fi
fi

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
    if [ -n "$home_root" ]; then worker_home=$home_root/$worker; else worker_home=/Users/$worker; fi
}
use_slot 1

say() { echo "==> $*"; }

# Commands run as the worker start in a directory it can enter (sudo keeps our cwd, which
# may be under the owner's home that step 2 closes to it).
cd /
as_worker() { sudo -u "$worker" env -i "HOME=$worker_home" PATH=/usr/bin:/bin:/usr/sbin:/sbin "$@"; }

# account_home <user> - the home the user's account records (dscl prints it after the key, or wrapped on the next line).
account_home() {
    { dscl . -read "/Users/$1" NFSHomeDirectory 2>/dev/null || true; } \
        | awk '{ for (i = 1; i <= NF; i++) if ($i != "NFSHomeDirectory:") { print $i; exit } }'
}

# --remove-worker-login: delete each worker's own Claude login and nothing else. Never through the
# launch helper (its exit kills every process of the worker user), and done as the worker, never as
# root (root following a symlink the worker planted in its home could delete anything).
remove_login() {
    local kc status
    id "$worker" >/dev/null 2>&1 || { echo "No such worker user: $worker" >&2; exit 1; }
    # The home its account records (it may be under a --home-root), never one derived from the options.
    worker_home=$(account_home "$worker")
    [[ $worker_home == /?* ]] || { echo "$worker's account records no absolute home ('$worker_home')." >&2; exit 1; }
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
[[ $owner =~ ^[a-zA-Z_][a-zA-Z0-9_-]*$ ]] || { echo "Invalid owner user name." >&2; exit 64; }
id "$owner" >/dev/null 2>&1 || { echo "No such owner user: $owner" >&2; exit 1; }
# A root sudoers entry must never resolve through an owner/worker-writable ancestor. Refuse
# unsafe existing parents rather than silently changing system directory permissions.
# Mode bits alone do not prove trust: ACL security/ownership rights can grant write access.
# Unknown ACL syntax and non-root rights fail closed, including inheritance-only entries.
root_acl_trusted() {
    local listing
    listing=$(ls -lde "$1") || return 1
    printf '%s\n' "$listing" | awk '
        NR == 1 { if ($0 !~ /^[-d]rwxr-xr-x[+@ ]/) exit 1; next }
        {
            if ($0 !~ /^[ \t]*[0-9]+: (user|group):[^ \t]+ (inherited )?(allow|deny) /) exit 1
            sub(/^[ \t]*[0-9]+: /, "")
            principal = $1; $1 = ""; sub(/^[ \t]+/, "")
            sub(/^inherited /, "")
            if ($1 == "deny" || principal == "user:root") next
            $1 = ""; gsub(/[ \t]/, "")
            n = split($0, rights, ",")
            for (i = 1; i <= n; i++)
                if (rights[i] !~ /^(read|list|search|execute|readattr|readextattr|readsecurity|file_inherit|directory_inherit|only_inherit|limit_inherit|inherited)$/) exit 1
        }
        END { if (NR == 0) exit 1 }'
}
for trusted in / /usr /usr/local /usr/local/libexec "$helper_dir" \
    "$helper" "$helper-2" "$helper-3" "$helper-4" "$helper-5" "$helper-6" "$helper-7" "$helper-8" \
    "$xcodebuild_wrapper" "$prepare_tool"; do
    if [ -e "$trusted" ] || [ -L "$trusted" ]; then
        [ ! -L "$trusted" ] && [ "$(stat -f %Su "$trusted")" = root ] && [ "$(stat -f %Lp "$trusted")" = 755 ] \
            && root_acl_trusted "$trusted" \
            || { echo "Session trampoline parent $trusted must be a root-owned 0755 directory without links; nothing was installed. ACLs must grant no non-root mutation rights." >&2; exit 1; }
    fi
done
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

# --home-root, checked before any account is created or changed.
#  - Outside the owner's home and the work root (as given, and as resolved below).
#  - Its parent exists (nothing above it is created: under /Volumes an unmounted volume would otherwise leave the homes on
#    the boot disk, and the volume, mounted later, at "/Volumes/<name> 1"), and the path is resolved through it (pwd -P), so
#    the checks below and the homes are on the volume the directory really is on; the root itself may not be a symlink.
#  - That volume is a local APFS volume that honours ownership: on one that ignores it (Owners: Disabled, the default for
#    an added disk) every file reads as owned by whoever looks, so a home's owner and its 0700 would keep no worker out of
#    another's. Under /Volumes/<name> it must be the volume mounted there.
#  - No directory above it can be changed by a worker: each is owned by root or the owner and writable by no one else (or
#    root's and sticky, like /tmp), so no worker can move the home root away and put its own in its place.
if [ -n "$home_root" ]; then
    home_root_outside() {
        case "$home_root/" in
            "$owner_home"/* | "$work_root"/*)
                echo "--home-root must be outside the owner's home (no worker can enter it) and the work root (every worker reads it)." >&2
                exit 64 ;;
        esac
    }
    home_root_outside
    home_parent=$(dirname "$home_root")
    if [ ! -d "$home_parent" ]; then
        echo "--home-root $home_root: $home_parent does not exist. Mount the volume (or create the directory the homes go in) first: setup creates only the home root itself." >&2
        exit 1
    fi
    home_root=$(cd -P "$home_parent" && pwd -P)/$(basename "$home_root")
    home_root=${home_root#/}; home_root=/$home_root # a parent of / gives //name
    home_parent=$(dirname "$home_root")
    home_root_outside
    if [ -L "$home_root" ] || { [ -e "$home_root" ] && [ ! -d "$home_root" ]; }; then
        echo "--home-root $home_root is a symlink or not a directory: give the real directory." >&2
        exit 1
    fi
    if [ -d "$home_root" ]; then on=$home_root; else on=$home_parent; fi
    device=$(stat -f %Sd "$on" 2>/dev/null || true)
    volume_info=
    if [[ $device =~ ^disk[0-9]+(s[0-9]+)*$ ]]; then
        volume_info=$(diskutil info "/dev/$device" 2>/dev/null || true)
    fi
    volume_field() { printf '%s\n' "$volume_info" | sed -n "s/^ *$1: *//p" | head -n 1; }
    volume_mount=$(volume_field 'Mount Point')
    volume="the volume of $on ($(volume_field 'Volume Name'), mounted at ${volume_mount:-nowhere}, /dev/$device)"
    if [ -z "$volume_info" ] || [ "$(volume_field 'Device Node')" != "/dev/$device" ]; then
        echo "--home-root $home_root: $on is not on a local disk volume (device '${device:-none}' is unknown to diskutil). Put the homes on a local APFS volume (diskutil apfs addVolume, or a second disk erased as APFS)." >&2
        exit 1
    fi
    case "$home_root/" in
        /Volumes/?*)
            mounted_at=${home_root#/Volumes/}; mounted_at=/Volumes/${mounted_at%%/*}
            if [ "$volume_mount" != "$mounted_at" ]; then
                echo "--home-root $home_root: $on is on $volume, not on a volume mounted at $mounted_at. Mount that volume first." >&2
                exit 1
            fi ;;
    esac
    case "$(volume_field 'File System Personality')" in
        *APFS*) ;;
        *)  echo "--home-root $home_root: $volume is $(volume_field 'File System Personality'), not APFS. Put the homes on a local APFS volume (diskutil apfs addVolume, or a second disk erased as APFS)." >&2
            exit 1 ;;
    esac
    if [ "$(volume_field 'Owners')" != Enabled ]; then
        echo "--home-root $home_root: $volume ignores ownership (Owners: $(volume_field 'Owners')). Enable it, then re-run: sudo diskutil enableOwnership '${volume_mount:-<its mount point>}'" >&2
        exit 1
    fi
    dir=$home_parent
    while :; do
        dir_owner=$(stat -f %Su "$dir" 2>/dev/null || true)
        dir_mode=$(stat -f %Lp "$dir" 2>/dev/null || true)
        if ! [[ $dir_mode =~ ^[0-7]{3,4}$ ]] || { [ "$dir_owner" != root ] && [ "$dir_owner" != "$owner" ]; } \
            || { (( 8#$dir_mode & 8#022 )) && ! { [ "$dir_owner" = root ] && (( 8#$dir_mode & 8#1000 )); }; }; then
            echo "--home-root $home_root: $dir (owner '$dir_owner', mode '$dir_mode') must be owned by root or $owner and writable by no one" >&2
            echo "else (or root's and sticky), so no worker can move the home root away. Choose another --home-root, or fix $dir." >&2
            exit 1
        fi
        [ "$dir" = / ] && break
        dir=$(dirname "$dir")
    done
fi

# A worker that already exists keeps its home: a different one (another --home-root, or none) is refused, never moved - its
# caches, Claude Code and transcripts are there, and the orchestrator and the helper read HOME from its account.
for slot in $(seq 1 "$slots"); do
    use_slot "$slot"
    if dscl . -read "/Users/$worker" >/dev/null 2>&1; then
        recorded=$(account_home "$worker")
        if [ "$recorded" != "$worker_home" ]; then
            if [ "$recorded" = "/Users/$worker" ]; then was=" (no --home-root)"
            elif [ "$(basename "$recorded")" = "$worker" ]; then was=" (--home-root $(dirname "$recorded"))"
            else was=; fi
            echo "$worker already exists with its home at '$recorded', not $worker_home: setup never moves a home." >&2
            echo "Re-run with the home root it was set up with$was, or move the home and its account's" >&2
            echo "NFSHomeDirectory yourself (dscl . -create /Users/$worker NFSHomeDirectory <home>) with the factory stopped." >&2
            exit 1
        fi
    fi
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
# The home root (--home-root): owned by root, so no worker can swap another's home in it.
if [ -n "$home_root" ]; then
    [ -d "$home_root" ] || install -d -o root -g wheel -m 0755 "$home_root"
    home_mode=$(stat -f %Lp "$home_root" 2>/dev/null || true)
    if [ "$(stat -f %Su "$home_root" 2>/dev/null || true)" != root ] || ! [[ $home_mode =~ ^[0-7]{3,4}$ ]] || (( 8#$home_mode & 8#022 )); then
        echo "$home_root must be owned by root and writable by no one else, so no worker can swap another's home in it" >&2
        echo "(sudo chown root:wheel $home_root && sudo chmod go-w $home_root)." >&2
        exit 1
    fi
fi

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
    sed -e "s/^sandbox_user=.*/sandbox_user=$worker/" -e "s/^sandbox_uid=.*/sandbox_uid=$uid/" -e "s/^sandbox_owner=.*/sandbox_owner=$owner/" \
        "$script_dir/factory-worker-launch" >"$helper_src"
    grep -qx "sandbox_user=$worker" "$helper_src" || { echo "Could not set sandbox_user in the helper." >&2; exit 1; }
    grep -qx "sandbox_uid=$uid" "$helper_src" || { echo "Could not set sandbox_uid in the helper." >&2; exit 1; }
    grep -qx "sandbox_owner=$owner" "$helper_src" || { echo "Could not set sandbox_owner in the helper." >&2; exit 1; }
    install -o root -g wheel -m 0755 "$helper_src" "$helper"
    rm -f "$helper_src"
    # One root command per slot. It authenticates the pinned owner, adopts only this user's
    # existing GUI login, and drops uid before reading stdin or executing the requested program.
    cat >>"$rule" <<EOF
# Dark Factory: $owner may run only the pinned session trampoline $helper, only as root.
Defaults!$helper !use_pty, !log_output
$owner ALL=(root) NOPASSWD: $helper
EOF
done
visudo -cqf "$rule"
install -o root -g wheel -m 0440 "$rule" "$sudoers"
rm -f "$rule"

# The xcodebuild wrapper (sc-26130): root-owned and unedited (the orchestrator compares it with the repo's when Worker:Swift is
# on). Workers run it as themselves by its absolute path, the one xcodebuild rule they are allowed; it needs no sudoers rule.
say "Installing $xcodebuild_wrapper"
install -o root -g wheel -m 0755 "$script_dir/factory-xcodebuild" "$xcodebuild_wrapper"

# factory-prepare (sc-26232): root-owned and unedited (the orchestrator compares it with the repo's when Worker:Prepare is on).
# Workers run it as themselves by its absolute path; it downloads nothing (the factory owner does) and needs no sudoers rule.
say "Installing $prepare_tool"
install -o root -g wheel -m 0755 "$script_dir/factory-prepare" "$prepare_tool"

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
        if out=$(with_open_stdin sudo -u "$owner" sudo -n -u root "$helper" $tool 2>&1); then
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
        if out=$(with_open_stdin sudo -u "$owner" sudo -n -u root "$helper" $tool 2>&1); then
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
# It lists SDKs and starts no simulator (CoreSimulator: check_swift_simulator). Returns 1 naming each one missing.
check_swift_sdks() {
    local out sdk missing=0
    if ! out=$(with_open_stdin sudo -u "$owner" sudo -n -u root "$helper" /usr/bin/xcodebuild -showsdks 2>&1); then
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

# simctl_lists <runtimes|devicetypes> - reads `xcrun simctl list <what> -j` on stdin; succeeds when it lists an available iOS
# runtime (a runtime object with "platform": "iOS" and "isAvailable": true) or an iPhone device type ("productFamily":
# "iPhone"). Only each list entry's own fields count (brace depth 2), not those of the objects nested in it.
simctl_lists() {
    awk -v what="$1" '
        { text = text $0 " " }
        END {
            depth = 0; entry = ""; found = 0
            for (i = 1; i <= length(text); i++) {
                c = substr(text, i, 1)
                if (c == "{") { depth++; if (depth == 2) entry = ""; continue }
                if (c == "}") {
                    if (depth == 2) {
                        if (what == "runtimes") { if (entry ~ /"platform"[ ]*:[ ]*"iOS"/ && entry ~ /"isAvailable"[ ]*:[ ]*true/) found = 1 }
                        else if (entry ~ /"productFamily"[ ]*:[ ]*"iPhone"/) found = 1
                    }
                    depth--
                    continue
                }
                if (depth == 2) entry = entry c
            }
            exit found ? 0 : 1
        }'
}

# check_swift_simulator - as the worker through the helper, booting nothing (sc-26134): its processes are in a GUI (Aqua) login
# session (`launchctl managername`), and only then - CoreSimulator answers a user only in one - `xcrun simctl list runtimes -j`
# lists an available iOS runtime and `xcrun simctl list devicetypes -j` an iPhone device type. Returns 1 naming what failed.
check_swift_simulator() {
    local out what
    out=$(with_open_stdin sudo -u "$owner" sudo -n -u root "$helper" /bin/launchctl managername 2>&1)
    if [ "$out" != Aqua ]; then
        echo "    FAIL  /bin/launchctl managername is '$out', not 'Aqua': the worker user needs a GUI login OF ITS OWN (sc-26789: the helper refuses to run it in the owner's session or without a login; see docs/acceptance.md, S3). Run this script from the owner's console login too (a Terminal at the console, not over ssh)" >&2
        return 1
    fi
    echo "    ok    /bin/launchctl managername: Aqua"
    for what in runtimes devicetypes; do
        if ! out=$(with_open_stdin sudo -u "$owner" sudo -n -u root "$helper" /usr/bin/xcrun simctl list "$what" -j 2>&1); then
            echo "    FAIL  /usr/bin/xcrun simctl list $what -j: $out" >&2
            return 1
        fi
        if ! printf '%s\n' "$out" | simctl_lists "$what"; then
            echo "    FAIL  /usr/bin/xcrun simctl list $what -j lists no $([ "$what" = runtimes ] && echo 'available iOS simulator runtime' || echo 'iPhone simulator device type') (sudo xcodebuild -downloadPlatform iOS)" >&2
            return 1
        fi
        echo "    ok    /usr/bin/xcrun simctl list $what -j"
    done
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
        if ! check_toolchain "${swift_tools[@]}" || ! check_swift_sdks || ! check_swift_simulator; then
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
  launchctl managername not Aqua: the worker user needs a GUI login of its own (sc-26789; docs/acceptance.md, S3),
  and the script itself must run from the owner's console login (a Terminal at the console, not over ssh)
  xcrun simctl list without an available iOS runtime or an iPhone device type: sudo xcodebuild -downloadPlatform iOS
Then re-run this script with --swift (or set Worker:Swift=false if the factory works no Swift repository).
EOF
    exit 1
fi

slots_flag=
[ "$slots" -eq 1 ] || slots_flag=" --slots $slots"
use_slot 1
cat <<EOF

Workers hold only the router key (Worker:Auth=router-key, the only mode): enroll your plans on the
router instead (simple-router login claude, simple-router login codex). If a worker user still has a Claude login of its
own, remove it (that runs nothing else and signals no process):
    sudo $0$slots_flag --remove-worker-login
EOF
if [ "$slots" -gt 1 ]; then
    echo
    echo "Set Worker__Slots=$slots for the factory (a Worker:Slots larger than the slots set up here fails start-up:"
    echo "the higher slots have no launch helper)."
fi
