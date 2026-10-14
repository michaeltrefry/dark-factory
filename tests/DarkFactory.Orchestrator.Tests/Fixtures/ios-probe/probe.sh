#!/bin/bash
# sc-26126 (P5 S0): can this user boot an iOS simulator and run one XCTest unit test and one XCUITest UI test?
#
# Usage: probe.sh <work dir>   (run from anywhere; the fixture project is this script's own directory)
#
# Run by LiveSimulatorProbeTests as a sandbox user through the launch helper, in ONE helper run: the helper's exit
# kills every process of that user, CoreSimulatorService and the booted simulator included, so a simulator booted in
# one helper run never survives into the next. Also runnable by the owner (the same steps, the owner's own simulators).
#
# It touches only this user's own things: a device it creates in this user's own device set (named df-probe-<pid>,
# deleted on exit), DerivedData and results under <work dir>, and this user's home caches. It changes no system
# setting. Every step prints one line `probe: <step> ok|FAILED (<seconds>s) [detail]`; the last line is
# `probe-result: unit=<pass|fail|not-run> ui=<pass|fail|not-run>`. On a failure the step's own output is printed
# (bounded) between `--- <step> output ---` and `--- end ---`, so the caller can name what is missing.
set -u

fixture=$(cd "$(dirname "$0")" && pwd)
work=${1:?usage: probe.sh <work dir>}
mkdir -p "$work" || exit 2
work=$(cd "$work" && pwd)
derived="$work/DerivedData"
logs="$work/logs"
mkdir -p "$logs" || exit 2

unit=not-run
ui=not-run
udid=

# with_timeout <seconds> <cmd...>: macOS has no timeout(1); perl's alarm, signalling only the command it started.
with_timeout() {
    local seconds=$1
    shift
    /usr/bin/perl -e '
        my $t = shift; my $pid = fork; die "fork: $!" unless defined $pid;
        if ($pid == 0) { setpgrp(0, 0); exec @ARGV or die "exec $ARGV[0]: $!"; }
        $SIG{ALRM} = sub { kill "TERM", -$pid; sleep 5; kill "KILL", -$pid; print STDERR "probe: timed out after ${t}s\n"; exit 124; };
        alarm $t; waitpid($pid, 0); exit($? & 127 ? 128 + ($? & 127) : $? >> 8);
    ' "$seconds" "$@"
}

# step <name> <timeout seconds> <cmd...>: runs it, its output to logs/<name>.log, and prints the step's line.
step() {
    local name=$1 seconds=$2
    shift 2
    local start=$SECONDS rc
    with_timeout "$seconds" "$@" >"$logs/$name.log" 2>&1
    rc=$?
    if [ $rc -eq 0 ]; then
        echo "probe: $name ok ($((SECONDS - start))s)"
    else
        echo "probe: $name FAILED ($((SECONDS - start))s) exit $rc"
        echo "--- $name output ---"
        tail -n 60 "$logs/$name.log"
        echo "--- end ---"
    fi
    return $rc
}

cleanup() {
    if [ -n "$udid" ]; then
        xcrun simctl shutdown "$udid" >/dev/null 2>&1
        if xcrun simctl delete "$udid" >/dev/null 2>&1; then
            echo "probe: deleted device $udid"
        else
            echo "probe: could NOT delete device $udid"
        fi
    fi
    echo "probe-result: unit=$unit ui=$ui"
}
trap cleanup EXIT

# The environment: who, which session, which Xcode. Diagnostics only (never a failure by themselves).
echo "probe: user $(/usr/bin/id -un) uid $(/usr/bin/id -u) home $HOME tmpdir ${TMPDIR:-unset}"
echo "probe: launchd manager $(/bin/launchctl managername 2>&1) (Aqua = a GUI login session; Background/System = none)"
echo "probe: console user $(/usr/bin/stat -f %Su /dev/console 2>&1)"
echo "probe: developer dir $(/usr/bin/xcode-select -p 2>&1)"
echo "probe: DevToolsSecurity $(/usr/sbin/DevToolsSecurity -status 2>&1 | tr '\n' ' ')"
echo "probe: groups $(/usr/bin/id -Gn 2>&1)"

step xcodebuild-version 120 xcrun xcodebuild -version || exit 1
# Exit 0 = Xcode's first-launch packages are installed (system-wide; `sudo xcodebuild -runFirstLaunch` otherwise).
step first-launch 120 xcrun xcodebuild -checkFirstLaunchStatus || exit 1
# The CoreSimulator service answering this user at all (it is a per-user launchd service).
step simctl-list 180 xcrun simctl list runtimes || exit 1

runtime=$(xcrun simctl list runtimes 2>/dev/null | /usr/bin/awk '/^iOS / && !/unavailable/ { id = $NF } END { print id }')
# A current iPhone the runtime supports (the list is not in release order, so by name).
devtype=
types=$(xcrun simctl list devicetypes 2>/dev/null)
for model in "iPhone 17" "iPhone 16" "iPhone 16e" "iPhone 15"; do
    devtype=$(printf '%s\n' "$types" | /usr/bin/awk -v m="$model (" 'index($0, m) == 1 { sub(/.*\(/, ""); sub(/\).*/, ""); print; exit }')
    [ -z "$devtype" ] || break
done
if [ -z "$runtime" ] || [ -z "$devtype" ]; then
    echo "probe: no-runtime FAILED: no available iOS simulator runtime or iPhone device type (runtime '$runtime', type '$devtype')"
    exit 1
fi
echo "probe: runtime $runtime device type $devtype"

name="df-probe-$$"
step simctl-create 120 xcrun simctl create "$name" "$devtype" "$runtime" || exit 1
udid=$(tail -n 1 "$logs/simctl-create.log" | tr -d '[:space:]')
echo "probe: device $name $udid"
step simctl-boot 300 xcrun simctl bootstatus "$udid" -b || exit 1

common=(-project "$fixture/ProbeApp.xcodeproj" -scheme ProbeApp -destination "id=$udid" -derivedDataPath "$derived"
    CODE_SIGN_IDENTITY=- CODE_SIGN_STYLE=Manual DEVELOPMENT_TEAM=)
step build-for-testing 900 xcrun xcodebuild build-for-testing "${common[@]}" || exit 1

if step unit-test 600 xcrun xcodebuild test-without-building "${common[@]}" -only-testing:ProbeAppTests \
    -resultBundlePath "$work/unit.xcresult"; then
    unit=pass
else
    unit=fail
fi
if step ui-test 900 xcrun xcodebuild test-without-building "${common[@]}" -only-testing:ProbeAppUITests \
    -resultBundlePath "$work/ui.xcresult"; then
    ui=pass
else
    ui=fail
fi
[ "$unit" = pass ] && [ "$ui" = pass ]
