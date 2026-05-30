#!/usr/bin/env bash
# Remove a user install of Nexus. Leaves group memberships intact.
set -euo pipefail

echo "==> Stopping + disabling service"
systemctl --user disable --now nexus.service 2>/dev/null || true
rm -f "$HOME/.config/systemd/user/nexus.service"
systemctl --user daemon-reload 2>/dev/null || true

echo "==> Removing app + menu entry + autostart"
rm -rf "$HOME/.local/share/nexus"
rm -f "$HOME/.local/share/applications/nexus.desktop"
rm -f "$HOME/.local/share/icons/hicolor/512x512/apps/nexus.png"
rm -f "$HOME/.config/autostart/nexus.desktop"

echo "==> Removing udev rules (sudo)"
sudo rm -f /etc/udev/rules.d/99-nexus.rules 2>/dev/null || true
sudo udevadm control --reload-rules 2>/dev/null || true

echo "Nexus removed. Group memberships (i2c/input/dialout) were left in place."
