#!/usr/bin/env bash
# Kjøres på Raspberry Pi-en:  sudo ./install.sh
# Forutsetter at mappen "app" (output fra publish.ps1) ligger ved siden av dette skriptet.
set -euo pipefail

if [[ $EUID -ne 0 ]]; then echo "Kjør med sudo."; exit 1; fi
HERE="$(cd "$(dirname "$0")" && pwd)"
SRC="$HERE/app"
DEST=/opt/husskjerm

[[ -d "$SRC" ]] || { echo "Fant ikke $SRC"; exit 1; }

apt-get update -y
apt-get install -y rsync libfontconfig1 fonts-dejavu-core
apt-get install -y libgpiod2 || apt-get install -y libgpiod3 || true

id husskjerm &>/dev/null || useradd --system --create-home --home-dir /var/lib/husskjerm husskjerm
for g in gpio spi; do getent group "$g" >/dev/null && usermod -aG "$g" husskjerm || true; done

mkdir -p "$DEST/data"
# Ikke overskriv konfig og data ved oppdatering
rsync -a --exclude 'appsettings.local.json' --exclude 'data' "$SRC"/ "$DEST"/
[[ -f "$DEST/appsettings.local.json" ]] || echo '{}' > "$DEST/appsettings.local.json"
chmod +x "$DEST/HusSkjerm"
chown -R husskjerm:husskjerm "$DEST"
chmod 600 "$DEST/appsettings.local.json"

install -m 644 "$HERE/husskjerm.service" /etc/systemd/system/husskjerm.service
systemctl daemon-reload
systemctl enable husskjerm.service
systemctl restart husskjerm.service

echo
echo "Ferdig. Se logg med:  journalctl -u husskjerm -f"
echo "Kontrollpanel: http://$(hostname).local"
echo "Husk: legg Aqara-nøkler i $DEST/appsettings.local.json (se docs/RASPBERRY-PI-OPPSETT.md)"
