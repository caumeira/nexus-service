# Nexus for Linux (x86-64)

A single `linux-x64` build covers mainstream **glibc x86-64 desktop Linux** -
Ubuntu/Debian, Fedora (incl. Bazzite/SteamOS-like), Arch, openSUSE, Pop!_OS,
Mint. Not covered: musl distros (Alpine) and ARM64.

## Install

```sh
tar xf Nexus-Linux-x64.tar.gz
cd nexus
./install.sh
```

This installs Nexus as a **root systemd daemon** for full hardware access
(motherboard PWM, NVML GPU fans, kernel modules, raw i2c/hidraw) with no
udev rules or group membership to juggle:

- app in `/opt/nexus`, unit at `/etc/systemd/system/nexus.service`
  (enabled and started immediately)
- an application menu entry that opens the dashboard
- the motherboard Super-I/O sensor driver, loaded via `setup-sensors.sh`

Needs `sudo`. Immutable-distro friendly (Bazzite/rpm-ostree): `/opt` and
`/etc` are writable there.

Open the dashboard at <http://localhost:9400>.

> The daemon adopts the active user's login session at startup so the tray,
> MPRIS media, volume, and dashboard work. If you installed before logging
> in, run `sudo systemctl restart nexus` once.

## Manage

```sh
systemctl status nexus            # state
sudo systemctl restart nexus      # restart
journalctl -u nexus -f            # logs
```

## Uninstall

From the extracted tarball directory (`uninstall.sh` is not copied to
`/opt/nexus`):

```sh
./uninstall.sh
```

The sensors module config under `/etc/modules-load.d` + `/etc/modprobe.d`
and any DKMS module are left in place.

## What works on Linux

Sensors (hwmon), CPU/GPU performance, network, USB enumeration, screen-time,
shortcuts, weather, audio-reactive lighting, system tray, start at boot -
plus, in this build: RGB (OpenRGB, native i2c/hidraw), HYTE serial devices
(NP50 / MiniHub / CNVS / Q-series cooler / Y70), motherboard fan control
(hwmon PWM), keyboard macros (uinput), media (MPRIS), volume
(PipeWire/PulseAudio), display brightness (backlight + DDC/CI), and
screen-mirror lighting (xdg-desktop-portal ScreenCast).

Not available on Linux: the in-game FPS overlay and the floating
desktop-widget overlay (no viable host).
