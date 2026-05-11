#!/bin/bash
# Wrap the AOT-published macOS binary into a proper .app bundle.
# Usage: ./build-app.sh <staging-dir> <output-dir>
#   staging-dir: where `dotnet publish` output lives (contains qOS binary + wwwroot)
#   output-dir:  where qOS.app should be created

set -euo pipefail

STAGING="${1:-publish-mac/staging}"
OUT="${2:-publish-mac}"

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
APP="$OUT/qOS.app"

rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS"
mkdir -p "$APP/Contents/Resources"

# Copy binary + managed assemblies + wwwroot + status icons next to the
# executable (AppContext.BaseDirectory). Self-contained non-AOT publish
# produces qOS (native bootstrap) plus qOS.dll/.deps.json/.runtimeconfig.json
# alongside it; all four are required at runtime.
cp "$STAGING/qOS" "$APP/Contents/MacOS/"
cp "$STAGING/qOS.dll" "$APP/Contents/MacOS/"
cp "$STAGING/qOS.deps.json" "$APP/Contents/MacOS/"
cp "$STAGING/qOS.runtimeconfig.json" "$APP/Contents/MacOS/"
# Copy all other managed assemblies too (LibreHardware*, etc.)
for f in "$STAGING"/*.dll "$STAGING"/*.pdb; do
    [ -f "$f" ] && cp "$f" "$APP/Contents/MacOS/"
done
[ -d "$STAGING/wwwroot" ] && cp -R "$STAGING/wwwroot" "$APP/Contents/MacOS/"
[ -d "$STAGING/openrgb" ] && cp -R "$STAGING/openrgb" "$APP/Contents/MacOS/"
[ -d "$STAGING/ffmpeg" ] && cp -R "$STAGING/ffmpeg" "$APP/Contents/MacOS/"
[ -f "$STAGING/status-icon.png" ] && cp "$STAGING/status-icon.png" "$APP/Contents/MacOS/"
[ -f "$STAGING/status-icon@2x.png" ] && cp "$STAGING/status-icon@2x.png" "$APP/Contents/MacOS/"

# Build and copy the Swift audio-helper sidecar so SCK system-audio loopback
# works for music-reactive lighting. Lives next to the main binary so TCC
# attributes Screen Recording permission to the qOS bundle.
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
# get flattened up next to qOS.
shopt -s nullglob
for dylib in "$STAGING"/*.dylib; do
    cp "$dylib" "$APP/Contents/MacOS/"
done
shopt -u nullglob

# Copy Info.plist + .icns
cp "$SCRIPT_DIR/Info.plist" "$APP/Contents/"
[ -f "$SCRIPT_DIR/AppIcon.icns" ] && cp "$SCRIPT_DIR/AppIcon.icns" "$APP/Contents/Resources/"

# Ensure the binary is executable
chmod +x "$APP/Contents/MacOS/qOS"

# Remove extended attributes that would trigger Gatekeeper quarantine warnings
xattr -cr "$APP" 2>/dev/null || true

echo "Built: $APP"
