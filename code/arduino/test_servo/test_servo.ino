// kARt — Test servo SG90 (pan della camera)
// Apri il Monitor Seriale a 115200 baud e scrivi un comando + Invio:
//   c        = centro (camera dritta)
//   s        = tutto a sinistra (visto da dietro)
//   d        = tutto a destra
//   g        = giro completo lento da un lato all'altro e ritorno al centro
//   un numero da -45 a 45 = vai a quell'angolo rispetto al centro (negativo = sinistra, es. -20)

#include <Servo.h>

const int PIN_SERVO = 3;     // filo arancione
const int CENTRO = 127;      // angolo del servo con la sbarretta dritta in avanti (misurato)
const int ESCURSIONE = 45;   // gradi massimi per lato: servo tra 82 e 172

// Sul servo gli angoli minori girano a destra (visto da dietro):
// invertiamo il segno così pan negativo = sinistra, pan positivo = destra
int angoloServo(int pan) { return CENTRO - pan; }

Servo servo;

// pan: angolo rispetto al centro, da -ESCURSIONE a +ESCURSIONE
void vaiA(int pan) {
  pan = constrain(pan, -ESCURSIONE, ESCURSIONE);
  servo.write(angoloServo(pan));
  Serial.print(F("Pan "));
  Serial.print(pan);
  Serial.print(F(" gradi (servo a "));
  Serial.print(angoloServo(pan));
  Serial.println(F(")"));
}

void giro() {
  Serial.println(F("Giro lento..."));
  for (int p = -ESCURSIONE; p <= ESCURSIONE; p++) { servo.write(angoloServo(p)); delay(15); }
  for (int p = ESCURSIONE; p >= -ESCURSIONE; p--) { servo.write(angoloServo(p)); delay(15); }
  vaiA(0);
}

void setup() {
  Serial.begin(115200);
  servo.attach(PIN_SERVO);
  Serial.println(F("kARt test servo pronto."));
  Serial.println(F("c = centro, s / d = lati, g = giro, oppure un angolo da -45 a 45"));
  vaiA(0);
}

void loop() {
  if (!Serial.available()) return;

  String riga = Serial.readStringUntil('\n');
  riga.trim();
  if (riga.length() == 0) return;

  if (riga == "c") vaiA(0);
  else if (riga == "s") vaiA(-ESCURSIONE);
  else if (riga == "d") vaiA(ESCURSIONE);
  else if (riga == "g") giro();
  else if (isDigit(riga.charAt(0)) || riga.charAt(0) == '-') vaiA(riga.toInt());
  else Serial.println(F("Comando sconosciuto"));
}
