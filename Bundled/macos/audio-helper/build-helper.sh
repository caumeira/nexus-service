#!/bin/bash
# Compile the nexus-audio-helper Swift sidecar.
# Usage: ./build-helper.sh <output-dir>
#   output-dir: directory the resulting `nexus-audio-helper` binary lands in.
#
# Runs only on macOS. Linux / Windows builds skip silently.

set -euo pipefail

if [ "$(uname)" != "Darwin" ]; then
    echo "[audio-helper] non-macOS host, skipping"
    exit 0
fi

OUT="${1:-.}"
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
SRC="$SCRIPT_DIR/main.swift"

if ! command -v swiftc >/dev/null 2>&1; then
    echo "[audio-helper] swiftc not found - install Xcode Command Line Tools" >&2
    exit 1
fi

mkdir -p "$OUT"

# Embed Info.plist into the binary so macOS attaches a stable bundle id to
# the helper. Without it the linker assigns the binary a random ad-hoc
# identifier each link, TCC can't track grants across rebuilds, and SCK
# silently delivers empty buffers when the helper's identity drifts.
swiftc -O -target arm64-apple-macos13.0 \
    -framework ScreenCaptureKit \
    -framework CoreMedia \
    -framework CoreGraphics \
    -framework AVFoundation \
    -framework Foundation \
    -Xlinker -sectcreate \
    -Xlinker __TEXT \
    -Xlinker __info_plist \
    -Xlinker "$SCRIPT_DIR/Info.plist" \
    "$SRC" -o "$OUT/nexus-audio-helper"

# Re-sign ad-hoc so the embedded Info.plist is hashed into the code-signing
# directory. Without re-signing, codesign sees a binary whose pages don't
# match the linker-emitted signature and refuses to load it.
codesign -s - --force "$OUT/nexus-audio-helper"

# Strip extended attributes that would trigger Gatekeeper warnings.
xattr -cr "$OUT/nexus-audio-helper" 2>/dev/null || true

echo "[audio-helper] built: $OUT/nexus-audio-helper"
