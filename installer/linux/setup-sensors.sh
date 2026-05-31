#!/usr/bin/env bash
# Bring up the motherboard Super-I/O sensor chip so Nexus can read and control
# fans through the kernel hwmon interface — the same universal layer every
# Linux fan tool (lm-sensors, fancontrol, CoolerControl) uses.
#
# Strategy, lightest first:
#   1. sensors-detect --auto loads the in-tree driver (nct6775, in-tree it87,
#      w83627…). This covers the large majority of boards — no build needed.
#   2. If that exposes controllable PWM, persist the loaded modules and stop.
#   3. Newest ITE chips (e.g. IT8689E / IT8696E) aren't in the mainline it87.
#      When we see an "unknown ITE ID" and DKMS is available, build+install the
#      maintained frankcrawford/it87 fork (the same module CoolerControl's docs
#      point users to). Otherwise print the one-line install hint and move on —
#      Nexus still works, fans just stay BIOS-controlled until the driver lands.
#
# Run as root (install.sh calls it via sudo). Never writes to /usr; module
# autoload + options go under /etc, which is writable on immutable distros.
set -euo pipefail

IT87_FORK="https://github.com/frankcrawford/it87"
MODLOAD=/etc/modules-load.d/nexus-sensors.conf
MODOPTS=/etc/modprobe.d/nexus-sensors.conf

log(){ echo "   $*"; }
have_pwm(){ compgen -G "/sys/class/hwmon/hwmon*/pwm[0-9]" >/dev/null 2>&1; }

# USB liquid coolers / AIOs / fan hubs are driven through the liquidctl CLI
# (Nexus auto-detects it at runtime). We only detect + guide — installing a
# system package or touching the user's Python env without consent isn't ours
# to do. Most distros: `<pkg-mgr> install liquidctl`.
if ! command -v liquidctl >/dev/null 2>&1; then
  echo "==> USB coolers (NZXT/Corsair/etc.): install 'liquidctl' to enable them"
  echo "    e.g. dnf install liquidctl  |  apt install liquidctl  |  pacman -S liquidctl"
fi

# Record the SuperIO hwmon modules currently loaded so they re-load at boot.
persist_loaded(){
  local mods
  mods=$(for m in nct6775 nct6683 it87 w83627ehf w83627hf f71882fg; do
           lsmod | grep -q "^$m " && echo "$m"; done)
  [ -n "$mods" ] || return 0
  printf '# Written by Nexus setup-sensors.sh\n%s\n' "$mods" > "$MODLOAD"
  log "persisted module autoload: $(echo $mods | tr '\n' ' ')"
}

echo "==> Detecting motherboard sensor chip"

# 1. In-tree drivers via lm-sensors (answer every prompt with the default).
if command -v sensors-detect >/dev/null 2>&1; then
  yes '' | sensors-detect --auto >/tmp/nexus-sensors-detect.log 2>&1 || true
fi

# 2. Did we get controllable fans for free?
if have_pwm; then
  log "fan PWM exposed by in-tree driver"
  persist_loaded
  exit 0
fi

# 3. Look for an ITE chip the mainline driver rejected.
ite_id=$(grep -oiE 'unknown chip with ID 0x8[0-9a-f]{3}' /tmp/nexus-sensors-detect.log 2>/dev/null \
           | grep -oiE '0x8[0-9a-f]{3}' | head -1 || true)
if [ -z "$ite_id" ]; then
  log "no software-controllable fan controller found — fans stay BIOS-managed"
  exit 0
fi
log "found unsupported ITE Super-I/O chip $ite_id (not in mainline it87)"

# 4. Build the maintained it87 fork. DKMS is the portable, kernel-update-safe
#    path; without it we don't hand-roll an install — just guide the user.
if ! command -v dkms >/dev/null 2>&1; then
  log "DKMS not installed — install it plus kernel headers, then re-run, or"
  log "install the distro 'it87-dkms' package. Skipping fan-driver build."
  exit 0
fi
if [ ! -d "/lib/modules/$(uname -r)/build" ]; then
  log "kernel headers for $(uname -r) missing — install them and re-run. Skipping."
  exit 0
fi

work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
if ! git clone --depth 1 "$IT87_FORK" "$work/it87" >/dev/null 2>&1; then
  log "could not fetch it87 fork (offline?) — skipping"; exit 0
fi
ver="nexus-it87-$(date +%Y%m%d 2>/dev/null || echo 1)"
src="/usr/src/$ver"
rm -rf "$src"; cp -a "$work/it87" "$src"
# Minimal dkms.conf if the fork ships none.
[ -f "$src/dkms.conf" ] || cat > "$src/dkms.conf" <<DKMS
PACKAGE_NAME="${ver%-*}"
PACKAGE_VERSION="${ver##*-}"
BUILT_MODULE_NAME[0]="it87"
DEST_MODULE_LOCATION[0]="/extra"
AUTOINSTALL="yes"
DKMS

if dkms add -m "${ver%-*}" -v "${ver##*-}" >/dev/null 2>&1 \
   && dkms install -m "${ver%-*}" -v "${ver##*-}" >/dev/null 2>&1; then
  log "it87 fork installed via DKMS (survives kernel updates)"
else
  log "DKMS build failed — see 'dkms status'. Skipping fan-driver setup."
  exit 0
fi

# 5. Autoload it87 at boot. ignore_resource_conflict lets it bind past the ACPI
#    region claim that blocks it on many recent boards.
printf '# Written by Nexus setup-sensors.sh\nit87\n' > "$MODLOAD"
printf '# Written by Nexus setup-sensors.sh\noptions it87 ignore_resource_conflict=1\n' > "$MODOPTS"
modprobe it87 ignore_resource_conflict=1 2>/dev/null || true
have_pwm && log "fan PWM now available via it87" || log "it87 loaded; verify fan headers in Nexus"
exit 0
