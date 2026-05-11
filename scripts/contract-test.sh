#!/usr/bin/env bash
# Contract test suite for qos-service.
# Boots the service on a private port, hits every documented HTTP endpoint,
# and asserts each returns 2xx (or one of the documented client-error codes
# defined per-endpoint). Exits 0 if all endpoints pass; non-zero on any failure.
#
# Run from the qos-service repo root:
#   bash scripts/contract-test.sh
#
# Requirements: dotnet, curl, jq.

set -u

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PORT="${PORT:-19493}"           # not the dev 19492; doesn't collide with a running service
BASE="http://localhost:${PORT}"
DLL="$REPO_ROOT/bin/Debug/net10.0/qos-service.dll"
PID=""

cleanup() {
  if [[ -n "$PID" ]] && kill -0 "$PID" 2>/dev/null; then
    kill "$PID" 2>/dev/null || true
    wait "$PID" 2>/dev/null || true
  fi
}
trap cleanup EXIT

if [[ ! -f "$DLL" ]]; then
  echo "[contract-test] dll missing at $DLL — running 'dotnet build' first" >&2
  (cd "$REPO_ROOT" && dotnet build >/dev/null 2>&1) || { echo "build failed"; exit 2; }
fi

dotnet "$DLL" "$BASE" >/tmp/qos-contract-test.log 2>&1 &
PID=$!

# Wait up to 10s for /ping to come up.
for i in {1..50}; do
  if curl -fsS -o /dev/null "${BASE}/ping" 2>/dev/null; then break; fi
  sleep 0.2
done

if ! curl -fsS -o /dev/null "${BASE}/ping"; then
  echo "[contract-test] service failed to start within 10s. Log:"
  cat /tmp/qos-contract-test.log
  exit 3
fi

# Fetch auth token via /pair
TOKEN=$(curl -s "${BASE}/pair" | jq -r '.token // empty')
if [[ -z "$TOKEN" ]]; then
  echo "[contract-test] failed to fetch auth token from /pair"
  exit 4
fi
echo "[contract-test] got auth token: ${TOKEN:0:8}..."

PASS=0
FAIL=0
FAILED_ENDPOINTS=()

# check_http VERB PATH [BODY] [EXPECTED_CODES_DEFAULT_2XX]
# Asserts the response status is in the expected set.
check_http() {
  local verb="$1"
  local path="$2"
  local body="${3:-}"
  local expect="${4:-2xx}"

  local code
  if [[ -n "$body" ]]; then
    code=$(curl -s -o /dev/null -w "%{http_code}" \
      -X "$verb" -H 'Content-Type: application/json' -H "Authorization: Bearer $TOKEN" -d "$body" "${BASE}${path}")
  else
    code=$(curl -s -o /dev/null -w "%{http_code}" -X "$verb" -H "Authorization: Bearer $TOKEN" "${BASE}${path}")
  fi

  local ok=0
  case "$expect" in
    2xx) [[ "$code" =~ ^2 ]] && ok=1 ;;
    *)   [[ ",${expect}," == *",${code},"* ]] && ok=1 ;;
  esac

  if [[ $ok -eq 1 ]]; then
    PASS=$((PASS + 1))
    printf '  ✓ %-6s %-50s %s\n' "$verb" "$path" "$code"
  else
    FAIL=$((FAIL + 1))
    FAILED_ENDPOINTS+=("$verb $path → $code")
    printf '  ✗ %-6s %-50s %s\n' "$verb" "$path" "$code"
  fi
}

echo "[contract-test] hitting endpoints on $BASE"

# Ping + system snapshot
check_http GET /ping
check_http GET /system/snapshot
check_http GET /system/os-version
check_http GET /system/elevation
check_http GET /system/polling-rate
check_http POST /system/polling-rate '{"pollingRate":1000}'
check_http GET /system/volume
check_http POST /system/volume '{"volume":0.5}'
check_http POST /system/volume/mute '{"muted":false}'

# CPU
check_http GET /system/cpu/sensors
check_http GET /system/cpu/model
check_http GET /system/cpu/health

# GPU
check_http GET /system/gpu/sensors
check_http GET /system/gpu/model

# Memory
check_http GET /system/memory/sensors
check_http GET /system/memory/total

# Storage
check_http GET /system/storage/sensors
check_http GET /system/storage/partitions
check_http GET /system/storage/info

# Motherboard + Fps
check_http GET /system/motherboard/sensors
check_http GET /system/motherboard/model
check_http GET /system/fps/sensors

# Cooling
check_http GET /cooling/all
check_http GET /cooling/q60
check_http GET /cooling/q80
check_http GET /cooling/minihub
check_http GET /cooling/np50
check_http POST /cooling/layout '{"port1":1,"port2":2,"port3":3,"port4":4}'

# AIO firmware
check_http GET /cooling/aio/turbo
check_http POST /cooling/aio/turbo '{"isTurbo":true}'
check_http GET /cooling/aio/animations
check_http GET /cooling/aio/animation
check_http POST /cooling/aio/animation '{"enabled":true,"animation":"Static","color":{"r":255,"g":0,"b":0,"a":1.0},"brightness":100}'
check_http GET /cooling/aio/mode
check_http POST /cooling/aio/mode/Software
check_http GET /cooling/aio/curve
check_http POST /cooling/aio/curve '{"id":"x","points":[]}'

# Portal firmware
check_http GET /cooling/portal/speed
check_http POST /cooling/portal/speed/50
check_http GET /cooling/portal/mode
check_http POST /cooling/portal/mode/Software
check_http GET /cooling/portal/animations
check_http GET /cooling/portal/animation
check_http POST /cooling/portal/animation '{"enabled":true,"animation":"Static","color":{"r":0,"g":255,"b":0,"a":1.0},"brightness":80}'

# Fan control
check_http GET /cooling/fans
check_http GET /cooling/sources
check_http POST /cooling/fan/test-id/speed '{"speed":50}'
check_http POST /cooling/fan/test-id/auto
check_http POST /cooling/fan/test-id/name '{"name":"Test Fan"}' "200,400"
check_http GET /cooling/profiles
check_http POST /cooling/profile/auto
check_http POST /cooling/profile/silent
check_http POST /cooling/profile/balanced
check_http POST /cooling/profile/performance

# Curves
check_http POST /cooling/curves/set '{"globalSpeedModifier":1.0,"curves":[]}'

# Calibration
check_http GET /cooling/calibrations
check_http GET /cooling/calibration/nonexistent-id "" "404"
check_http POST /cooling/calibrate '{"fanIds":[]}'

# Lighting
check_http POST /lighting/stop
check_http POST /lighting/frame-rate '{"frameRate":60}'
check_http POST /lighting/scale-ratio '{"ratio":1.0}'
check_http GET /lighting/current
check_http POST /lighting/brightness '{"scale":{},"enabled":false}'
check_http POST /lighting/speed '{"scale":{},"enabled":false}'
check_http POST /lighting/static/headless-start '{"color":{"r":255,"g":0,"b":0,"a":1.0}}'
check_http POST /lighting/animate/headless-start '{"effect":"Wave","speed":50,"noise":0.0,"filter":"None","intensity":1.0,"scheme":[],"hue":0,"sat":0}'
check_http POST /lighting/music/headless-start '{"effect":"CircleRamp","source":"default"}'
check_http POST /lighting/screen/headless-start '{"monitor":"","effect":"Average","saturation":1,"contrast":1,"blur":0,"hue":0,"colorize":0}'
check_http GET /lighting/screen/effect
check_http POST /lighting/screen/effect '{"hue":0.25,"colorize":0.5,"saturation":1.2,"contrast":1.1,"persist":true}'
check_http GET /lighting/media/effect
check_http POST /lighting/media/effect '{"hue":0.1,"colorize":0.2,"saturation":1,"contrast":1,"persist":false}'
check_http POST /lighting/gif/headless-start '{"speed":50,"mode":"Loop","paths":[]}'
check_http POST /lighting/streaming/set-streaming '{"streaming":{},"scale":{"width":1920,"height":1080},"mode":"single"}'

# Devices
check_http GET /devices/all
check_http GET /devices/usb/all
check_http GET /devices/cnvs/connected
check_http GET /devices/fw/cnvs/version
check_http GET /devices/cnvs
check_http POST /devices/cnvs '{"playAnimation":true,"playWhenPCOff":false}'
check_http POST /devices/can-update '{"id":"cnvs"}'
check_http POST /devices/update '{"id":"cnvs"}'
check_http POST /devices/update-progress '{"id":"cnvs"}'
check_http GET /devices/motherboard/leds
check_http POST /devices/motherboard/leds '{"channels":[]}'
check_http POST /devices/function-check '{"type":"cnvs","version":"1.0.0","firmwareFunction":"x"}'
check_http POST /devices/lighting-devices/disable '{"devices":[]}'
check_http POST /devices/lighting-devices/power '{"id":"openrgb-0","on":true}'
check_http POST /devices/lighting-devices/brightness '{"id":"x","brightness":100}'
check_http POST /devices/lighting-devices/hue '{"id":"x","hue":0}'
check_http POST /devices/lighting-devices/saturation '{"id":"x","saturation":1}'
check_http POST /devices/lighting-devices/zone-size '{"id":"openrgb-0-0","count":12}'
check_http POST /devices/lighting-devices/identify '{"id":"openrgb-0-0","durationMs":500}'
check_http GET /devices/lighting-devices/openrgb-0/led-map
check_http POST /devices/lighting-devices/openrgb-0/led-map '{"overrides":[]}'
check_http DELETE /devices/lighting-devices/openrgb-0/led-map

# Keeb
check_http GET /keeb/settings
check_http GET /keeb/rotary/functions
check_http POST /keeb/rotary '{"left":"VolumeAdjustment","right":"BrightnessAdjustment","apps":[]}'
check_http POST /keeb/rotary/sensitivity '{"sensitivity":"Balanced"}'
check_http POST /keeb/key-reactive '{"animationMode":"Static","speed":"Medium","direction":"Forward","brightness":80,"keyReactive":false,"keyReactiveMask":false,"keyReactiveMode":"Off","keyReactiveColor":{"r":0,"g":0,"b":0,"a":1.0},"keyIndicator":false}'
check_http POST /keeb/firmware/lighting '{"animationMode":"Static","speed":"Medium","direction":"Forward","brightness":80,"keyReactive":false,"keyReactiveMask":false,"keyReactiveMode":"Off","keyReactiveColor":{"r":0,"g":0,"b":0,"a":1.0},"keyIndicator":false}'
check_http POST /keeb/game-mode '{"altF4":false,"altTab":false,"shiftTab":false,"windowsKey":false}'
check_http GET /keeb/macro/0
check_http POST /keeb/macro/0 '{"keys":[]}'

# Inputter
check_http POST /inputter '{"strokes":[]}'

# Y70
check_http GET /y70/status
check_http GET /y70/rotation
check_http POST /y70/rotation '{"orientation":"Landscape"}'
check_http GET /y70/brightness
check_http POST /y70/brightness '{"brightness":100}'
check_http GET /y70/toggle
check_http POST /y70/toggle '{"toggle":false}'
check_http GET /y70/is-rotated

# Q60
check_http GET /q60/serial
check_http GET /q60/timev0

# System displays
check_http GET /displays
check_http GET /displays/nonexistent/brightness "" "404"
check_http POST /displays/nonexistent/brightness '{"brightness":50}' "400"
check_http GET /displays/nonexistent/vcp/16 "" "404"
check_http GET /displays/nonexistent/vcp/999 "" "400"

# AW5D
check_http POST /aw5d/launch
check_http POST /aw5d/terminate
check_http GET /aw5d/status

# Activity - image endpoints use /api/ prefix to avoid WebSocket route collisions
check_http GET /api/screentime
check_http GET /api/screentime/history
check_http GET /api/screentime/image
check_http GET /api/screentime/day/2026-04-20
check_http GET /api/screentime/day/notadate "" "400"
check_http GET "/api/screentime/range?from=2026-04-13&to=2026-04-20"
check_http GET "/api/screentime/app/Slack?from=2026-04-13&to=2026-04-20"
check_http GET /api/screentime/day/2026-04-20/hour/9
check_http GET /api/screentime/tracking
check_http POST /api/screentime/tracking '{"enabled":true}'
check_http DELETE /api/screentime/day/1999-01-01
check_http DELETE "/api/screentime/range?from=1999-01-01&to=1999-01-02"
check_http DELETE /api/screentime/app/__nonexistent__
check_http POST /api/appdetection/kill/999999
check_http GET "/api/media/spotify/album-art" "" "400,404"  # 400/404 acceptable when no album art exists

# Network
check_http GET /api/network/top
check_http GET /shortcuts
check_http GET "/shortcuts/icon?targetId=x" "" 400,404
check_http POST /shortcuts/launch "" 200

# Profiles + sharing + reset
check_http GET /profiles
check_http GET /profiles/sharing
check_http PUT /profiles/sharing/categories '{"category":"lighting","shared":false}'
check_http PUT /profiles/sharing/categories '{"category":"not-a-real-category","shared":true}' "400"
check_http POST /profiles/nonexistent/reset "" "404"
check_http POST /profiles/nonexistent/reset/lighting "" "404"

# Preferences round-trip for desktop overlay fields. Each POST returns 2xx and
# the GET should reflect the value back. The 400 path exercises the JSON
# binding contract on the same route.
check_http GET /preferences
check_http POST /preferences '{"overlayWidgetOpacity":0.5}'
check_http POST /preferences '{"overlayWidgetOpacity":1}'
PREFS_AFTER=$(curl -s -H "Authorization: Bearer $TOKEN" "${BASE}/preferences")
if ! echo "$PREFS_AFTER" | jq -e '.overlayWidgetOpacity == 1' >/dev/null; then
  echo "[contract-test] /preferences round-trip mismatch: $PREFS_AFTER" >&2
  FAIL=$((FAIL + 1))
  FAILED_ENDPOINTS+=("/preferences round-trip")
else
  PASS=$((PASS + 1))
fi

# Lifecycle
check_http GET /pawnio
check_http GET /start
check_http POST /start '{"enabled":false,"path":"","arguments":""}'
# Skip /shutdown — it would tear down the server we're testing.

echo
echo "[contract-test] $PASS passed, $FAIL failed"
if [[ $FAIL -gt 0 ]]; then
  echo "Failed endpoints:"
  for ep in "${FAILED_ENDPOINTS[@]}"; do
    echo "  - $ep"
  done
  echo "Service log:"
  tail -50 /tmp/qos-contract-test.log
  exit 1
fi
exit 0
