#!/usr/bin/env python3
"""kARt — bridge del Gateway layer (Raspberry Pi Zero W).

    Quest --UDP :5005--> bridge --seriale--> Arduino
    Quest <--UDP :5006-- bridge <--seriale-- Arduino
                         bridge --MQTT--> broker locale (arkart/kart/...)

Protocollo: vedi project.md §3. La guida passa solo da UDP e seriale:
se MQTT non è disponibile il kart continua a funzionare.
"""

import glob
import json
import logging
import os
import re
import select
import socket
import subprocess
import time

import serial

try:
    import paho.mqtt.client as mqtt
except ImportError:  # MQTT è opzionale per la guida
    mqtt = None

# --- Configurazione (modificabile da variabili d'ambiente) ---
PORTA_COMANDI = int(os.environ.get("ARKART_CMD_PORT", 5005))
PORTA_TELEMETRIA = int(os.environ.get("ARKART_TEL_PORT", 5006))
BAUD = 115200
MQTT_HOST = os.environ.get("ARKART_MQTT_HOST", "localhost")
MQTT_PORT = int(os.environ.get("ARKART_MQTT_PORT", 1883))

TIMEOUT_COMANDI_S = 0.25   # secondo livello di sicurezza (l'Arduino ha il suo a 300 ms)
NUOVA_SESSIONE_S = 1.0     # pausa oltre la quale il seq può ripartire da capo
ANTIRIMBALZO_EVENTI_S = 1.0  # lo stesso evento non viene ripubblicato prima di questo intervallo
PERIODO_MQTT_S = 0.5       # 2 Hz
PERIODO_RSSI_S = 2.0

TOPIC_TELEMETRIA = "arkart/kart/telemetry"
TOPIC_EVENTI = "arkart/kart/events"
TOPIC_STATO = "arkart/kart/status"
TOPIC_CONFIG = "arkart/config"

RE_COMANDO = re.compile(r"^C,(\d{1,5}),(-?\d{1,3}),(-?\d{1,3}),(-?\d{1,2})$")
RE_TELEMETRIA = re.compile(r"^T,(\d{1,5}),(-?\d+),([A-Z]+)$")

log = logging.getLogger("arkart")


def seq_piu_recente(nuovo, vecchio):
    """True se nuovo è successivo a vecchio, tenendo conto del giro a 65536."""
    return 0 < (nuovo - vecchio) % 65536 < 32768


def trova_porta_seriale():
    porte = sorted(glob.glob("/dev/ttyUSB*") + glob.glob("/dev/ttyACM*"))
    return porte[0] if porte else None


def leggi_rssi():
    try:
        out = subprocess.run(["iw", "dev", "wlan0", "link"], capture_output=True, text=True, timeout=1).stdout
        m = re.search(r"signal:\s*(-?\d+)", out)
        return int(m.group(1)) if m else None
    except Exception:
        return None


def leggi_temperatura():
    try:
        with open("/sys/class/thermal/thermal_zone0/temp") as f:
            return round(int(f.read()) / 1000, 1)
    except Exception:
        return None


class Bridge:
    def __init__(self):
        self.avvio = time.monotonic()

        self.udp = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        self.udp.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        self.udp.bind(("0.0.0.0", PORTA_COMANDI))
        self.udp.setblocking(False)

        self.ser = None
        self.buffer_seriale = b""
        self.arduino_pronto = False

        self.client_quest = None          # (ip, porta) dell'ultimo mittente di comandi
        self.ultimo_seq = None
        self.ultimo_comando_t = 0.0
        self.stop_inviato = True
        self.throttle = self.steer = self.pan = 0

        self.dist_cm = -1
        self.stato = "BOOT"
        self.ultima_tel_mqtt = 0.0
        self.rssi = None
        self.ultimo_rssi = 0.0

        self.ultimo_evento = {}       # nome evento -> istante dell'ultima pubblicazione

        self.mqtt = None
        self.avvia_mqtt()

    # ------------------------------------------------------------ MQTT

    def avvia_mqtt(self):
        if mqtt is None:
            log.warning("paho-mqtt non installato: MQTT disattivato")
            return
        try:
            client = mqtt.Client(mqtt.CallbackAPIVersion.VERSION2, client_id="arkart-bridge")
        except AttributeError:  # paho-mqtt 1.x
            client = mqtt.Client(client_id="arkart-bridge")
        client.will_set(TOPIC_STATO, "offline", qos=1, retain=True)
        client.on_connect = self.mqtt_connesso
        client.on_message = self.mqtt_messaggio
        client.reconnect_delay_set(min_delay=1, max_delay=10)
        client.connect_async(MQTT_HOST, MQTT_PORT, keepalive=10)
        client.loop_start()
        self.mqtt = client

    def mqtt_connesso(self, client, userdata, flags, reason_code, properties=None):
        log.info("MQTT connesso a %s:%s", MQTT_HOST, MQTT_PORT)
        client.publish(TOPIC_STATO, "online", qos=1, retain=True)
        client.subscribe(TOPIC_CONFIG, qos=1)

    def mqtt_messaggio(self, client, userdata, msg):
        if msg.topic != TOPIC_CONFIG:
            return
        try:
            cfg = json.loads(msg.payload)
            max_speed = max(0, min(255, int(cfg.get("max_speed", 255))))
            stop_cm = max(5, min(29, int(cfg.get("stop_cm", 15))))
        except (ValueError, TypeError, AttributeError):
            log.warning("Configurazione non valida: %r", msg.payload)
            return
        log.info("Nuova configurazione: max_speed=%d stop_cm=%d", max_speed, stop_cm)
        self.scrivi_seriale(f"P,{max_speed},{stop_cm}")

    def evento(self, nome, **dati):
        ora = time.monotonic()
        if ora - self.ultimo_evento.get(nome, -ANTIRIMBALZO_EVENTI_S) < ANTIRIMBALZO_EVENTI_S:
            return
        self.ultimo_evento[nome] = ora
        self.pubblica(TOPIC_EVENTI, {"event": nome, **dati, "ts": int(time.time() * 1000)})

    def pubblica(self, topic, dati):
        if self.mqtt is not None and self.mqtt.is_connected():
            self.mqtt.publish(topic, json.dumps(dati), qos=0)

    # ------------------------------------------------------------ Seriale

    def apri_seriale(self):
        porta = trova_porta_seriale()
        if porta is None:
            return
        try:
            self.ser = serial.Serial(porta, BAUD, timeout=0)
        except serial.SerialException as e:
            log.warning("Impossibile aprire %s: %s", porta, e)
            self.ser = None
            return
        # l'apertura della porta resetta l'Arduino: si aspetta la riga BOOT
        self.arduino_pronto = False
        self.buffer_seriale = b""
        log.info("Seriale aperta su %s, attendo BOOT dall'Arduino", porta)

    def chiudi_seriale(self):
        if self.ser is not None:
            try:
                self.ser.close()
            except Exception:
                pass
        self.ser = None
        self.arduino_pronto = False
        log.warning("Arduino scollegato")

    def scrivi_seriale(self, riga):
        if self.ser is None or not self.arduino_pronto:
            return
        try:
            self.ser.write((riga + "\n").encode())
        except (serial.SerialException, OSError):
            self.chiudi_seriale()

    def leggi_seriale(self):
        try:
            self.buffer_seriale += self.ser.read(256)
        except (serial.SerialException, OSError):
            self.chiudi_seriale()
            return
        while b"\n" in self.buffer_seriale:
            riga, self.buffer_seriale = self.buffer_seriale.split(b"\n", 1)
            self.gestisci_telemetria(riga.decode(errors="replace").strip())

    def gestisci_telemetria(self, riga):
        m = RE_TELEMETRIA.match(riga)
        if not m:
            return
        dist, stato = int(m.group(2)), m.group(3)

        # qualsiasi riga T valida vuol dire che il firmware gira (BOOT compreso)
        if not self.arduino_pronto:
            log.info("Arduino pronto (%s)", stato)
        self.arduino_pronto = True

        # eventi sulle transizioni di stato
        if stato != self.stato:
            if stato == "STOP":
                self.evento("COLLISION_PREVENTED", distance_cm=dist)
            elif stato == "FAILSAFE" and self.stato != "BOOT":
                self.evento("FAILSAFE")
        self.stato, self.dist_cm = stato, dist

        if self.client_quest is not None:
            try:
                self.udp.sendto((riga + "\n").encode(), (self.client_quest[0], PORTA_TELEMETRIA))
            except OSError:
                pass

    # ------------------------------------------------------------ UDP

    def leggi_udp(self):
        while True:
            try:
                dati, mittente = self.udp.recvfrom(128)
            except BlockingIOError:
                return
            riga = dati.decode(errors="replace").strip()
            m = RE_COMANDO.match(riga)
            if not m:
                continue
            seq, thr, steer, pan = (int(x) for x in m.groups())
            if seq > 65535 or abs(thr) > 255 or abs(steer) > 255 or abs(pan) > 45:
                continue
            # dopo una pausa lunga (pilota riavviato) la numerazione può ripartire da capo:
            # la si considera una nuova sessione e si accetta qualsiasi seq
            nuova_sessione = time.monotonic() - self.ultimo_comando_t > NUOVA_SESSIONE_S
            if self.ultimo_seq is not None and not nuova_sessione and not seq_piu_recente(seq, self.ultimo_seq):
                continue  # pacchetto vecchio o duplicato
            if self.client_quest != mittente:
                log.info("Nuovo pilota: %s:%d", *mittente)
            self.client_quest = mittente
            self.ultimo_seq = seq
            self.ultimo_comando_t = time.monotonic()
            self.stop_inviato = False
            self.throttle, self.steer, self.pan = thr, steer, pan
            self.scrivi_seriale(riga)

    # ------------------------------------------------------------ Main loop

    def controlla_timeout(self, ora):
        if not self.stop_inviato and ora - self.ultimo_comando_t > TIMEOUT_COMANDI_S:
            seq = ((self.ultimo_seq or 0) + 1) % 65536
            self.scrivi_seriale(f"C,{seq},0,0,0")
            self.stop_inviato = True
            self.throttle = self.steer = self.pan = 0
            log.info("Nessun comando da %.0f ms: stop", TIMEOUT_COMANDI_S * 1000)

    def telemetria_mqtt(self, ora):
        if ora - self.ultimo_rssi > PERIODO_RSSI_S:
            self.ultimo_rssi = ora
            self.rssi = leggi_rssi()
        if ora - self.ultima_tel_mqtt < PERIODO_MQTT_S:
            return
        self.ultima_tel_mqtt = ora
        self.pubblica(TOPIC_TELEMETRIA, {
            "rear_distance_cm": self.dist_cm,
            "throttle": self.throttle,
            "steer": self.steer,
            "pan": self.pan,
            "state": self.stato if self.ser is not None else "DISCONNECTED",
            "rssi_dbm": self.rssi,
            "cpu_temp_c": leggi_temperatura(),
            "uptime_s": int(ora - self.avvio),
        })

    def esegui(self):
        log.info("Bridge avviato: comandi UDP :%d, telemetria UDP :%d", PORTA_COMANDI, PORTA_TELEMETRIA)
        ultimo_tentativo_seriale = 0.0
        while True:
            ora = time.monotonic()
            if self.ser is None and ora - ultimo_tentativo_seriale > 2:
                ultimo_tentativo_seriale = ora
                self.apri_seriale()

            sorgenti = [self.udp] + ([self.ser] if self.ser is not None else [])
            pronti, _, _ = select.select(sorgenti, [], [], 0.02)
            if self.udp in pronti:
                self.leggi_udp()
            if self.ser is not None and self.ser in pronti:
                self.leggi_seriale()

            ora = time.monotonic()
            self.controlla_timeout(ora)
            self.telemetria_mqtt(ora)


def main():
    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(message)s")
    bridge = Bridge()
    try:
        bridge.esegui()
    except KeyboardInterrupt:
        pass
    finally:
        bridge.scrivi_seriale("C,0,0,0,0")
        if bridge.mqtt is not None:
            bridge.mqtt.publish(TOPIC_STATO, "offline", qos=1, retain=True)
            bridge.mqtt.loop_stop()


if __name__ == "__main__":
    main()
