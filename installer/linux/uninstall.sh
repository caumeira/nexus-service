#!/usr/bin/env bash
# Remove the Nexus root daemon. Needs sudo.
set -euo pipefail

echo "==> Stopping + disabling the system service (sudo)"
sudo systemctl disable --now nexus.service 2>/dev/null || true
sudo rm -f /etc/systemd/system/nexus.service
sudo systemctl daemon-reload 2>/dev/null || true

echo "==> Removing app + menu entry (sudo)"
sudo rm -rf /opt/nexus
rm -f "$HOME/.local/share/applications/nexus.desktop"
rm -f "$HOME/.local/share/icons/hicolor/512x512/apps/nexus.png"

echo "Nexus removed. The it87/sensors module config under /etc/modules-load.d +"
echo "/etc/modprobe.d (if installed) and any DKMS module were left in place."
