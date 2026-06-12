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
