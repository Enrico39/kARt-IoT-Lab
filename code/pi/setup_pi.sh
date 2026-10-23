#!/usr/bin/env bash
# kARt — installazione completa della Raspberry Pi Zero W (Gateway layer).
#
# Prerequisiti: microSD scritta con Raspberry Pi Imager (Raspberry Pi OS Lite 32-bit,
# hostname, utente, Wi-Fi e SSH configurati), chiave SSH copiata. Funziona con qualunque nome utente.
#
# Uso, dal Mac nella cartella code/pi:
#   ssh arkart 'mkdir -p ~/arkart' && scp arkart_bridge.py arkart-bridge.service arkart_camera.py arkart-camera.service mosquitto-arkart.conf setup_pi.sh arkart:~/arkart/
#   ssh -t arkart 'bash ~/arkart/setup_pi.sh'
#
# Chiede la password di sudo una sola volta (poi sudo resta senza password per l'utente corrente).
# Si può rilanciare senza problemi: ogni passo è idempotente.

set -euo pipefail
DIR="$(cd "$(dirname "$0")" && pwd)"
UTENTE="$(whoami)"

passo() { echo; echo "==> $*"; }

passo "1/7 sudo senza password per $UTENTE"
REGOLA="/etc/sudoers.d/010_${UTENTE}-nopasswd"
if [ ! -f "$REGOLA" ]; then
  echo "$UTENTE ALL=(ALL) NOPASSWD: ALL" | sudo tee "$REGOLA.tmp" >/dev/null
  sudo chmod 440 "$REGOLA.tmp"
  sudo visudo -cf "$REGOLA.tmp" >/dev/null
  sudo mv "$REGOLA.tmp" "$REGOLA"
fi
echo "ok"

passo "2/7 aggiornamento del sistema (sulla Pi Zero può richiedere 30-60 minuti)"
sudo apt-get update
sudo DEBIAN_FRONTEND=noninteractive apt-get -y -o Dpkg::Options::=--force-confold full-upgrade

passo "3/7 pacchetti kARt"
sudo DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends \
  mosquitto mosquitto-clients python3-serial python3-paho-mqtt python3-picamera2 iw git

passo "4/7 broker MQTT Mosquitto"
sudo cp "$DIR/mosquitto-arkart.conf" /etc/mosquitto/conf.d/arkart.conf
sudo systemctl enable mosquitto
sudo systemctl restart mosquitto
echo "mosquitto: $(systemctl is-active mosquitto)"

passo "5/7 servizi arkart-bridge e arkart-camera"
for SERVIZIO in arkart-bridge arkart-camera; do
  sed -e "s|__UTENTE__|$UTENTE|" -e "s|__DIR__|$DIR|" "$DIR/$SERVIZIO.service" | sudo tee "/etc/systemd/system/$SERVIZIO.service" >/dev/null
done
sudo systemctl daemon-reload
for SERVIZIO in arkart-bridge arkart-camera; do
  sudo systemctl enable "$SERVIZIO"
  sudo systemctl restart "$SERVIZIO"
  echo "$SERVIZIO: $(systemctl is-active "$SERVIZIO")"
done

passo "6/7 Wi-Fi senza risparmio energetico"
CONN="$(nmcli -t -f NAME,DEVICE connection show --active | grep ':wlan0$' | cut -d: -f1)"
if [ -n "$CONN" ]; then
  sudo nmcli connection modify "$CONN" 802-11-wireless.powersave 2
  echo "powersave: $(nmcli -g 802-11-wireless.powersave connection show "$CONN")"
else
  echo "ATTENZIONE: nessuna connessione Wi-Fi attiva trovata su wlan0"
fi

passo "7/7 camera Freenove 8MP (IMX219): l'auto-detect non la riconosce, driver forzato"
CONFIG=/boot/firmware/config.txt
if ! grep -q '^dtoverlay=imx219' "$CONFIG"; then
  if grep -q '^camera_auto_detect=' "$CONFIG"; then
    sudo sed -i 's/^camera_auto_detect=.*/camera_auto_detect=0\ndtoverlay=imx219/' "$CONFIG"
  else
    printf '\n[all]\ncamera_auto_detect=0\ndtoverlay=imx219\n' | sudo tee -a "$CONFIG" >/dev/null
  fi
fi
grep -E '^(camera_auto_detect|dtoverlay=imx219)' "$CONFIG"

echo
echo "Installazione completata. Riavvia con: sudo reboot"
