#!/bin/bash
# Compile the nexus-camera-helper sink feeder + nexus-camera-activator dylib.
# Usage: ./build.sh <output-dir>
#   output-dir: directory the resulting artifacts land in.
#
# Runs only on macOS. Linux / Windows builds skip silently.

set -euo pipefail

if [ "$(uname)" != "Darwin" ]; then
    echo "[camera-helper] non-macOS host, skipping"
    exit 0
fi

OUT="${1:-.}"
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"

if ! command -v swiftc >/dev/null 2>&1; then
    echo "[camera-helper] swiftc not found - install Xcode Command Line Tools" >&2
    exit 1
fi

mkdir -p "$OUT"

# Embed Info.plist so macOS attaches a stable bundle id to the helper, same
# convention as the audio/overlay helpers.
swiftc -O -target arm64-apple-macos13.0 \
    -framework CoreMediaIO \
    -framework CoreMedia \
    -framework CoreVideo \
    -framework VideoToolbox \
    -framework Foundation \
    -Xlinker -sectcreate \
    -Xlinker __TEXT \
    -Xlinker __info_plist \
    -Xlinker "$SCRIPT_DIR/Info.plist" \
    "$SCRIPT_DIR/main.swift" -o "$OUT/nexus-camera-helper"

# Re-sign ad-hoc so the embedded Info.plist is hashed into the code-signing
# directory.
codesign -s - --force "$OUT/nexus-camera-helper"

# The activator deliberately embeds NO Info.plist: it runs inside the Nexus
# process and Bundle.main must stay Nexus.app for sysextd to accept the
# activation request.
swiftc -O -emit-library -target arm64-apple-macos13.0 \
    -framework SystemExtensions \
    -framework Foundation \
    "$SCRIPT_DIR/activator.swift" -o "$OUT/nexus-camera-activator.dylib"

codesign -s - --force "$OUT/nexus-camera-activator.dylib"

# Strip extended attributes that would trigger Gatekeeper warnings.
xattr -cr "$OUT/nexus-camera-helper" "$OUT/nexus-camera-activator.dylib" 2>/dev/null || true

echo "[camera-helper] built: $OUT/nexus-camera-helper + $OUT/nexus-camera-activator.dylib"
