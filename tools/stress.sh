#!/bin/bash
# Usage: tools/stress.sh <oops-stress binary> <scenario> [args...]
# Runs one oops-stress scenario in a fresh headless sway with its own session bus.
# WLR_RENDERER=gles2 (default) renders the compositor on the GPU; pixman forces software.
set -e
BIN=$(realpath "$1"); shift
RT=$(mktemp -d /tmp/oopsstress.XXXX)
trap 'swaymsg -s "$SWAYSOCK" exit >/dev/null 2>&1 || true; rm -rf "$RT"' EXIT
env -i HOME="$HOME" PATH="$PATH" XDG_RUNTIME_DIR="$RT" WLR_BACKENDS=headless WLR_LIBINPUT_NO_DEVICES=1 \
    WLR_RENDERER="${WLR_RENDERER:-gles2}" sway -c "$(dirname "$0")/../tests/fixtures/sway.conf" >"$RT/sway.log" 2>&1 &
for i in $(seq 100); do ls "$RT"/sway-ipc.* >/dev/null 2>&1 && break; sleep 0.1; done
export XDG_RUNTIME_DIR=$RT WAYLAND_DISPLAY=$(ls "$RT" | grep -m1 '^wayland-[0-9]*$') SWAYSOCK=$(ls "$RT"/sway-ipc.* | head -1)
${PREFIX:-} dbus-run-session -- "$BIN" "$@"
