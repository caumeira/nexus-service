#!/bin/bash
# Compile the qos-overlay-helper Swift sidecar.
# Usage: ./build-helper.sh <output-dir>
#   output-dir: directory the resulting `qos-overlay-helper` binary lands in.
#
# Runs only on macOS. Linux / Windows builds skip silently.

set -euo pipefail

if [ "$(uname)" != "Darwin" ]; then
    echo "[overlay-helper] non-macOS host, skipping"
    exit 0
fi

OUT="${1:-.}"
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
SRC="$SCRIPT_DIR/main.swift"

if ! command -v swiftc >/dev/null 2>&1; then
    echo "[overlay-helper] swiftc not found - install Xcode Command Line Tools" >&2
    exit 1
fi

mkdir -p "$OUT"

# Embed Info.plist so macOS attaches a stable bundle id (overlay TCC grants
# need a stable identity to persist across rebuilds, same reason as the
# audio helper).
swiftc -O -target arm64-apple-macos13.0 \
    -framework AppKit \
    -framework WebKit \
    -framework Foundation \
    -Xlinker -sectcreate \
    -Xlinker __TEXT \
    -Xlinker __info_plist \
    -Xlinker "$SCRIPT_DIR/Info.plist" \
    "$SRC" -o "$OUT/qos-overlay-helper"

# Re-sign ad-hoc so the embedded Info.plist is hashed into the code-signing
# directory.
codesign -s - --force "$OUT/qos-overlay-helper"

# Strip extended attributes that would trigger Gatekeeper warnings.
xattr -cr "$OUT/qos-overlay-helper" 2>/dev/null || true

echo "[overlay-helper] built: $OUT/qos-overlay-helper"
