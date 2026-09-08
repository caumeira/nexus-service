#!/usr/bin/env bash
# Ensure a Bundled/<rid>/ffmpeg/ directory exists with our minimal ffmpeg binary
# for the requested target. If the output is already there, nothing happens;
# otherwise, run the custom build (scripts/build-ffmpeg-minimal.sh) and copy
# the artifact into Bundled/.
#
# Usage:
#   bash scripts/fetch-ffmpeg.sh <mac|win|linux|all>
#
# Targets map 1:1 to scripts/build-ffmpeg-minimal.sh. "all" builds every target
# supported on the current host (mac always, win via brew mingw-w64 if present,
# linux native only on Linux).

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
BUILD_SCRIPT="${REPO_ROOT}/scripts/build-ffmpeg-minimal.sh"

case "$(uname -s)-$(uname -m)" in
  Darwin-arm64) HOST_RID="osx-arm64" ;;
  Darwin-*)     HOST_RID="osx-x64" ;;
  Linux-*)      HOST_RID="linux-x64" ;;
  *)            HOST_RID="" ;;
esac

fetch_one() {
  local target="$1"
  local rid bin
  case "$target" in
    mac)
      if [[ "$(uname -m)" == "arm64" ]]; then rid="osx-arm64"; else rid="osx-x64"; fi
      bin="ffmpeg"
      ;;
    win)   rid="win-x64"; bin="ffmpeg.exe" ;;
    linux) rid="linux-x64"; bin="ffmpeg" ;;
    *) echo "unknown target: $target" >&2; return 2 ;;
  esac

  local dest="${REPO_ROOT}/Bundled/${rid}/ffmpeg"
  # A bundled binary from before an encoder was added to the configure line
  # produces a service that builds, imports, and silently drops the feature -
  # transparent backgrounds flatten. Re-check the capability, not just the path.
  # Only the host's own binary is runnable, so a cross-built one is taken on trust.
  if [[ -f "${dest}/${bin}" ]]; then
    if [[ "${rid}" == "${HOST_RID}" ]] && ! "${dest}/${bin}" -hide_banner -encoders 2>/dev/null | grep -qE '^ [A-Z.]+ png' ; then
      echo "[fetch-ffmpeg] ${rid}: bundled binary predates the png/gif encoders; rebuilding"
    else
      echo "[fetch-ffmpeg] ${rid}: already bundled at ${dest}/${bin}"
      return 0
    fi
  fi

  echo "[fetch-ffmpeg] ${rid}: building from source..."
  bash "${BUILD_SCRIPT}" "${target}"

  mkdir -p "${dest}"
  cp "${REPO_ROOT}/build/ffmpeg/out/${rid}/${bin}" "${dest}/${bin}"
  [[ -f "${REPO_ROOT}/build/ffmpeg/out/${rid}/LICENSE.txt" ]] && \
    cp "${REPO_ROOT}/build/ffmpeg/out/${rid}/LICENSE.txt" "${dest}/LICENSE.txt"

  local bytes size_mb
  bytes=$(wc -c < "${dest}/${bin}")
  size_mb=$(awk "BEGIN {printf \"%.1f\", ${bytes}/1024/1024}")
  echo "[fetch-ffmpeg] ${rid}: done (${size_mb} MB -> ${dest}/${bin})"
}

TARGET="${1:-}"
if [[ -z "$TARGET" ]]; then
  echo "usage: $0 <mac|win|linux|all>" >&2
  exit 2
fi

if [[ "$TARGET" == "all" ]]; then
  fetch_one mac
  if command -v x86_64-w64-mingw32-gcc >/dev/null 2>&1; then
    fetch_one win
  else
    echo "[fetch-ffmpeg] skipping win: mingw-w64 not found (brew install mingw-w64 to enable)"
  fi
  if [[ "$(uname -s)" == "Linux" ]]; then
    fetch_one linux
  else
    echo "[fetch-ffmpeg] skipping linux: not running on Linux host"
  fi
else
  fetch_one "$TARGET"
fi
