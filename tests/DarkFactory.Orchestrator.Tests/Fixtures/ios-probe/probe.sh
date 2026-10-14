#!/bin/bash
# sc-26126 (P5 S0): can this user boot an iOS simulator and run one XCTest unit test and one XCUITest UI test?
#
# Usage: probe.sh <work dir>   (run from anywhere; the fixture project is this script's own directory)
#
# Run by LiveWorkerSandboxTests as a sandbox user through the launch helper, in ONE helper run: the helper's exit
# kills every process of that user, CoreSimulatorService and the booted simulator included, so a simulator booted in
# one helper run never survives into the next.
#
# It signals no process: no step has a timer of its own. The one bound is the caller's — the live test's token, whose
# cancellation closes the helper's stdin, and the helper then kills every process of the sandbox user (that user's own,
# never another's). Every step announces itself (`probe: <step> started`) before it runs, on stdout and in
# <work dir>/progress, so a run cut short names the step it was in.
#
# It touches only this user's own things: a device it creates in this user's own device set (named df-probe-<pid>,
# deleted on exit; those of earlier runs that were cut short deleted at the start), DerivedData and results under
# <work dir>, and this user's home caches. It changes no system setting. Every step prints `probe: <step> started`, then
# `probe: <step> ok|FAILED (<seconds>s) [detail]`; the last line is `probe-result: unit=<pass|fail|not-run>
# ui=<pass|fail|not-run>`. On a failure the step's own output is printed (bounded) between `--- <step> output ---` and
# `--- end ---`, so the caller names what is missing from that step's output alone.
set -u

fixture=$(cd "$(dirname "$0")" && pwd)
work=${1:?usage: probe.sh <work dir>}
mkdir -p "$work" || exit 2
work=$(cd "$work" && pwd)
derived="$work/DerivedData"
logs="$work/logs"
progress="$work/progress"
mkdir -p "$logs" || exit 2
: >"$progress"

unit=not-run
ui=not-run
udid=

# say <line>: to stdout and to the progress file (which the caller reads when it had to cut the run short).
say() {
    printf '%s\n' "$*"
    printf '%s\n' "$*" >>"$progress"
}

# step <name> <cmd...>: runs it, its output to logs/<name>.log, and prints the step's lines.
step() {
    local name=$1
    shift
    local start=$SECONDS rc
    say "probe: $name started"
    "$@" >"$logs/$name.log" 2>&1
    rc=$?
    if [ $rc -eq 0 ]; then
        say "probe: $name ok ($((SECONDS - start))s)"
    else
        say "probe: $name FAILED ($((SECONDS - start))s) exit $rc"
        echo "--- $name output ---"
        tail -n 60 "$logs/$name.log"
        echo "--- end ---"
    fi
    return $rc
}

# fail <name> <detail>: a check of the probe's own that failed, its detail as the step's output block.
fail() {
    say "probe: $1 FAILED"
    echo "--- $1 output ---"
    echo "$2"
    echo "--- end ---"
}

cleanup() {
    if [ -n "$udid" ]; then
        step simctl-shutdown xcrun simctl shutdown "$udid"
        if step simctl-delete xcrun simctl delete "$udid"; then
            say "probe: deleted device $udid"
        else
            say "probe: could NOT delete device $udid"
        fi
    fi
    say "probe-result: unit=$unit ui=$ui"
}
trap cleanup EXIT

# The environment: who, which session, which Xcode. Diagnostics only (never a failure by themselves).
say "probe: user $(/usr/bin/id -un) uid $(/usr/bin/id -u) home $HOME tmpdir ${TMPDIR:-unset}"
say "probe: launchd manager $(/bin/launchctl managername 2>&1) (Aqua = a GUI login session; Background/System = none)"
say "probe: console user $(/usr/bin/stat -f %Su /dev/console 2>&1)"
say "probe: developer dir $(/usr/bin/xcode-select -p 2>&1)"
say "probe: DevToolsSecurity $(/usr/sbin/DevToolsSecurity -status 2>&1 | tr '\n' ' ')"
say "probe: groups $(/usr/bin/id -Gn 2>&1)"

step xcodebuild-version xcrun xcodebuild -version || exit 1
# Exit 0 = Xcode's first-launch packages are installed (system-wide; `sudo xcodebuild -runFirstLaunch` otherwise).
step first-launch xcrun xcodebuild -checkFirstLaunchStatus || exit 1
# The CoreSimulator service answering this user at all (it is a per-user launchd service).
step simctl-list xcrun simctl list runtimes || exit 1
step simctl-list-devicetypes xcrun simctl list devicetypes || exit 1
step simctl-list-devices xcrun simctl list devices || exit 1

# Devices of earlier probe runs in this user's own set (a run cut short is killed before its cleanup can delete them).
for stale in $(/usr/bin/awk '/^ *df-probe-[0-9]+ \(/ { if (match($0, /\([0-9A-F-]+\)/) && RLENGTH == 38) print substr($0, RSTART + 1, 36) }' "$logs/simctl-list-devices.log"); do
    step simctl-delete-stale xcrun simctl delete "$stale" && say "probe: deleted stale device $stale"
done

runtime=$(/usr/bin/awk '/^iOS / && !/unavailable/ { id = $NF } END { print id }' "$logs/simctl-list.log")
# A current iPhone the runtime supports (the list is not in release order, so by name).
devtype=
for model in "iPhone 17" "iPhone 16" "iPhone 16e" "iPhone 15"; do
    devtype=$(/usr/bin/awk -v m="$model (" 'index($0, m) == 1 { sub(/.*\(/, ""); sub(/\).*/, ""); print; exit }' "$logs/simctl-list-devicetypes.log")
    [ -z "$devtype" ] || break
done
if [ -z "$runtime" ] || [ -z "$devtype" ]; then
    fail no-runtime "no available iOS simulator runtime or iPhone device type (runtime '$runtime', type '$devtype')"
    exit 1
fi
say "probe: runtime $runtime device type $devtype"

name="df-probe-$$"
step simctl-create xcrun simctl create "$name" "$devtype" "$runtime" || exit 1
udid=$(tail -n 1 "$logs/simctl-create.log" | tr -d '[:space:]')
say "probe: device $name $udid"

# The device is in THIS user's own device set, in its own home (E4): the home is this uid's, the device's directory is
# under it, and CoreSimulator itself names that directory as the device's data path.
uid=$(/usr/bin/id -u)
home_owner=$(/usr/bin/stat -f %u "$HOME" 2>&1)
set_dir="$HOME/Library/Developer/CoreSimulator/Devices"
step simctl-list-json xcrun simctl list -j devices || exit 1
# JSON writes "/" as "\/".
data_path=$(/usr/bin/grep '"dataPath"' "$logs/simctl-list-json.log" | /usr/bin/grep -F "$udid" | /usr/bin/sed 's#\\/#/#g; s/^ *"dataPath" : "//; s/",*$//' | head -n 1)
if [ "$home_owner" != "$uid" ]; then
    fail device-set "the home $HOME is owned by uid $home_owner, not by this user ($uid)"
    exit 1
elif [ ! -d "$set_dir/$udid" ]; then
    fail device-set "the device $udid is not in this user's own device set: no directory $set_dir/$udid"
    exit 1
elif [ "$data_path" != "$set_dir/$udid/data" ]; then
    fail device-set "CoreSimulator names the device's data path '$data_path', not $set_dir/$udid/data"
    exit 1
fi
say "probe: device set $set_dir (home owned by uid $home_owner; data path $data_path)"

step simctl-boot xcrun simctl bootstatus "$udid" -b || exit 1

common=(-project "$fixture/ProbeApp.xcodeproj" -scheme ProbeApp -destination "id=$udid" -derivedDataPath "$derived"
    CODE_SIGN_IDENTITY=- CODE_SIGN_STYLE=Manual DEVELOPMENT_TEAM=)
step build-for-testing xcrun xcodebuild build-for-testing "${common[@]}" || exit 1

if step unit-test xcrun xcodebuild test-without-building "${common[@]}" -only-testing:ProbeAppTests \
    -resultBundlePath "$work/unit.xcresult"; then
    unit=pass
else
    unit=fail
fi
if step ui-test xcrun xcodebuild test-without-building "${common[@]}" -only-testing:ProbeAppUITests \
    -resultBundlePath "$work/ui.xcresult"; then
    ui=pass
else
    ui=fail
fi
[ "$unit" = pass ] && [ "$ui" = pass ]
