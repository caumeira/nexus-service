#!/usr/bin/env bash
# AOT serialization smoke test.
#
# WebApplicationFactory integration tests run the JIT build, so they cannot
# catch the one failure mode unique to the shipped product: a DTO that the
# source-generated JSON context does not cover serializes to "{}" once Native
# AOT trims the reflection fallback. This script publishes the real AOT binary,
# starts it, hits a handful of data endpoints, and fails if any response body is
# empty ("{}") - which is the signature of a missing [JsonSerializable] entry.
#
# Intended for CI (a clean runner). It boots the real service, so it has the
# normal first-run side effects (protocol-handler registration, orphan cleanup);
# do not run it casually on a working desktop.
#
# Usage:  tests/aot-smoke.sh [rid]      # rid defaults to the host (osx-arm64 / linux-x64)
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
RID="${1:-$(dotnet --info | awk -F'[:]' '/RID:/{gsub(/ /,"",$2);print $2;exit}')}"
PORT=9477
OUT="$(mktemp -d)"
URL="http://127.0.0.1:${PORT}"

echo "[aot-smoke] publishing AOT for ${RID} → ${OUT}"
dotnet publish "${ROOT}/Nexus.Service.csproj" -c Release -r "${RID}" -o "${OUT}" >/dev/null

BIN="${OUT}/Nexus"
[ -x "${BIN}" ] || { echo "[aot-smoke] published binary not found at ${BIN}"; exit 1; }

echo "[aot-smoke] launching ${BIN} on ${PORT}"
"${BIN}" "${URL}" >/tmp/aot-smoke-service.log 2>&1 &
SVC=$!
trap 'kill "${SVC}" 2>/dev/null || true; rm -rf "${OUT}"' EXIT

# Wait for /ping.
for _ in $(seq 1 30); do
  if curl -fsS "${URL}/ping" >/dev/null 2>&1; then break; fi
  sleep 1
done

# Obtain the loopback token, then probe data endpoints. Any "{}" body means an
# AOT-trimmed / unregistered DTO.
TOKEN="$(curl -fsS "${URL}/pair" | sed -n 's/.*"token":"\([^"]*\)".*/\1/p')"
[ -n "${TOKEN}" ] || { echo "[aot-smoke] could not obtain token"; exit 1; }

FAIL=0
for ep in /system/specs /cooling/status /lighting/global-brightness /devices /widgets-api/installed; do
  body="$(curl -fsS -H "Authorization: Bearer ${TOKEN}" "${URL}${ep}" || echo '__ERR__')"
  if [ "${body}" = '__ERR__' ]; then echo "[aot-smoke] FAIL ${ep}: request error"; FAIL=1; continue; fi
  if [ "${body}" = '{}' ] || [ -z "${body}" ]; then
    echo "[aot-smoke] FAIL ${ep}: empty body '${body}' (likely an unregistered DTO under AOT trimming)"; FAIL=1
  else
    echo "[aot-smoke] ok   ${ep}"
  fi
done

exit "${FAIL}"
