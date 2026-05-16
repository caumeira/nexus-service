#!/bin/bash
# Wrap the AOT-published macOS binary into a proper .app bundle.
# Usage: ./build-app.sh <staging-dir> <output-dir>
#   staging-dir: where `dotnet publish` output lives (contains Qos binary + wwwroot)
#   output-dir:  where Qos.app should be created

set -euo pipefail

STAGING="${1:-publish-mac/staging}"
OUT="${2:-publish-mac}"

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
APP="$OUT/Qos.app"

rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS"
mkdir -p "$APP/Contents/Resources"

# macOS ships AOT-only - the publish output is a single self-contained native
# binary, no managed Qos.dll / *.deps.json / *.runtimeconfig.json sidecars.
# Copy whatever the AOT publish produced at the staging root.
cp "$STAGING/Qos" "$APP/Contents/MacOS/"
[ -d "$STAGING/wwwroot" ] && cp -R "$STAGING/wwwroot" "$APP/Contents/MacOS/"
[ -d "$STAGING/openrgb" ] && cp -R "$STAGING/openrgb" "$APP/Contents/MacOS/"
[ -d "$STAGING/ffmpeg" ] && cp -R "$STAGING/ffmpeg" "$APP/Contents/MacOS/"
[ -f "$STAGING/status-icon.png" ] && cp "$STAGING/status-icon.png" "$APP/Contents/MacOS/"
[ -f "$STAGING/status-icon@2x.png" ] && cp "$STAGING/status-icon@2x.png" "$APP/Contents/MacOS/"

# Build and copy the Swift audio-helper sidecar so SCK system-audio loopback
# works for music-reactive lighting. Lives next to the main binary so TCC
# attributes Screen Recording permission to the Qos bundle.
bash "$SCRIPT_DIR/audio-helper/build-helper.sh" "$APP/Contents/MacOS"

# Build and copy the Swift overlay-helper sidecar - per-screen transparent
# borderless NSWindow + WKWebView for the floating desktop widgets. The
# service spawns this when the user pins a widget; killed when the last
# widget is unpinned.
bash "$SCRIPT_DIR/overlay-helper/build-helper.sh" "$APP/Contents/MacOS"

# Native dylibs (libe_sqlite3, libglfw, etc) are dlopen'd at runtime from
# AppContext.BaseDirectory. Publish drops them at the staging root next to
# the exe; mirror that placement inside the bundle. Single-level glob is
# deliberate - openrgb/*.dylib must stay alongside the openrgb binary, not
# get flattened up next to Qos.
shopt -s nullglob
for dylib in "$STAGING"/*.dylib; do
    cp "$dylib" "$APP/Contents/MacOS/"
done
shopt -u nullglob

# Copy Info.plist + .icns
cp "$SCRIPT_DIR/Info.plist" "$APP/Contents/"
[ -f "$SCRIPT_DIR/AppIcon.icns" ] && cp "$SCRIPT_DIR/AppIcon.icns" "$APP/Contents/Resources/"

# Ensure the binary is executable
chmod +x "$APP/Contents/MacOS/Qos"

# Remove extended attributes that would trigger Gatekeeper quarantine warnings
xattr -cr "$APP" 2>/dev/null || true

echo "Built: $APP"
