# Third-Party Software

This service bundles third-party software which is distributed under separate
licenses. Their respective license texts ship alongside the bundled binaries.

## OpenRGB (headless build)

- **Project**: OpenRGB
- **Upstream**: https://gitlab.com/CalcProgrammer1/OpenRGB
- **Headless fork (source code, GPLv2 §3 compliance)**: https://github.com/hello-nexus/openrgb-headless
- **License**: GNU General Public License version 2 or later (GPL-2.0-or-later)
- **License text**: shipped with the bundled binary at
  `openrgb/LICENSE-OpenRGB.txt` in the publish output
- **Communication boundary**: This service launches the bundled OpenRGB binary
  as a child process and communicates with it exclusively over a local TCP
  socket (the OpenRGB SDK protocol on `127.0.0.1:6742`). No part of this service
  links against, dynamically loads, or calls into OpenRGB's address space. Per
  the GPL FAQ this constitutes "mere aggregation" via IPC, not a derivative work.
- **Modifications**: The headless fork strips the Qt5 dependency entirely
  (Widgets, Gui, Core, DBus) so the SDK server can ship as a small Qt-less
  binary. The patches are tiny and viewable in the fork's diff against
  upstream `master`.

## FFmpeg (minimal build)

- **Project**: FFmpeg
- **Upstream**: https://ffmpeg.org
- **License**: GNU Lesser General Public License version 2.1 or later
  (LGPL-2.1-or-later)
- **License text**: shipped with the bundled binary at `ffmpeg/LICENSE.txt`
  in the publish output
- **Build**: compiled from unmodified upstream release source by
  `scripts/build-ffmpeg-minimal.sh` with an LGPL-only configuration
  (`--disable-gpl --disable-nonfree`); no GPL components are enabled and no
  source changes are made, so the corresponding source is the upstream
  release tarball of the pinned version
- **Communication boundary**: launched as a separate child process; this
  service does not link against the FFmpeg libraries

## LibreHardwareMonitor

- **Project**: LibreHardwareMonitorLib
- **Upstream**: https://github.com/LibreHardwareMonitor/LibreHardwareMonitor
- **License**: Mozilla Public License 2.0 (MPL-2.0)
- **Usage**: consumed unmodified as the official NuGet package; provides the
  Windows sensor backend (CPU / GPU / motherboard / fan / temperature)

## PawnIO

- **Project**: PawnIO (signed kernel I/O driver + module loader)
- **Upstream**: https://github.com/namazso/PawnIO
- **License**: GNU General Public License version 2 with a special linking
  exception (see the bundled `NOTICE`)
- **License text**: shipped alongside the bundled files (`COPYING` and
  `NOTICE` under the bundled `pawnio/` directory)
- **Usage**: `PawnIOLib.dll` is loaded in-process on Windows
  (LibreHardwareMonitorLib uses it for LPC/SMBus access, permitted by the
  linking exception) and the bundled OpenRGB child process uses it to probe
  SMBus for RGB DRAM

## dfu-util

- **Project**: dfu-util
- **Upstream**: https://dfu-util.sourceforge.net
- **License**: GNU General Public License version 2 (see `COPYING` under the
  bundled `dfu-util/` directory)
- **Communication boundary**: spawned as a child process by the firmware
  flasher to write device firmware in DFU mode; not linked against
