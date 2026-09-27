#!/usr/bin/env bash
# Seal a locally built Nexus.app with a stable identity (an Apple Development
# certificate is enough). macOS privacy grants - Input Monitoring for touch
# routing, Screen Recording, Microphone - are keyed on the bundle's designated
# requirement: an ad-hoc signature changes it on every build, and a bundle
# whose resources are not sealed is identified by executable path, which the
# Privacy & Security pane cannot add. Run it last, once wwwroot is in place;
# any later change to the bundle breaks the seal. Expects build-app.sh's
# layout: only Mach-O code under Contents/MacOS, data in Contents/Resources.
#
# Usage: sign-local.sh <Nexus.app> <identity>
# Env:   NEXUS_MAC_APP_ENTITLEMENTS  entitlements for the main executable, as
#        build-app.sh takes them (a re-sign otherwise drops them)
set -euo pipefail

APP="${1:?usage: sign-local.sh <Nexus.app> <identity>}"
ID="${2:?usage: sign-local.sh <Nexus.app> <identity>}"

xattr -cr "$APP" 2>/dev/null || true
# Nested code first, then the bundle. The camera extension keeps the signature
# xcodebuild gave it: a re-sign ships its entitlements unexpanded.
while IFS= read -r f; do
    [ "$f" = "$APP/Contents/MacOS/Nexus" ] && continue
    case "$f" in "$APP"/Contents/Library/SystemExtensions/*) continue ;; esac
    if file -b "$f" | grep -q "Mach-O"; then
        codesign --force -s "$ID" "$f"
    fi
done < <(find "$APP" -type f ! -type l)
if [ -n "${NEXUS_MAC_APP_ENTITLEMENTS:-}" ]; then
    codesign --force --identifier com.hellonexus.panel.service --entitlements "$NEXUS_MAC_APP_ENTITLEMENTS" -s "$ID" "$APP"
else
    codesign --force --identifier com.hellonexus.panel.service -s "$ID" "$APP"
fi
codesign --verify --deep --strict "$APP"
echo "Sealed: $APP ($ID)"
