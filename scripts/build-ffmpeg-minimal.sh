#!/usr/bin/env bash
# Builds a minimal ffmpeg binary sized for nexus-service's narrow use cases:
#   - decode images / gifs / common video containers (jpg, png, gif, mp4, webm, mov, avi, mkv, wmv, m4v, mpg)
#   - downscale + pad (libswscale via scale/pad filters)
#   - write raw rgb24 pipe/file, mjpeg thumbnails, and png/gif with alpha
#   - capture video: gdigrab (Win), avfoundation (macOS)
#   - capture audio: dshow (Win), avfoundation (macOS), pulse (Linux)
#
# Includes libx264 (GPL) for the H.264 mp4 the panel-background feature encodes
# per device. Configure flags still drop: streaming protocols, other GPL/nonfree
# encoders (x265/aom/vpx-enc), subtitle codecs, hardware accel, hundreds of
# obscure decoders. Outcome is a ~10-15 MB statically-linked ffmpeg vs the
# ~96 MB gyan.dev essentials. The bundle is GPLv2 (x264); ship COPYING.GPLv2.
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
  # Licensing: GPL (libx264). No nonfree.
  --enable-gpl
  --disable-nonfree
  --enable-libx264

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

  # Filters we literally call from MediaImporter.cs / PanelBgImporter.cs /
  # ScreenMirrorEffect.cs (crop: panel-background + lighting media cropper)
  --enable-filter=scale
  --enable-filter=pad
  --enable-filter=crop
  --enable-filter=format
  --enable-filter=fps
  # Transparency import: color+overlay mattes alpha onto black when the user
  # turns "keep transparency" off; split+palettegen+paletteuse are the only way
  # to write a GIF that keeps its transparent index (PanelBgImporter.cs).
  --enable-filter=color
  --enable-filter=overlay
  --enable-filter=split
  --enable-filter=palettegen
  --enable-filter=paletteuse
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

  # Encoders: rawvideo passthrough + mjpeg thumbnails/images + libx264 for the
  # per-device H.264 panel background + audio pipe. png/gif carry the alpha the
  # mjpeg/x264 pair cannot: neither has an alpha channel, so a transparent
  # source imported without these silently flattens onto whatever RGB sat under
  # its transparent pixels.
  --enable-encoder=rawvideo
  --enable-encoder=mjpeg
  --enable-encoder=libx264
  --enable-encoder=png
  --enable-encoder=gif
  --enable-encoder=pcm_s16le
  --enable-encoder=pcm_f32le

  # Muxers (mp4/mov carry the H.264 panel background; gif carries a transparent
  # animated background, image2 the single-frame png/jpg ones)
  --enable-muxer=rawvideo
  --enable-muxer=image2
  --enable-muxer=gif
  --enable-muxer=mp4
  --enable-muxer=mov
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
      --pkg-config-flags=--static
    )
    # libx264 built static from source into a private prefix. Homebrew's x264
    # ships a .dylib the macOS linker prefers over the .a, leaving the binary
    # with a runtime dependency on /opt/homebrew/opt/x264 that no end-user Mac
    # has (dyld: libx264.*.dylib not loaded). Building a static-only prefix
    # forces a self-contained binary - same reason the win path cross-builds it.
    MAC_X264_PREFIX="${BUILD_ROOT}/x264-mac/${RID}"
    if [[ ! -f "${MAC_X264_PREFIX}/lib/libx264.a" ]]; then
      echo "[ffmpeg] building static libx264 for ${RID}..."
      DEPS_WORK="${BUILD_ROOT}/x264-src"
      mkdir -p "${DEPS_WORK}"
      cd "${DEPS_WORK}"
      [[ -d x264 ]] || git clone --depth 1 --branch stable https://code.videolan.org/videolan/x264.git
      cd x264
      ./configure --enable-static --disable-cli --disable-opencl --prefix="${MAC_X264_PREFIX}"
      make -j"$(sysctl -n hw.ncpu)"
      make install
      cd "${SRC_DIR}"
    fi
    export PKG_CONFIG_PATH="${MAC_X264_PREFIX}/lib/pkgconfig:${PKG_CONFIG_PATH:-}"
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

    # libx264 (GPL) cross-built into the mingw sysroot for the H.264
    # panel-background encode. Own guard (not the zlib/bzip2 sentinel) so it
    # still builds on machines that pre-date this dep. Installs libx264.a +
    # x264.pc, picked up below via pkg-config.
    if [[ ! -f "${MINGW_ROOT}/lib/libx264.a" ]]; then
      echo "[ffmpeg] cross-building libx264 into mingw sysroot (one-time)..."
      DEPS_WORK="${BUILD_ROOT}/mingw-deps"
      mkdir -p "${DEPS_WORK}"
      cd "${DEPS_WORK}"
      [[ -d x264 ]] || git clone --depth 1 --branch stable https://code.videolan.org/videolan/x264.git
      cd x264
      ./configure --host=x86_64-w64-mingw32 --cross-prefix=x86_64-w64-mingw32- \
        --prefix="${MINGW_ROOT}" --enable-static --disable-cli --disable-opencl
      make -j"$(sysctl -n hw.ncpu 2>/dev/null || nproc)"
      make install
      cd "${SRC_DIR}"
    fi

    export PKG_CONFIG_PATH="${MINGW_ROOT}/lib/pkgconfig:${PKG_CONFIG_PATH:-}"

    EXTRA_CONFIG=(
      --arch=x86_64
      --target-os=mingw64
      --cross-prefix=x86_64-w64-mingw32-
      --pkg-config=pkg-config
      --pkg-config-flags=--static
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
    # --disable-autodetect requires opting into every external lib explicitly:
    # pulse capture (BeatsProvider runs `ffmpeg -f pulse`) needs both
    # --enable-libpulse and --enable-indev=pulse. No --pkg-config-flags=--static
    # either: libpulse's static dep chain is unsatisfiable on a normal glibc
    # host and would drop libpulse again. External deps (libx264, libpulse,
    # zlib, bzip2) link dynamically and ship as distro packages.
    EXTRA_CONFIG=(
      --arch=x86_64
      --target-os=linux
      --enable-avdevice
      --enable-libpulse
      --enable-indev=pulse
    )
    # libx264 from the system (apt install libx264-dev / equivalent).
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
# GPLv2: the build links libx264.
cp COPYING.GPLv2 "${OUT_DIR}/LICENSE.txt" 2>/dev/null || true

SIZE_BYTES=$(wc -c < "${OUT_DIR}/${BIN_NAME}")
SIZE_MB=$(awk "BEGIN {printf \"%.1f\", ${SIZE_BYTES}/1024/1024}")
echo "[ffmpeg] done: ${OUT_DIR}/${BIN_NAME} (${SIZE_MB} MB)"
