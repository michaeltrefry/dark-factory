#!/bin/bash
# setup-worker-user.sh — one-time, idempotent setup of the Dark Factory worker sandbox (E5).
#
#   sudo scripts/setup-worker-user.sh [--owner <user>] [--worker <user>] [--work-root <dir>]
#
# Creates a hidden role account (default _factory) whose workers:
#   - cannot traverse the owner's home (so no ~/.ssh, ~/.config/gh or login keychain),
#   - get dotnet, git and their own Claude Code install,
#   - run only through the root-owned launch helper, which the owner may start as the
#     worker user without a password (one sudoers.d rule, nothing else).
# The work root (clones + worktrees) is owned by the owner under a root-owned parent and is
# readable by the worker; the orchestrator grants write access per worktree.
# Re-running it changes nothing that is already in place.
set -euo pipefail

owner=${SUDO_USER:-}
worker=_factory
work_root=/opt/dark-factory/work
helper_dir=/usr/local/libexec/dark-factory
helper=$helper_dir/factory-worker-launch
sudoers=/private/etc/sudoers.d/dark-factory
script_dir=$(cd "$(dirname "$0")" && pwd)

while [ $# -gt 0 ]; do
    case "$1" in
        --owner) owner=$2; shift 2 ;;
        --worker) worker=$2; shift 2 ;;
        --work-root) work_root=$2; shift 2 ;;
        *) echo "usage: sudo $0 [--owner <user>] [--worker <user>] [--work-root <dir>]" >&2; exit 64 ;;
    esac
done

[ "$(uname -s)" = Darwin ] || { echo "macOS only." >&2; exit 1; }
[ "$(id -u)" -eq 0 ] || { echo "Run with sudo." >&2; exit 1; }
[[ $worker =~ ^_?[a-z][a-z0-9_-]*$ ]] || { echo "Invalid worker user name: $worker" >&2; exit 64; }
[ -n "$owner" ] && [ "$owner" != root ] || { echo "Pass --owner <your user> (or run via sudo from your account)." >&2; exit 64; }
id "$owner" >/dev/null 2>&1 || { echo "No such owner user: $owner" >&2; exit 1; }
owner_home=$(dscl . -read "/Users/$owner" NFSHomeDirectory | awk '{print $2}')
worker_home=/Users/$worker
case "$work_root/" in
    /*) ;;
    *) echo "--work-root must be absolute." >&2; exit 64 ;;
esac
case "$work_root/" in
    "$owner_home"/* | "$worker_home"/*)
        echo "--work-root must be outside both homes (the worker can't enter the owner's; it could swap paths in its own)." >&2
        exit 64 ;;
esac

say() { echo "==> $*"; }

# Removes then adds an ACL entry so re-runs don't stack duplicates. Never recursive: chmod -R
# applies ACLs to symlink targets, and this runs as root over trees the worker can write.
set_acl() { # set_acl <entry> <path>
    chmod -a "$1" "$2" 2>/dev/null || true
    chmod +a "$1" "$2"
}

# --- 1. Hidden role account with its own group (not staff) ---------------------------
free_id() { # lowest id in 400-499 unused as both a UID and a GID
    local used id
    used=$( (dscl . -list /Users UniqueID; dscl . -list /Groups PrimaryGroupID) | awk '{print $2}')
    for id in $(seq 499 -1 400); do
        if ! grep -qx "$id" <<<"$used"; then echo "$id"; return; fi
    done
    echo "No free id in 400-499" >&2; exit 1
}

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

if [ ! -d "$worker_home" ]; then
    say "Creating $worker_home"
    mkdir -p "$worker_home"
fi
chown "$worker:$worker" "$worker_home"
chmod 0700 "$worker_home"

# --- 2. Close the owner's home to the worker ------------------------------------------
# Denying traversal at the top covers ~/.ssh, ~/.config/gh, ~/Library/Keychains and the rest.
say "Denying $worker access to $owner_home"
set_acl "user:$worker deny list,search,read,add_file,add_subdirectory,delete_child,readattr,readextattr,readsecurity" "$owner_home"

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
set_acl "user:$worker allow list,search,read,readattr,readextattr,readsecurity,file_inherit,directory_inherit" "$work_root"

# --- 4. Launch helper (root-owned) and the one sudoers rule ---------------------------
say "Installing $helper"
install -d -o root -g wheel -m 0755 "$helper_dir"
# The helper kills every process of sandbox_user when a run ends, so it must name this worker.
helper_src=$(mktemp)
sed "s/^sandbox_user=.*/sandbox_user=$worker/" "$script_dir/factory-worker-launch" >"$helper_src"
grep -qx "sandbox_user=$worker" "$helper_src" || { echo "Could not set sandbox_user in the helper." >&2; exit 1; }
install -o root -g wheel -m 0755 "$helper_src" "$helper"
rm -f "$helper_src"

rule=$(mktemp)
cat >"$rule" <<EOF
# Dark Factory: $owner may run only the worker launch helper, only as $worker.
Defaults!$helper !use_pty, !log_output
$owner ALL=($worker) NOPASSWD: $helper
EOF
visudo -cqf "$rule"
install -o root -g wheel -m 0440 "$rule" "$sudoers"
rm -f "$rule"
grep -q '^#includedir /private/etc/sudoers.d' /etc/sudoers \
    || echo "warning: /etc/sudoers has no '#includedir /private/etc/sudoers.d'; the rule is inactive." >&2

# --- 5. Toolchain: dotnet and git are system-wide; Claude Code installs per user ------
# Commands run as the worker start in a directory it can enter (sudo keeps our cwd, which
# may be under the owner's home that step 2 closed to it).
cd /
as_worker() { sudo -u "$worker" env -i "HOME=$worker_home" PATH=/usr/bin:/bin:/usr/sbin:/sbin "$@"; }
if [ ! -x "$worker_home/.local/bin/claude" ]; then
    say "Installing Claude Code for $worker (native installer)"
    as_worker /bin/bash -c 'cd ~ && curl -fsSL https://claude.ai/install.sh | bash'
fi

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

say "Checking the toolchain as $worker through the helper"
check_toolchain "/usr/local/share/dotnet/dotnet --version" "git --version" "claude --version" || exit 1

say "Done. Worker user: $worker  Helper: $helper  Work root: $work_root"
cat <<EOF

Next, for Worker:Auth=claude-login (the default), log the worker's own Claude Code in once
(never copy your credentials):
    sudo -u $worker -H $worker_home/.local/bin/claude      # then /login, then /exit
For Worker:Auth=router-key nothing else is needed.
EOF
