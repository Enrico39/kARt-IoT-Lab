#!/usr/bin/env bash
# kARt — installazione completa della Raspberry Pi Zero W (Gateway layer).
#
# Prerequisiti: microSD scritta con Raspberry Pi Imager (Raspberry Pi OS Lite 32-bit,
# hostname, utente, Wi-Fi e SSH configurati), chiave SSH copiata. Funziona con qualunque nome utente.
#
# Uso, dal Mac nella cartella code/pi:
#   ssh arkart 'mkdir -p ~/arkart' && scp arkart_bridge.py arkart-bridge.service mosquitto-arkart.conf setup_pi.sh arkart:~/arkart/
#   ssh -t arkart 'bash ~/arkart/setup_pi.sh'
#
# Chiede la password di sudo una sola volta (poi sudo resta senza password per l'utente corrente).
# Si può rilanciare senza problemi: ogni passo è idempotente.

set -euo pipefail
DIR="$(cd "$(dirname "$0")" && pwd)"
UTENTE="$(whoami)"

passo() { echo; echo "==> $*"; }

passo "1/6 sudo senza password per $UTENTE"
REGOLA="/etc/sudoers.d/010_${UTENTE}-nopasswd"
if [ ! -f "$REGOLA" ]; then
  echo "$UTENTE ALL=(ALL) NOPASSWD: ALL" | sudo tee "$REGOLA.tmp" >/dev/null
  sudo chmod 440 "$REGOLA.tmp"
  sudo visudo -cf "$REGOLA.tmp" >/dev/null
  sudo mv "$REGOLA.tmp" "$REGOLA"
fi
echo "ok"

passo "2/6 aggiornamento del sistema (sulla Pi Zero può richiedere 30-60 minuti)"
sudo apt-get update
sudo DEBIAN_FRONTEND=noninteractive apt-get -y -o Dpkg::Options::=--force-confold full-upgrade

passo "3/6 pacchetti kARt"
sudo DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends \
  mosquitto mosquitto-clients python3-serial python3-paho-mqtt python3-picamera2 iw git

passo "4/6 broker MQTT Mosquitto"
sudo cp "$DIR/mosquitto-arkart.conf" /etc/mosquitto/conf.d/arkart.conf
sudo systemctl enable mosquitto
sudo systemctl restart mosquitto
echo "mosquitto: $(systemctl is-active mosquitto)"

passo "5/6 servizio arkart-bridge"
sed -e "s|__UTENTE__|$UTENTE|" -e "s|__DIR__|$DIR|" "$DIR/arkart-bridge.service" | sudo tee /etc/systemd/system/arkart-bridge.service >/dev/null
sudo systemctl daemon-reload
sudo systemctl enable arkart-bridge
sudo systemctl restart arkart-bridge
echo "arkart-bridge: $(systemctl is-active arkart-bridge)"

passo "6/6 Wi-Fi senza risparmio energetico"
CONN="$(nmcli -t -f NAME,DEVICE connection show --active | grep ':wlan0$' | cut -d: -f1)"
if [ -n "$CONN" ]; then
  sudo nmcli connection modify "$CONN" 802-11-wireless.powersave 2
  echo "powersave: $(nmcli -g 802-11-wireless.powersave connection show "$CONN")"
else
  echo "ATTENZIONE: nessuna connessione Wi-Fi attiva trovata su wlan0"
fi

echo
echo "Installazione completata. Riavvia con: sudo reboot"
