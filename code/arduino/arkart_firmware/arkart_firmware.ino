// kARt — Firmware Arduino (Sensor / Actuation layer)
//
// Protocollo seriale (115200 baud, righe ASCII terminate da '\n'):
//   Ingresso  C,<seq>,<throttle>,<steer>,<pan>   comando di guida (throttle/steer -255..255, pan -45..45)
//             P,<max_speed>,<stop_cm>            configurazione (max_speed 0..255, stop_cm 5..100)
//   Uscita    T,<seq>,<dist_cm>,<stato>          telemetria a ~20 Hz (dist_cm = -1 se nessuna eco)
//             stato: BOOT, OK, WARN, STOP, FAILSAFE
//
// Sicurezza:
//   1. Watchdog comandi: nessun comando valido per > 300 ms -> motori fermi, FAILSAFE.
//      Si riparte solo con un comando a throttle 0 (niente ripartenze improvvise).
//   2. Frenata in retromarcia: sensore posteriore < stop_cm -> STOP; < 30 cm -> velocità ridotta (WARN).
//   3. Watchdog hardware AVR (500 ms): se il firmware si blocca, l'Arduino si resetta a motori fermi.

#include <Servo.h>
#include <avr/wdt.h>

// --- Pin (cablaggio reale, vedi project.md §6.2) ---
const uint8_t ENA = 6, IN1 = 11, IN2 = 10;   // motore A = destro
const uint8_t ENB = 5, IN3 = 9,  IN4 = 8;    // motore B = sinistro
const uint8_t TRIG = A0, ECHO = A1;          // HC-SR04 posteriore
const uint8_t PIN_SERVO = 3;                 // pan camera

// --- Taratura ---
const bool INVERTI_A = false;
const bool INVERTI_B = true;      // motore sinistro montato a specchio
const int  DEADZONE = 8;          // comandi più piccoli di così = fermo
const int  MIN_PWM = 70;          // sotto questo i motori TT non partono [da tarare]
const int  SERVO_CENTRO = 127;    // servo con la camera dritta (misurato)
const int  PAN_MAX = 45;
const int  SERVO_PASSO = 3;       // gradi ogni 10 ms = 300 gradi/s

// --- Sicurezza ---
const unsigned long TIMEOUT_CMD_MS = 300;
const int WARN_CM = 30;
const int ISTERESI_CM = 3;        // si esce dallo STOP solo sopra stop_cm + 3 (niente tremolio al limite)
int stopCm = 15;                  // modificabile con P
int maxSpeed = 255;               // modificabile con P

// --- Sensore ---
const unsigned long PERIODO_SENSORE_MS = 50;   // 20 Hz, anche frequenza della telemetria
const unsigned long ECO_TIMEOUT_US = 12000;    // circa 2 m: oltre = nessun ostacolo

enum Stato : uint8_t { BOOT, OK, WARN, STOP, FAILSAFE };
const char* const NOMI_STATO[] = { "BOOT", "OK", "WARN", "STOP", "FAILSAFE" };

Servo servo;

// Ultimo comando ricevuto
bool haComando = false;
bool bloccato = true;             // dopo un FAILSAFE serve un comando a throttle 0 per ripartire
unsigned long ultimoComandoMs = 0;
uint16_t ultimoSeq = 0;
int cmdThrottle = 0, cmdSteer = 0, panTarget = 0;

// Stato
Stato stato = FAILSAFE;
int distCm = -1;
int letture[3] = { -1, -1, -1 };
uint8_t idxLettura = 0;
int panAttuale = 0;
unsigned long ultimoSensoreMs = 0, ultimoServoMs = 0;

char buf[40];
uint8_t lenBuf = 0;

// ---------------------------------------------------------------- Motori

// v: -255..255 (positivo = avanti)
void motore(uint8_t en, uint8_t a, uint8_t b, bool inverti, int v) {
  if (inverti) v = -v;
  if (abs(v) < DEADZONE) {
    digitalWrite(a, LOW);
    digitalWrite(b, LOW);
    analogWrite(en, 0);
    return;
  }
  int pwm = map(abs(v), DEADZONE, 255, MIN_PWM, 255);
  digitalWrite(a, v > 0);
  digitalWrite(b, v < 0);
  analogWrite(en, pwm);
}

void motoriStop() {
  motore(ENA, IN1, IN2, INVERTI_A, 0);
  motore(ENB, IN3, IN4, INVERTI_B, 0);
}

// ---------------------------------------------------------------- Sensore

int misuraCm() {
  digitalWrite(TRIG, LOW);
  delayMicroseconds(2);
  digitalWrite(TRIG, HIGH);
  delayMicroseconds(10);
  digitalWrite(TRIG, LOW);
  unsigned long durata = pulseIn(ECHO, HIGH, ECO_TIMEOUT_US);
  return durata == 0 ? -1 : (int)(durata / 58);
}

// Mediana delle ultime 3 letture: scarta le misure sbagliate isolate.
// "Nessuna eco" (-1) conta come distanza infinita.
void aggiornaDistanza() {
  letture[idxLettura] = misuraCm();
  idxLettura = (idxLettura + 1) % 3;

  int v[3];
  for (uint8_t i = 0; i < 3; i++) v[i] = letture[i] < 0 ? 32767 : letture[i];
  int mediana = max(min(v[0], v[1]), min(max(v[0], v[1]), v[2]));
  distCm = mediana == 32767 ? -1 : mediana;
}

// ---------------------------------------------------------------- Logica di guida

void applicaComando() {
  unsigned long ora = millis();

  // 1. Watchdog comandi
  if (!haComando || ora - ultimoComandoMs > TIMEOUT_CMD_MS) {
    bloccato = true;
    stato = FAILSAFE;
    panTarget = 0;
    motoriStop();
    return;
  }
  if (bloccato) {
    if (cmdThrottle != 0) {
      stato = FAILSAFE;
      motoriStop();
      return;
    }
    bloccato = false;
  }

  // 2. Frenata in retromarcia
  long thr = cmdThrottle;
  // isteresi: se eravamo già in STOP, la soglia per uscirne è un po' più lontana
  int sogliaStop = (stato == STOP) ? stopCm + ISTERESI_CM : stopCm;
  stato = OK;
  if (thr < 0 && distCm >= 0) {
    if (distCm < sogliaStop) {
      thr = 0;
      stato = STOP;
    } else if (distCm < WARN_CM) {
      // velocità in retromarcia dal 30% (vicino) al 100% (a 30 cm)
      long perc = 30 + 70L * (distCm - stopCm) / (WARN_CM - stopCm);
      thr = thr * perc / 100;
      stato = WARN;
    }
  }

  // Mixing differenziale
  long sx = thr + cmdSteer;
  long dx = thr - cmdSteer;
  long m = max(abs(sx), abs(dx));
  if (m > 255) {
    sx = sx * 255 / m;
    dx = dx * 255 / m;
  }
  sx = sx * maxSpeed / 255;
  dx = dx * maxSpeed / 255;

  motore(ENB, IN3, IN4, INVERTI_B, (int)sx);
  motore(ENA, IN1, IN2, INVERTI_A, (int)dx);
}

// ---------------------------------------------------------------- Servo

void aggiornaServo(unsigned long ora) {
  if (ora - ultimoServoMs < 10) return;
  ultimoServoMs = ora;
  if (panAttuale == panTarget) return;
  if (panAttuale < panTarget) panAttuale = min(panAttuale + SERVO_PASSO, panTarget);
  else panAttuale = max(panAttuale - SERVO_PASSO, panTarget);
  // sul servo gli angoli minori girano a destra: pan negativo = sinistra
  servo.write(SERVO_CENTRO - panAttuale);
}

// ---------------------------------------------------------------- Seriale

// Legge n interi separati da virgola; false se la riga non è valida
bool leggiNumeri(const char* s, long* out, uint8_t n) {
  for (uint8_t i = 0; i < n; i++) {
    char* fine;
    out[i] = strtol(s, &fine, 10);
    if (fine == s) return false;
    if (i < n - 1) {
      if (*fine != ',') return false;
      s = fine + 1;
    } else if (*fine != '\0') {
      return false;
    }
  }
  return true;
}

void elaboraRiga(const char* r) {
  if (r[0] == 'C' && r[1] == ',') {
    long v[4];
    if (!leggiNumeri(r + 2, v, 4)) return;
    if (v[0] < 0 || v[0] > 65535) return;
    if (abs(v[1]) > 255 || abs(v[2]) > 255 || abs(v[3]) > PAN_MAX) return;
    ultimoSeq = (uint16_t)v[0];
    cmdThrottle = (int)v[1];
    cmdSteer = (int)v[2];
    panTarget = (int)v[3];
    ultimoComandoMs = millis();
    haComando = true;
    applicaComando();
  } else if (r[0] == 'P' && r[1] == ',') {
    long v[2];
    if (!leggiNumeri(r + 2, v, 2)) return;
    maxSpeed = constrain(v[0], 0, 255);
    stopCm = constrain(v[1], 5, WARN_CM - 1);
  }
}

void leggiSeriale() {
  while (Serial.available()) {
    char c = Serial.read();
    if (c == '\r') continue;
    if (c == '\n') {
      buf[lenBuf] = '\0';
      elaboraRiga(buf);
      lenBuf = 0;
    } else if (lenBuf < sizeof(buf) - 1) {
      buf[lenBuf++] = c;
    } else {
      lenBuf = 0;   // riga troppo lunga: la scarto
    }
  }
}

void inviaTelemetria() {
  Serial.print(F("T,"));
  Serial.print(ultimoSeq);
  Serial.print(',');
  Serial.print(distCm);
  Serial.print(',');
  Serial.println(NOMI_STATO[stato]);
}

// ---------------------------------------------------------------- Main

void setup() {
  wdt_disable();

  pinMode(ENA, OUTPUT); pinMode(IN1, OUTPUT); pinMode(IN2, OUTPUT);
  pinMode(ENB, OUTPUT); pinMode(IN3, OUTPUT); pinMode(IN4, OUTPUT);
  pinMode(TRIG, OUTPUT); pinMode(ECHO, INPUT);
  motoriStop();

  servo.attach(PIN_SERVO);
  servo.write(SERVO_CENTRO);

  Serial.begin(115200);
  Serial.println(F("T,0,-1,BOOT"));

  wdt_enable(WDTO_500MS);
}

void loop() {
  wdt_reset();
  leggiSeriale();

  unsigned long ora = millis();
  if (ora - ultimoSensoreMs >= PERIODO_SENSORE_MS) {
    ultimoSensoreMs = ora;
    aggiornaDistanza();
    applicaComando();     // ricontrolla timeout e distanza anche senza nuovi comandi
    inviaTelemetria();
  }
  aggiornaServo(ora);
}
