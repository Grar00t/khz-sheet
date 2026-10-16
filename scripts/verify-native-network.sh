#!/usr/bin/env bash
# A deliberately narrow *native core* runtime network-syscall test.
# This is not a whole-app, Windows UI, WebView2 or telemetry attestation.
set -euo pipefail
if [ "$#" -ne 1 ] || [ ! -f "$1" ] || [ ! -x "$1" ]; then
    echo "Usage: $0 /path/to/khz_sovereign_scale_test" >&2
    exit 2
fi
if ! command -v strace >/dev/null 2>&1; then
    echo "UNVERIFIED: strace is unavailable; refusing to claim no sockets" >&2
    exit 3
fi
exe="$1"
trace_file=$(mktemp)
trap 'rm -f "$trace_file"' EXIT
# -qq suppresses attach/exit status text; trace=network covers socket,
# socketpair, connect, bind, accept, send*, recv* and other network syscalls.
strace -f -qq -e trace=network -o "$trace_file" -- "$exe"
if [ -s "$trace_file" ]; then
    echo "FAIL: network-category syscalls observed in native acceptance test:" >&2
    cat "$trace_file" >&2
    exit 1
fi
echo "PASS: 0 network-category syscalls observed in THIS native test process"
echo "OUT_OF_SCOPE: WPF, WebView2, Tauri, plugins, OS/background and ERP APIs"
sha256sum "$exe"
