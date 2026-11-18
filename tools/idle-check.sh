#!/bin/bash
# Usage: tools/idle-check.sh <binary> [seconds] [args...]
# Starts <binary> in a fresh headless sway, lets it settle for 2 s, then counts the event loop's
# epoll_wait returns (= wakeups) over [seconds] (default 10). An idle shell should report 0.
set -e
BIN=$1; SECS=${2:-10}; shift 2 || shift $#
RT=$(mktemp -d /tmp/oopsidle.XXXX)
trap 'swaymsg -s "$SWAYSOCK" exit >/dev/null 2>&1 || true; rm -rf "$RT"' EXIT
env -i HOME="$HOME" PATH="$PATH" XDG_RUNTIME_DIR="$RT" WLR_BACKENDS=headless WLR_LIBINPUT_NO_DEVICES=1 WLR_RENDERER=pixman \
    sway -c "$(dirname "$0")/../tests/fixtures/sway.conf" >"$RT/sway.log" 2>&1 &
for i in $(seq 100); do ls "$RT"/sway-ipc.* >/dev/null 2>&1 && break; sleep 0.1; done
export XDG_RUNTIME_DIR=$RT WAYLAND_DISPLAY=$(ls "$RT" | grep -m1 '^wayland-[0-9]*$') SWAYSOCK=$(ls "$RT"/sway-ipc.* | head -1)
"$BIN" "$@" & PID=$!
sleep 2
timeout -s INT "$SECS" strace -f -p "$PID" -e trace=epoll_wait,epoll_pwait,epoll_pwait2 -o "$RT/trace" || true
kill -TERM $PID 2>/dev/null; wait $PID 2>/dev/null || true
WAKEUPS=$(grep -cE "epoll_p?wait2?\(.*= [0-9]+$" "$RT/trace" || true)
echo "$(basename "$BIN"): $WAKEUPS epoll wakeups in ${SECS}s"
