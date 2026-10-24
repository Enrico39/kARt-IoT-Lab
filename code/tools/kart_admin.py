#!/usr/bin/env python3
"""kARt — console di gestione della Raspberry Pi e di Node-RED.

    python3 kart_admin.py                 # Pi raggiunta con l'alias SSH "arkart"
    python3 kart_admin.py --host <host>   # altro alias SSH o utente@indirizzo

La Pi si gestisce via SSH (serve l'accesso con chiave e sudo senza password, come
li configura setup_pi.sh). Node-RED gira su questo computer, da code/nodered.
Usa solo la libreria standard di Python.
"""

import os
import signal
import subprocess
import sys
import time
import urllib.request
import webbrowser
from pathlib import Path

SERVIZI = ["arkart-bridge", "arkart-camera", "mosquitto"]
PORTA_NODERED = 1880
PORTA_VIDEO = 8080
CARTELLA_NODERED = Path(__file__).resolve().parent.parent / "nodered"
LOG_NODERED = CARTELLA_NODERED / "node-red.log"
DASHBOARD = f"http://localhost:{PORTA_NODERED}/dashboard/kart"


# ---------------------------------------------------------------- Raspberry Pi

class Pi:
    def __init__(self, host):
        self.host = host

    def ssh(self, comando, timeout=15):
        """Esegue un comando sulla Pi; restituisce (ok, output)."""
        try:
            r = subprocess.run(
                ["ssh", "-o", "ConnectTimeout=8", "-o", "BatchMode=yes", self.host, comando],
                capture_output=True, text=True, timeout=timeout)
            return r.returncode == 0, (r.stdout + r.stderr).strip()
        except subprocess.TimeoutExpired:
            return False, "timeout"

    def online(self):
        # la prima risoluzione del nome .local può richiedere qualche secondo
        return self.ssh("true", timeout=15)[0]

    def indirizzo(self):
        """Nome di rete vero dietro l'alias SSH (per lo stream HTTP)."""
        r = subprocess.run(["ssh", "-G", self.host], capture_output=True, text=True)
        for riga in r.stdout.splitlines():
            if riga.startswith("hostname "):
                return riga.split(" ", 1)[1]
        return self.host.split("@")[-1]

    def stato(self):
        if not self.online():
            print("Pi non raggiungibile (spenta o fuori rete).")
            return
        comando = (
            f"for s in {' '.join(SERVIZI)}; do printf '%-16s %s\\n' $s $(systemctl is-active $s); done; "
            "echo; echo \"temperatura   $(vcgencmd measure_temp | cut -d= -f2)\"; "
            "t=$(vcgencmd get_throttled | cut -d= -f2); "
            "echo \"alimentazione $( [ \"$t\" = 0x0 ] && echo ok || echo \"cali di tensione ($t)\")\"; "
            "echo \"wi-fi         $(awk 'NR==3{print int($4)\" dBm\"}' /proc/net/wireless)\"; "
            "echo \"acceso da     $(uptime -p | sed 's/up //')\"; "
            "echo \"carico        $(cut -d' ' -f1-3 /proc/loadavg)\""
        )
        print(self.ssh(comando)[1])

    def riavvia_servizio(self, servizio):
        ok, out = self.ssh(f"sudo -n systemctl restart {servizio} && systemctl is-active {servizio}")
        print(f"{servizio}: {out}" if ok else f"Errore: {out}")

    def log(self, servizio, righe=25):
        print(self.ssh(f"journalctl -u {servizio} -n {righe} --no-pager -o short-iso")[1])

    def foto(self):
        url = f"http://{self.indirizzo()}:{PORTA_VIDEO}/snapshot.jpg"
        destinazione = Path.home() / "Desktop" / f"kart_{time.strftime('%Y%m%d_%H%M%S')}.jpg"
        try:
            with urllib.request.urlopen(url, timeout=5) as r:
                destinazione.write_bytes(r.read())
        except OSError as e:
            print(f"Foto non disponibile ({e}). Il servizio arkart-camera è attivo?")
            return
        print(f"Salvata in {destinazione}")
        subprocess.run(["open", str(destinazione)])

    def spegni(self, riavvio=False):
        azione = "riavviare" if riavvio else "spegnere"
        if input(f"Sicuro di voler {azione} la Pi? [s/N] ").strip().lower() != "s":
            return
        self.ssh("sudo -n reboot" if riavvio else "sudo -n shutdown -h now")
        time.sleep(3)
        if riavvio:
            print("Riavvio in corso: torna raggiungibile in circa un minuto.")
        elif self.online():
            print("La Pi risponde ancora: riprova tra qualche secondo.")
        else:
            print("Spenta. Aspetta che il LED verde smetta di lampeggiare prima di staccare l'alimentazione.")


# ---------------------------------------------------------------- Node-RED

def pid_nodered():
    r = subprocess.run(["lsof", "-ti", f"tcp:{PORTA_NODERED}", "-sTCP:LISTEN"],
                       capture_output=True, text=True)
    pid = r.stdout.split()
    return int(pid[0]) if pid else None


def stato_nodered():
    pid = pid_nodered()
    print(f"Node-RED attivo (pid {pid}), dashboard: {DASHBOARD}" if pid else "Node-RED spento.")


def avvia_nodered():
    if pid_nodered():
        print("Node-RED è già attivo.")
        return
    if not (CARTELLA_NODERED / "node_modules").exists():
        print("Prima installazione: npm install…")
        subprocess.run(["npm", "install"], cwd=CARTELLA_NODERED, check=True)
    with open(LOG_NODERED, "a") as log:
        subprocess.Popen(["npm", "start"], cwd=CARTELLA_NODERED, stdout=log, stderr=subprocess.STDOUT,
                         start_new_session=True)   # resta acceso anche chiudendo questa console
    for _ in range(30):
        time.sleep(1)
        if pid_nodered():
            print(f"Node-RED avviato: {DASHBOARD}  (log in {LOG_NODERED.name})")
            return
    print(f"Node-RED non risponde ancora: guarda {LOG_NODERED}")


def ferma_nodered():
    pid = pid_nodered()
    if not pid:
        print("Node-RED è già spento.")
        return
    os.kill(pid, signal.SIGTERM)
    for _ in range(10):
        time.sleep(0.5)
        if not pid_nodered():
            print("Node-RED fermato.")
            return
    print("Node-RED non si è fermato: riprova o chiudilo a mano.")


# ---------------------------------------------------------------- Menu

def scegli_servizio():
    for i, s in enumerate(SERVIZI, 1):
        print(f"  {i}. {s}")
    scelta = input("Servizio: ").strip()
    if scelta.isdigit() and 1 <= int(scelta) <= len(SERVIZI):
        return SERVIZI[int(scelta) - 1]
    return None


MENU = """
kARt admin — Pi: {host}
  Raspberry Pi                        Node-RED
  1  stato                            7  stato
  2  riavvia un servizio              8  avvia
  3  log di un servizio               9  ferma
  4  foto dalla camera                0  apri la dashboard
  5  riavvia la Pi
  6  spegni la Pi                     q  esci
"""


def main():
    host = sys.argv[sys.argv.index("--host") + 1] if "--host" in sys.argv else "arkart"
    pi = Pi(host)
    while True:
        print(MENU.format(host=host))
        scelta = input("> ").strip().lower()
        print()
        if scelta == "q":
            break
        elif scelta == "1":
            pi.stato()
        elif scelta in ("2", "3"):
            servizio = scegli_servizio()
            if servizio:
                (pi.riavvia_servizio if scelta == "2" else pi.log)(servizio)
        elif scelta == "4":
            pi.foto()
        elif scelta == "5":
            pi.spegni(riavvio=True)
        elif scelta == "6":
            pi.spegni()
        elif scelta == "7":
            stato_nodered()
        elif scelta == "8":
            avvia_nodered()
        elif scelta == "9":
            ferma_nodered()
        elif scelta == "0":
            webbrowser.open(DASHBOARD)


if __name__ == "__main__":
    try:
        main()
    except (KeyboardInterrupt, EOFError):
        print()
