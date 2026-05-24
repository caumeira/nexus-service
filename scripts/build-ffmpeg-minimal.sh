#!/usr/bin/env bash
# Builds a minimal ffmpeg binary sized for nexus-service's narrow use cases:
#   - decode images / gifs / common video containers (jpg, png, gif, mp4, webm, mov, avi, mkv, wmv, m4v, mpg)
#   - downscale + pad (libswscale via scale/pad filters)
#   - write raw rgb24 pipe/file and mjpeg thumbnails
#   - capture video: gdigrab (Win), avfoundation (macOS)
#   - capture audio: dshow (Win), avfoundation (macOS), pulse (Linux)
#
# Configure flags drop: streaming protocols, GPL encoders (x264/x265/aom/vpx-enc),
# subtitle codecs, hardware accel, hundreds of obscure decoders. Outcome is a
# ~10-15 MB statically-linked ffmpeg vs the ~96 MB gyan.dev essentials.
#
# Usage:
#   bash scripts/build-ffmpeg-minimal.sh <target>
#
# Targets:
#   mac    - build for the current macOS host (darwin-arm64 or darwin-x64)
#   win    - cross-compile for x86_64 Windows (needs mingw-w64 toolchain)
#   linux  - build for the current Linux host (glibc x86_64)
#
# Outputs the resulting binary to builds/ffmpeg/<rid>/ffmpeg[.exe] plus a
# LICENSE.txt for the LGPL attribution the installer needs to ship.

set -euo pipefail

FFMPEG_VERSION="${FFMPEG_VERSION:-7.1}"
FFMPEG_TARBALL_URL="https://ffmpeg.org/releases/ffmpeg-${FFMPEG_VERSION}.tar.xz"

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
BUILD_ROOT="${REPO_ROOT}/build/ffmpeg"
mkdir -p "${BUILD_ROOT}"

TARGET="${1:-}"
if [[ -z "$TARGET" ]]; then
  echo "usage: $0 <mac|win|linux>" >&2
  exit 2
fi

SRC_DIR="${BUILD_ROOT}/ffmpeg-${FFMPEG_VERSION}"
if [[ ! -d "${SRC_DIR}" ]]; then
  echo "[ffmpeg] fetching ffmpeg ${FFMPEG_VERSION} source..."
  cd "${BUILD_ROOT}"
  curl -fsSL -o "ffmpeg-${FFMPEG_VERSION}.tar.xz" "${FFMPEG_TARBALL_URL}"
  tar -xf "ffmpeg-${FFMPEG_VERSION}.tar.xz"
  rm "ffmpeg-${FFMPEG_VERSION}.tar.xz"
fi

# Shared configure flags: what we enable / disable for every target
COMMON_CONFIG=(
  # Licensing: LGPL-only, no GPL encumbrance
  --disable-gpl
  --disable-nonfree

  # Build shape
  --enable-static
  --disable-shared
  --enable-small
  --disable-debug
  --disable-doc
  --disable-network
  --disable-autodetect

  # Explicit deps that --disable-autodetect otherwise drops silently.
  # png/tiff decoders need zlib; matroska uses bzlib for a few compressed tracks.
  # (liblzma intentionally skipped - not in macOS SDK and only used for exotic
  # matroska compression that our media import path never exercises.)
  --enable-zlib
  --enable-bzlib

  # Start from nothing, opt-in everything
  --disable-everything

  # Core programs
  --disable-programs
  --enable-ffmpeg

  # Filters we literally call from MediaImporter.cs / ScreenMirrorEffect.cs
  --enable-filter=scale
  --enable-filter=pad
  --enable-filter=format
  --enable-filter=fps
  --enable-filter=aresample
  --enable-filter=setpts
  --enable-filter=asetpts
  --enable-filter=aformat
  --enable-filter=anull

  # Demuxers for every extension MediaImporter.cs accepts
  --enable-demuxer=image2
  --enable-demuxer=image_jpeg_pipe
  --enable-demuxer=image_png_pipe
  --enable-demuxer=image_webp_pipe
  --enable-demuxer=image_bmp_pipe
  --enable-demuxer=image_tiff_pipe
  --enable-demuxer=image_gif_pipe
  --enable-demuxer=gif
  --enable-demuxer=mov
  --enable-demuxer=matroska
  --enable-demuxer=avi
  --enable-demuxer=asf
  --enable-demuxer=mpegps
  --enable-demuxer=mpegvideo
  --enable-demuxer=wav
  --enable-demuxer=aac
  --enable-demuxer=mp3
  --enable-demuxer=flac

  # Decoders covering the common codecs inside those containers
  --enable-decoder=mjpeg
  --enable-decoder=png
  --enable-decoder=bmp
  --enable-decoder=webp
  --enable-decoder=tiff
  --enable-decoder=gif
  --enable-decoder=h264
  --enable-decoder=hevc
  --enable-decoder=vp8
  --enable-decoder=vp9
  --enable-decoder=mpeg4
  --enable-decoder=mpeg2video
  --enable-decoder=mpeg1video
  --enable-decoder=wmv1
  --enable-decoder=wmv2
  --enable-decoder=wmv3
  --enable-decoder=vc1
  --enable-decoder=prores
  --enable-decoder=aac
  --enable-decoder=mp3
  --enable-decoder=opus
  --enable-decoder=flac
  --enable-decoder=pcm_s16le
  --enable-decoder=pcm_f32le
  --enable-decoder=pcm_s24le

  # Encoders: rawvideo passthrough + mjpeg thumbnails + audio pipe
  --enable-encoder=rawvideo
  --enable-encoder=mjpeg
  --enable-encoder=pcm_s16le
  --enable-encoder=pcm_f32le

  # Muxers
  --enable-muxer=rawvideo
  --enable-muxer=image2
  --enable-muxer=wav
  --enable-muxer=null

  # Only file + pipe (no network)
  --enable-protocol=file
  --enable-protocol=pipe

  # Parsers speed up demuxing for codecs that need framing hints
  --enable-parser=h264
  --enable-parser=hevc
  --enable-parser=aac
  --enable-parser=mpeg4video
  --enable-parser=mpegvideo
  --enable-parser=mjpeg
  --enable-parser=vp8
  --enable-parser=vp9
  --enable-parser=gif
  --enable-parser=flac
)

# Per-target overrides
case "$TARGET" in
  mac)
    ARCH="$(uname -m)"
    if [[ "${ARCH}" == "arm64" ]]; then
      RID="osx-arm64"
      EXTRA_CONFIG=(--arch=arm64 --enable-cross-compile --target-os=darwin)
    else
      RID="osx-x64"
      EXTRA_CONFIG=(--arch=x86_64 --target-os=darwin)
    fi
    BIN_NAME="ffmpeg"
    # macOS devices: screen + audio capture via AVFoundation
    EXTRA_CONFIG+=(
      --enable-avdevice
      --enable-indev=avfoundation
    )
    ;;
  win)
    RID="win-x64"
    BIN_NAME="ffmpeg.exe"
    if ! command -v x86_64-w64-mingw32-gcc >/dev/null 2>&1; then
      echo "[ffmpeg] mingw-w64 toolchain missing. Install with: brew install mingw-w64" >&2
      exit 3
    fi

    # brew's mingw-w64 ships the compiler only, not zlib/bzip2 - build them
    # into the target sysroot so ffmpeg's png/tiff/matroska paths compile.
    # Cached between runs so this only pays the ~30s once.
    MINGW_ROOT="$(x86_64-w64-mingw32-gcc --print-sysroot)/x86_64-w64-mingw32"
    MINGW_DEPS_SENTINEL="${MINGW_ROOT}/.nexus-deps-built"
    if [[ ! -f "${MINGW_DEPS_SENTINEL}" ]]; then
      echo "[ffmpeg] building zlib + bzip2 into mingw sysroot (one-time)..."
      DEPS_WORK="${BUILD_ROOT}/mingw-deps"
      mkdir -p "${DEPS_WORK}"

      # zlib 1.3.1 (fetch from GitHub release mirror - zlib.net rotates URLs)
      if [[ ! -f "${MINGW_ROOT}/lib/libz.a" ]]; then
        cd "${DEPS_WORK}"
        [[ -d zlib-1.3.1 ]] || { curl -fsSL https://github.com/madler/zlib/releases/download/v1.3.1/zlib-1.3.1.tar.gz | tar -xz; }
        cd zlib-1.3.1
        make -f win32/Makefile.gcc PREFIX=x86_64-w64-mingw32- BINARY_PATH="${MINGW_ROOT}/bin" \
             INCLUDE_PATH="${MINGW_ROOT}/include" LIBRARY_PATH="${MINGW_ROOT}/lib" \
             SHARED_MODE=0 libz.a
        cp libz.a "${MINGW_ROOT}/lib/"
        cp zlib.h zconf.h "${MINGW_ROOT}/include/"
      fi

      # bzip2 1.0.8
      if [[ ! -f "${MINGW_ROOT}/lib/libbz2.a" ]]; then
        cd "${DEPS_WORK}"
        [[ -d bzip2-1.0.8 ]] || { curl -fsSL https://sourceware.org/pub/bzip2/bzip2-1.0.8.tar.gz | tar -xz; }
        cd bzip2-1.0.8
        make -j CC=x86_64-w64-mingw32-gcc AR=x86_64-w64-mingw32-ar RANLIB=x86_64-w64-mingw32-ranlib libbz2.a
        cp libbz2.a "${MINGW_ROOT}/lib/"
        cp bzlib.h "${MINGW_ROOT}/include/"
      fi

      touch "${MINGW_DEPS_SENTINEL}"
      cd "${SRC_DIR}"
    fi

    EXTRA_CONFIG=(
      --arch=x86_64
      --target-os=mingw64
      --cross-prefix=x86_64-w64-mingw32-
      --pkg-config=pkg-config
      --enable-avdevice
      --enable-indev=gdigrab
      --enable-indev=dshow
      # Cross-compile tells configure where to find the mingw sysroot headers/libs
      --extra-cflags="-I${MINGW_ROOT}/include"
      # -static pulls in libgcc_s_seh, libstdc++, libwinpthread so the binary
      # doesn't depend on mingw runtime DLLs on target machines.
      --extra-ldflags="-L${MINGW_ROOT}/lib -static"
    )
    ;;
  linux)
    RID="linux-x64"
    BIN_NAME="ffmpeg"
    EXTRA_CONFIG=(
      --arch=x86_64
      --target-os=linux
      --enable-avdevice
      --enable-indev=pulse
    )
    ;;
  *)
    echo "unknown target: $TARGET" >&2
    exit 2
    ;;
esac

OUT_DIR="${REPO_ROOT}/build/ffmpeg/out/${RID}"
mkdir -p "${OUT_DIR}"

echo "[ffmpeg] configuring for ${RID}..."
cd "${SRC_DIR}"
make distclean 2>/dev/null || true

./configure \
  "${COMMON_CONFIG[@]}" \
  "${EXTRA_CONFIG[@]}" \
  --prefix=/nexus-ffmpeg

echo "[ffmpeg] compiling..."
make -j"$(sysctl -n hw.ncpu 2>/dev/null || nproc)"

echo "[ffmpeg] stripping..."
case "$TARGET" in
  win)   x86_64-w64-mingw32-strip ffmpeg.exe || true ;;
  linux) strip ffmpeg ;;
  mac)   strip -S ffmpeg ;;
esac

cp "${BIN_NAME}" "${OUT_DIR}/${BIN_NAME}"
cp COPYING.LGPLv2.1 "${OUT_DIR}/LICENSE.txt" 2>/dev/null || true

SIZE_BYTES=$(wc -c < "${OUT_DIR}/${BIN_NAME}")
SIZE_MB=$(awk "BEGIN {printf \"%.1f\", ${SIZE_BYTES}/1024/1024}")
echo "[ffmpeg] done: ${OUT_DIR}/${BIN_NAME} (${SIZE_MB} MB)"
