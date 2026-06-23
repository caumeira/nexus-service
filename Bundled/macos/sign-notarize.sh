#!/usr/bin/env bash
# Deep-sign (Developer ID + hardened runtime), package, notarize, and staple a
# macOS Nexus.app into a distributable Nexus.dmg. Expects build-app.sh to have
# already relocated data out of Contents/MacOS (only Mach-O code is signable
# there). The signing identity must already be in a reachable keychain.
#
# Usage: sign-notarize.sh <Nexus.app> <output.dmg>
# Env:
#   MAC_SIGN_IDENTITY    codesign identity, e.g.
#                        "Developer ID Application: Integretti LLC (8ZFCKY2SQ9)"
#   MAC_NOTARY_APPLE_ID  Apple ID for notarytool
#   MAC_NOTARY_PASSWORD  app-specific password for that Apple ID
#   MAC_TEAM_ID          Developer Team ID
set -euo pipefail

APP="${1:?usage: sign-notarize.sh <Nexus.app> <output.dmg>}"
DMG="${2:?usage: sign-notarize.sh <Nexus.app> <output.dmg>}"
ID="${MAC_SIGN_IDENTITY:?MAC_SIGN_IDENTITY required}"

echo "==> deep-signing $APP"
xattr -cr "$APP" 2>/dev/null || true
# Sign every nested Mach-O first (anywhere in the bundle, real files not
# symlinks), then sign the bundle, which signs the main executable and seals.
while IFS= read -r f; do
    [ "$f" = "$APP/Contents/MacOS/Nexus" ] && continue
    if file -b "$f" | grep -q "Mach-O"; then
        codesign --force --options runtime --timestamp -s "$ID" "$f"
    fi
done < <(find "$APP" -type f ! -type l)
codesign --force --options runtime --timestamp -s "$ID" "$APP"
codesign --verify --deep --strict --verbose=2 "$APP"

echo "==> packaging $DMG"
STAGE="$(mktemp -d)"
ditto "$APP" "$STAGE/$(basename "$APP")"
ln -s /Applications "$STAGE/Applications"
rm -f "$DMG"
hdiutil create -volname "Nexus" -srcfolder "$STAGE" -ov -format UDZO "$DMG" >/dev/null
rm -rf "$STAGE"
codesign --force --timestamp -s "$ID" "$DMG"

echo "==> notarizing (this contacts Apple and waits)"
xcrun notarytool submit "$DMG" \
    --apple-id "${MAC_NOTARY_APPLE_ID:?}" \
    --password "${MAC_NOTARY_PASSWORD:?}" \
    --team-id "${MAC_TEAM_ID:?}" \
    --wait

echo "==> stapling"
xcrun stapler staple "$DMG"
spctl -a -t open --context context:primary-signature -vvv "$DMG"
echo "Notarized + stapled: $DMG"
