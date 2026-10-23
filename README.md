# kARt — Kart robotico FPV in Mixed Reality

Progetto per l'esame di **Lab of IoT** — Università degli Studi di Salerno, A.A. 2026/2027 (Prof. Massimo Ficco).

Un kart robotico guidato in tempo reale da un **Meta Quest 3S** in Mixed Reality: grazie al passthrough il pilota vede la stanza reale, con davanti un pannello video in prima persona (FPV) dalla camera di bordo e un HUD con la telemetria. Il sistema segue l'architettura IoT a tre livelli:

| Livello | Nodo | Ruolo |
| :--- | :--- | :--- |
| **Sensor / Actuation** | Arduino Uno R3 | Motori (L298N), sensore di retromarcia HC-SR04, servo della camera, watchdog e frenata automatica |
| **Gateway** | Raspberry Pi Zero W | Bridge UDP ↔ seriale, broker MQTT Mosquitto, streaming della camera |
| **Application** | Meta Quest 3S (Unity) + Node-RED | Guida dai controller, pannello FPV, HUD, statistiche e dashboard |

La guida viaggia su **UDP** (30 Hz, bassa latenza); telemetria, eventi e configurazione su **MQTT**.

📄 Documento di progetto completo: [project.md](project.md)

## Struttura del repository

```
├── project.md                  # documento di progetto (architettura, protocollo, misure, decisioni)
├── code/
│   ├── arduino/
│   │   ├── arkart_firmware/    # firmware del kart
│   │   └── test_*/             # sketch di prova di motori, sensore e servo
│   ├── pi/                     # bridge Python, streaming camera MJPEG, servizi systemd, broker MQTT, installazione automatica
│   ├── kARt/                   # progetto Unity 6 per Meta Quest 3S
│   ├── nodered/                # dashboard Node-RED (flows.json)
│   └── tools/                  # console di guida da tastiera (prove senza visore)
└── docs/                       # presentazione, video demo, copertina (in arrivo)
```

## Come si avvia

**Arduino** — caricare `code/arduino/arkart_firmware/arkart_firmware.ino` con l'Arduino IDE.

**Raspberry Pi** — scrivere la microSD con Raspberry Pi OS Lite (32-bit) tramite Raspberry Pi Imager, abilitando SSH e Wi-Fi, poi dalla cartella `code/pi`:

```bash
ssh <utente>@<pi> 'mkdir -p ~/arkart'
scp arkart_bridge.py arkart-bridge.service arkart_camera.py arkart-camera.service mosquitto-arkart.conf setup_pi.sh <utente>@<pi>:~/arkart/
ssh -t <utente>@<pi> 'bash ~/arkart/setup_pi.sh'
```

Lo script installa i pacchetti, il broker MQTT, il bridge e lo streaming della camera come servizi avviati al boot.

**Prova senza visore** — dal computer, sulla stessa rete della Pi:

```bash
cd code/tools
python3 -m venv .venv && .venv/bin/pip install pyserial
.venv/bin/python kart_console.py --udp <indirizzo-della-pi>
```

**Quest** — aprire `code/kARt` con Unity 6 (modulo Android), usare il menu **kARt** per creare l'interfaccia e impostare il progetto, poi *Build And Run* sul visore.

## Stato

- ✅ Kart: motori, sterzata differenziale, sensore di retromarcia, servo della camera
- ✅ Firmware con watchdog dei comandi, frenata automatica in retromarcia e watchdog hardware
- ✅ Bridge sulla Pi con telemetria ed eventi MQTT, guida via Wi-Fi verificata
- 🚧 App Unity per il Quest (interfaccia pronta, prove sul visore in corso)
- ⏳ Camera e streaming video FPV
- ✅ Dashboard Node-RED (`code/nodered`, con simulatore per le prove senza kart)
- ⏳ Misure sperimentali, presentazione

## Autore

**Enrico Madonna** — Università degli Studi di Salerno
