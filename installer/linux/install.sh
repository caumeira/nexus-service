#!/usr/bin/env bash
# Install Nexus as a ROOT system daemon (like coolercontrol's coolercontrold)
# for full hardware access — motherboard pwm, NVML GPU fans, kernel modules,
# raw i2c/hidraw — with no udev rules or group membership to juggle. The daemon
# adopts the active user's login session at startup so the tray, MPRIS media,
# volume, and dashboard still work. Needs sudo.
# Immutable-distro friendly (Bazzite/rpm-ostree): /opt and /etc are writable.
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
APP_DIR=/opt/nexus
UNIT=/etc/systemd/system/nexus.service
APPS_DIR="$HOME/.local/share/applications"
ICON_DIR="$HOME/.local/share/icons/hicolor/512x512/apps"

echo "==> Installing Nexus (root daemon) to $APP_DIR (sudo)"
sudo mkdir -p "$APP_DIR"
for entry in "$HERE"/* "$HERE"/.[!.]*; do
  [ -e "$entry" ] || continue
  case "$(basename "$entry")" in
    install.sh|uninstall.sh|README.md|nexus.service|99-nexus.rules|setup-sensors.sh) continue ;;
  esac
  sudo cp -a "$entry" "$APP_DIR/"
done
sudo chmod +x "$APP_DIR/Nexus"
[ -f "$APP_DIR/openrgb/openrgb-headless" ] && sudo chmod +x "$APP_DIR/openrgb/openrgb-headless" || true
# SELinux: a system service can't exec from a user home (user_home_t); /opt gets
# bin_t. restorecon stamps the default context so systemd can launch it.
sudo restorecon -R "$APP_DIR" 2>/dev/null || true

# Desktop menu entry just opens the dashboard — the binary is the service now,
# not a user-launched app. (The tray's "Open Dashboard" gives the --app window.)
mkdir -p "$APPS_DIR" "$ICON_DIR"
[ -f "$APP_DIR/nexus.png" ] && cp "$APP_DIR/nexus.png" "$ICON_DIR/nexus.png" 2>/dev/null || true
cat > "$APPS_DIR/nexus.desktop" <<EOF
[Desktop Entry]
Type=Application
Name=Nexus
Comment=Nexus hardware monitoring and control
Exec=xdg-open http://localhost:9400
Icon=nexus
Terminal=false
Categories=Utility;System;
EOF

# Load the motherboard Super-I/O fan driver (it87 etc.). Root daemon reads/writes
# hwmon directly; this only ensures the kernel module is present.
sudo bash "$HERE/setup-sensors.sh" || echo "   (sensor driver setup skipped)"

echo "==> Installing + enabling the system service (sudo)"
sudo cp "$HERE/nexus.service" "$UNIT"
sudo restorecon "$UNIT" 2>/dev/null || true
sudo systemctl daemon-reload
sudo systemctl enable --now nexus.service

echo
echo "Nexus installed as a root daemon. Dashboard: http://localhost:9400"
echo "Tray + media attach to your login session at startup — if you installed"
echo "before logging in, run:  sudo systemctl restart nexus"
