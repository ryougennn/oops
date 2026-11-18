#!/bin/sh
# Builds capturewl (window-capture test compositor) against wlroots 0.20. Uses the system headers if
# wlroots-devel is installed, else unpacks the Fedora wlroots-devel package locally (no root needed).
# The runtime library (wlroots 0.20) must be installed. Tests that need it skip when it is not built.
set -e
cd "$(dirname "$0")"
if pkg-config --exists wlroots-0.20; then
    WLR_CFLAGS=$(pkg-config --cflags wlroots-0.20)
    WLR_LIBS=$(pkg-config --libs wlroots-0.20)
else
    if [ ! -d .deps/usr/include/wlroots-0.20 ]; then
        mkdir -p .deps && (cd .deps && dnf download --arch x86_64 wlroots-devel >/dev/null && rpm2cpio wlroots-devel-*.rpm | cpio -idm 2>/dev/null)
    fi
    WLR_CFLAGS="-I.deps/usr/include/wlroots-0.20"
    WLR_LIBS=$(ls /usr/lib64/libwlroots-0.20.so* | head -1)
fi
wayland-scanner server-header "$(pkg-config --variable=pkgdatadir wayland-protocols)/stable/xdg-shell/xdg-shell.xml" xdg-shell-protocol.h
wayland-scanner server-header ../../../protocols/wlr-protocols/wlr-layer-shell-unstable-v1.xml wlr-layer-shell-unstable-v1-protocol.h
cc -O1 -DWLR_USE_UNSTABLE -I. $WLR_CFLAGS $(pkg-config --cflags pixman-1) capturewl.c -o capturewl $WLR_LIBS -lwayland-server -lxkbcommon
echo "built $(pwd)/capturewl"
