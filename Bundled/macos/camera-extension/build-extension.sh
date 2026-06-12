#!/bin/bash
# Builds the CMIO camera extension and copies it (renamed to its bundle id,
# the layout sysextd expects) into the target directory - normally
# Nexus.app/Contents/Library/SystemExtensions. Unsigned here; build-app.sh
# signs inside-out when a signing identity is configured.
set -euo pipefail

TARGET_DIR="${1:?usage: build-extension.sh <target-dir>}"
HERE="$(cd "$(dirname "$0")" && pwd)"
BUNDLE_ID="com.hellonexus.panel.service.camera-extension"

command -v xcodegen >/dev/null || { echo "xcodegen missing (brew install xcodegen)" >&2; exit 1; }

cd "$HERE"
xcodegen generate --quiet

DD="$(mktemp -d)"
trap 'rm -rf "$DD"' EXIT
# Build SIGNED: the Info.plist CMIOExtensionMachServiceName uses
# $(TeamIdentifierPrefix), which only expands when a signing team resolves.
# Unsigned builds ship the literal variable and sysextd rejects the bundle.
xcodebuild -project NexusCameraExtension.xcodeproj \
    -scheme NexusCameraExtension -configuration Release \
    -derivedDataPath "$DD" build \
    -allowProvisioningUpdates -quiet

PRODUCT="$DD/Build/Products/Release/$BUNDLE_ID.systemextension"
[ -d "$PRODUCT" ] || { echo "extension product missing at $PRODUCT" >&2; exit 1; }

mkdir -p "$TARGET_DIR"
rm -rf "$TARGET_DIR/$BUNDLE_ID.systemextension"
cp -R "$PRODUCT" "$TARGET_DIR/"
echo "Embedded: $TARGET_DIR/$BUNDLE_ID.systemextension"
