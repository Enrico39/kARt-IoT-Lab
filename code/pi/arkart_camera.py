#!/usr/bin/env python3
"""kARt — streaming della camera di bordo (pipeline A di project.md §5.4).

    Camera IMX219 --picamera2 + MJPEGEncoder (HW)--> HTTP multipart :8080 --> Quest

Endpoint:
    /stream.mjpg   flusso MJPEG (multipart/x-mixed-replace), un JPEG per parte
    /snapshot.jpg  ultimo fotogramma

Gira come servizio separato dal bridge: se la camera si blocca, la guida continua.
"""

import io
import logging
import os
import socket
import socketserver
import threading
from http import server

from libcamera import Transform
from picamera2 import Picamera2
from picamera2.encoders import MJPEGEncoder
from picamera2.outputs import FileOutput

# --- Configurazione (modificabile da variabili d'ambiente) ---
PORTA_HTTP = int(os.environ.get("ARKART_VIDEO_PORT", 8080))
LARGHEZZA = int(os.environ.get("ARKART_VIDEO_WIDTH", 640))
ALTEZZA = int(os.environ.get("ARKART_VIDEO_HEIGHT", 480))
FPS = int(os.environ.get("ARKART_VIDEO_FPS", 30))
BITRATE = int(os.environ.get("ARKART_VIDEO_BITRATE", 6_000_000))
RUOTA_180 = os.environ.get("ARKART_VIDEO_ROTATE_180", "0") == "1"   # camera montata capovolta

CONFINE = "arkartframe"

log = logging.getLogger("arkart-camera")


class UltimoFotogramma(io.BufferedIOBase):
    """Riceve i JPEG dall'encoder e sveglia i client in attesa.
    Si tiene solo l'ultimo: un client lento salta fotogrammi invece di accumulare ritardo."""

    def __init__(self):
        self.frame = None
        self.condizione = threading.Condition()

    def write(self, buf):
        with self.condizione:
            self.frame = bytes(buf)
            self.condizione.notify_all()
        return len(buf)

    def attendi(self, timeout=2.0):
        with self.condizione:
            self.condizione.wait(timeout)
            return self.frame


uscita = UltimoFotogramma()


class Gestore(server.BaseHTTPRequestHandler):
    def do_GET(self):
        if self.path == "/stream.mjpg":
            self.invia_stream()
        elif self.path == "/snapshot.jpg":
            frame = uscita.attendi()
            if frame is None:
                self.send_error(503, "Camera non pronta")
                return
            self.send_response(200)
            self.send_header("Content-Type", "image/jpeg")
            self.send_header("Content-Length", str(len(frame)))
            self.end_headers()
            self.wfile.write(frame)
        else:
            self.send_error(404)

    def invia_stream(self):
        self.send_response(200)
        self.send_header("Cache-Control", "no-cache, private")
        self.send_header("Pragma", "no-cache")
        self.send_header("Content-Type", f"multipart/x-mixed-replace; boundary={CONFINE}")
        self.end_headers()
        # una sola write per fotogramma e niente Nagle: con tre write piccole il ritardo
        # degli ACK del client dimezzava gli FPS sul Wi-Fi
        self.connection.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
        log.info("Client video collegato: %s", self.client_address[0])
        try:
            while True:
                frame = uscita.attendi()
                if frame is None:
                    continue
                intestazione = f"--{CONFINE}\r\nContent-Type: image/jpeg\r\nContent-Length: {len(frame)}\r\n\r\n"
                self.wfile.write(intestazione.encode() + frame + b"\r\n")
        except (BrokenPipeError, ConnectionResetError, TimeoutError):
            log.info("Client video scollegato: %s", self.client_address[0])

    def log_message(self, fmt, *args):
        pass  # niente log per ogni richiesta


class Server(socketserver.ThreadingMixIn, server.HTTPServer):
    allow_reuse_address = True
    daemon_threads = True


def main():
    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(message)s")
    camera = Picamera2()
    durata_us = int(1_000_000 / FPS)
    config = camera.create_video_configuration(
        main={"size": (LARGHEZZA, ALTEZZA)},
        transform=Transform(hflip=RUOTA_180, vflip=RUOTA_180),
        controls={"FrameDurationLimits": (durata_us, durata_us)},
        buffer_count=4,   # pochi buffer: meno RAM sulla Zero e meno latenza
    )
    camera.configure(config)
    camera.start_recording(MJPEGEncoder(bitrate=BITRATE), FileOutput(uscita))
    log.info("Camera avviata: %dx%d @ %d FPS, %.1f Mbps", LARGHEZZA, ALTEZZA, FPS, BITRATE / 1e6)
    try:
        with Server(("", PORTA_HTTP), Gestore) as srv:
            log.info("Stream su http://0.0.0.0:%d/stream.mjpg", PORTA_HTTP)
            srv.serve_forever()
    except KeyboardInterrupt:
        pass
    finally:
        camera.stop_recording()


if __name__ == "__main__":
    main()
