#!/usr/bin/env bash
# Install Nexus for the current user: app under ~/.local, a systemd --user
# service, a menu entry, and udev rules (via sudo) for device access.
# Works on immutable distros (Bazzite/rpm-ostree) — nothing is written to /usr.
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
APP_DIR="$HOME/.local/share/nexus"
UNIT_DIR="$HOME/.config/systemd/user"
APPS_DIR="$HOME/.local/share/applications"
ICON_DIR="$HOME/.local/share/icons/hicolor/512x512/apps"

echo "==> Installing Nexus to $APP_DIR"
mkdir -p "$APP_DIR" "$UNIT_DIR" "$APPS_DIR" "$ICON_DIR"

# Copy the app payload, excluding this tooling.
for entry in "$HERE"/* "$HERE"/.[!.]*; do
  [ -e "$entry" ] || continue
  case "$(basename "$entry")" in
    install.sh|uninstall.sh|README.md|nexus.service|99-nexus.rules|setup-sensors.sh) continue ;;
  esac
  cp -a "$entry" "$APP_DIR/"
done
chmod +x "$APP_DIR/Nexus"

# Menu entry + icon.
[ -f "$APP_DIR/nexus.png" ] && cp "$APP_DIR/nexus.png" "$ICON_DIR/nexus.png" || true
cat > "$APPS_DIR/nexus.desktop" <<EOF
[Desktop Entry]
Type=Application
Name=Nexus
Comment=Nexus hardware monitoring and control
Exec=$APP_DIR/Nexus
Icon=nexus
Terminal=false
Categories=Utility;System;
EOF

# systemd --user service (ExecStart uses %h, no templating needed).
cp "$HERE/nexus.service" "$UNIT_DIR/nexus.service"

# udev rules + group membership (needs root).
echo "==> Installing udev rules to /etc/udev/rules.d (sudo)"
if sudo cp "$HERE/99-nexus.rules" /etc/udev/rules.d/99-nexus.rules; then
  sudo udevadm control --reload-rules
  sudo udevadm trigger
  for grp in i2c input dialout; do
    if getent group "$grp" >/dev/null 2>&1; then
      sudo usermod -aG "$grp" "$USER" || true
    fi
  done
else
  echo "   (skipped udev rules — device access for i2c/uinput/serial may be limited)"
fi

# Motherboard fan driver: load the right hwmon Super-I/O module (needs root).
# Best-effort — Nexus runs fine without it, fans just stay BIOS-controlled.
sudo bash "$HERE/setup-sensors.sh" || echo "   (sensor driver setup skipped)"

echo "==> Enabling systemd --user service"
systemctl --user daemon-reload
systemctl --user enable --now nexus.service

echo
echo "Nexus installed. Dashboard: http://localhost:9400"
echo "If this was a first install, log out and back in so the new group"
echo "memberships (i2c/input/dialout) take effect for full device access."
