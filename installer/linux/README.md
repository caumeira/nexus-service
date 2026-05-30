# Nexus for Linux (x86-64)

A single `linux-x64` build covers mainstream **glibc x86-64 desktop Linux** —
Ubuntu/Debian, Fedora (incl. Bazzite/SteamOS-like), Arch, openSUSE, Pop!_OS,
Mint. Not covered: musl distros (Alpine) and ARM64.

## Install

```sh
tar xf Nexus-Linux-x64.tar.gz
cd nexus
./install.sh
```

This installs the app to `~/.local/share/nexus`, a `systemd --user` service
(`nexus.service`, started now and at login), an application menu entry, and
udev rules (`/etc/udev/rules.d/99-nexus.rules`, via `sudo`) plus group
membership for device access. Nothing is written to `/usr`, so it works on
immutable distros (Bazzite/rpm-ostree — `/etc` is writable there).

Open the dashboard at <http://localhost:9400>.

> First install: log out and back in once so the new `i2c` / `input` /
> `dialout` group memberships take effect (needed for DDC brightness, keyboard
> macros, and HYTE serial devices).

## Manage

```sh
systemctl --user status nexus      # state
systemctl --user restart nexus     # restart
journalctl --user -u nexus -f      # logs
```

## Uninstall

```sh
~/.local/share/nexus/uninstall.sh
```

## What works on Linux

Sensors (hwmon), CPU/GPU performance, network, USB enumeration, screen-time,
shortcuts, weather, audio-reactive lighting, system tray — plus, in this build:
RGB (OpenRGB, native i2c/hidraw), HYTE serial devices (NP50 / MiniHub / CNVS /
Q-series cooler / Y70), motherboard fan control (hwmon PWM), keyboard macros
(uinput), media (MPRIS), volume (PipeWire/PulseAudio), display brightness
(backlight + DDC/CI), and start-at-login.

Not available on Linux: the in-game FPS overlay and the floating desktop-widget
overlay (no viable host), plus screen-mirror→lighting.

## Notes / permissions

- **Motherboard fan PWM** writes are driver-dependent. The udev rule grants the
  `input` group write access to `pwmN`, but some hwmon drivers re-assert
  root-only permissions; if so, fans stay read-only (RPM still shown).
- **DDC/CI brightness** needs the `i2c-dev` module loaded and `/dev/i2c-*`
  readable (the udev rule + `i2c` group handle this). Internal laptop panels use
  `/sys/class/backlight` and need no special access.
