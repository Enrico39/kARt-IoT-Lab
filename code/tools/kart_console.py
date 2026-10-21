#!/usr/bin/env python3
"""kARt — console di guida da tastiera.

Invia comandi C,<seq>,<throttle>,<steer>,<pan> a 30 Hz e mostra la telemetria
T,<seq>,<dist_cm>,<stato>. Due modalità:

    python3 kart_console.py                  # USB seriale diretta all'Arduino (fa la parte della Pi)
    python3 kart_console.py /dev/cu.xxx      # come sopra, porta indicata a mano
    python3 kart_console.py --udp emety.local   # via Wi-Fi attraverso la Pi (fa la parte del Quest)

Tasti:
    w / s   acceleratore + / -  (passi da 50, da -255 a 255)
    a / d   sterzo sinistra / destra
    q / e   camera sinistra / destra
    c       camera al centro
    spazio  stop (throttle e sterzo a 0)
    f       simula la perdita del Wi-Fi (smette di inviare): prova del watchdog
    1 - 9   velocità massima (solo seriale): 1 = 25%, 9 = 100%
    x       esci
"""

import curses
import glob
import socket
import sys
import time

import serial

PERIODO_INVIO = 1 / 30
PASSO = 50
PASSO_PAN = 15
PAN_MAX = 45
STOP_CM = 15
PORTA_COMANDI = 5005
PORTA_TELEMETRIA = 5006


class Seriale:
    """Collegamento USB diretto all'Arduino."""

    def __init__(self, porta):
        self.descrizione = f"seriale {porta}"
        self.ser = serial.Serial(porta, 115200, timeout=0)
        time.sleep(2)  # all'apertura della porta l'Arduino si riavvia

    def invia(self, riga):
        self.ser.write((riga + "\n").encode())

    def ricevi(self):
        return self.ser.read(256)

    def chiudi(self):
        self.ser.close()


class Udp:
    """Collegamento Wi-Fi attraverso il bridge sulla Pi, come farà il Quest."""

    def __init__(self, host):
        self.destinazione = (socket.gethostbyname(host), PORTA_COMANDI)
        self.descrizione = f"UDP {host} ({self.destinazione[0]}:{PORTA_COMANDI})"
        self.sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        self.sock.bind(("0.0.0.0", PORTA_TELEMETRIA))
        self.sock.setblocking(False)

    def invia(self, riga):
        self.sock.sendto((riga + "\n").encode(), self.destinazione)

    def ricevi(self):
        dati = b""
        while True:
            try:
                dati += self.sock.recv(256)
            except BlockingIOError:
                return dati

    def chiudi(self):
        self.sock.close()


def trova_porta():
    porte = glob.glob("/dev/cu.usbserial*") + glob.glob("/dev/cu.wchusbserial*") + glob.glob("/dev/ttyUSB*") + glob.glob("/dev/ttyACM*")
    if not porte:
        sys.exit("Arduino non trovato: è collegato via USB? Puoi anche passare la porta come argomento.")
    return porte[0]


def limita(v, lo, hi):
    return max(lo, min(hi, v))


def main(scr, link):
    curses.curs_set(0)
    scr.nodelay(True)

    seq = 0
    throttle = steer = pan = 0
    in_pausa = False
    telemetria = "—"
    ricevuti = 0
    buffer = b""
    ultimo_invio = 0.0
    inviati = 0
    rtt_ms = None
    invii = {}  # seq -> istante di invio, per calcolare l'RTT
    avviso = ""

    while True:
        tasto = scr.getch()
        if tasto != -1:
            k = chr(tasto) if 0 <= tasto < 256 else ""
            if k == "x":
                break
            elif k == "w": throttle = limita(throttle + PASSO, -255, 255)
            elif k == "s": throttle = limita(throttle - PASSO, -255, 255)
            elif k == "a": steer = limita(steer - PASSO, -255, 255)
            elif k == "d": steer = limita(steer + PASSO, -255, 255)
            elif k == "q": pan = limita(pan - PASSO_PAN, -PAN_MAX, PAN_MAX)
            elif k == "e": pan = limita(pan + PASSO_PAN, -PAN_MAX, PAN_MAX)
            elif k == "c": pan = 0
            elif k == " ": throttle = steer = 0
            elif k == "f": in_pausa = not in_pausa
            elif k in "123456789":
                if isinstance(link, Seriale):
                    velocita = round(255 * int(k) / 9)
                    link.invia(f"P,{velocita},{STOP_CM}")
                    avviso = f"Velocità massima impostata a {velocita}/255"
                else:
                    avviso = "Via UDP la velocità massima si imposta da MQTT (arkart/config)"

        ora = time.monotonic()
        if not in_pausa and ora - ultimo_invio >= PERIODO_INVIO:
            ultimo_invio = ora
            seq = (seq + 1) % 65536
            link.invia(f"C,{seq},{throttle},{steer},{pan}")
            invii[seq] = ora
            invii.pop((seq - 300) % 65536, None)
            inviati += 1

        buffer += link.ricevi()
        while b"\n" in buffer:
            riga, buffer = buffer.split(b"\n", 1)
            riga = riga.decode(errors="replace").strip()
            if riga.startswith("T,"):
                telemetria = riga
                ricevuti += 1
                try:
                    s = int(riga.split(",")[1])
                    if s in invii:
                        rtt_ms = (time.monotonic() - invii[s]) * 1000
                except (IndexError, ValueError):
                    pass

        scr.erase()
        scr.addstr(0, 0, "kARt console  —  " + link.descrizione)
        scr.addstr(2, 0, f"Throttle {throttle:5d}   Sterzo {steer:5d}   Camera {pan:4d}°")
        scr.addstr(3, 0, "INVIO IN PAUSA (Wi-Fi simulato perso): il kart deve andare in FAILSAFE" if in_pausa else f"Comandi inviati: {inviati}")
        scr.addstr(5, 0, f"Telemetria: {telemetria}   (ricevute {ricevuti})")
        parti = telemetria.split(",")
        if len(parti) == 4:
            dist, stato = parti[2], parti[3]
            dist_txt = "nessun ostacolo" if dist == "-1" else f"{dist} cm"
            rtt_txt = f"{rtt_ms:.0f} ms" if rtt_ms is not None else "—"
            scr.addstr(6, 0, f"Dietro: {dist_txt:16s} Stato: {stato:9s} RTT: {rtt_txt}")
        scr.addstr(8, 0, "w/s acceleratore   a/d sterzo   q/e camera   c centro   spazio stop")
        scr.addstr(9, 0, "f simula perdita Wi-Fi   1-9 velocità massima   x esci")
        if avviso:
            scr.addstr(11, 0, avviso)
        scr.refresh()
        time.sleep(0.005)

    # uscita pulita: ferma il kart
    link.invia(f"C,{(seq + 1) % 65536},0,0,0")
    link.chiudi()


if __name__ == "__main__":
    args = sys.argv[1:]
    try:
        if args and args[0] == "--udp":
            link = Udp(args[1] if len(args) > 1 else "emety.local")
        else:
            link = Seriale(args[0] if args else trova_porta())
    except serial.SerialException as e:
        if "busy" in str(e).lower():
            sys.exit("La porta seriale è occupata da un altro programma.\n"
                     "Chiudi il Monitor Seriale dell'Arduino IDE (o l'IDE intero) e riprova.")
        raise
    except socket.gaierror:
        sys.exit("Pi non trovata in rete: è accesa e collegata allo stesso Wi-Fi?")
    curses.wrapper(main, link)
