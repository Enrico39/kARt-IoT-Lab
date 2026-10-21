// kARt — Test motori (L298N)
// Apri il Monitor Seriale a 115200 baud e scrivi un comando + Invio:
//   a = motore A (destro) avanti     z = motore A indietro
//   k = motore B (sinistro) avanti   m = motore B indietro
//   w = tutti e due avanti  x = tutti e due indietro
//   s = stop
// Per sicurezza ogni comando dura 1 secondo, poi i motori si fermano da soli.

const int ENA = 6;   // PWM motore A = DESTRO (OUT1/OUT2)
const int IN1 = 11;
const int IN2 = 10;
const int ENB = 5;   // PWM motore B = SINISTRO (OUT3/OUT4)
const int IN3 = 9;
const int IN4 = 8;

const int VELOCITA = 180;            // 0-255
const unsigned long DURATA_MS = 1000;

// I motori sono montati a specchio: se un motore gira al contrario, metti true
const bool INVERTI_A = false;
const bool INVERTI_B = true;    // motore B (sinistro) montato a specchio

unsigned long inizioComando = 0;
bool inMovimento = false;

// dir: +1 avanti, -1 indietro, 0 fermo
void motoreA(int dir) {
  if (INVERTI_A) dir = -dir;
  digitalWrite(IN1, dir > 0);
  digitalWrite(IN2, dir < 0);
  analogWrite(ENA, dir == 0 ? 0 : VELOCITA);
}

void motoreB(int dir) {
  if (INVERTI_B) dir = -dir;
  digitalWrite(IN3, dir > 0);
  digitalWrite(IN4, dir < 0);
  analogWrite(ENB, dir == 0 ? 0 : VELOCITA);
}

void stop() {
  motoreA(0);
  motoreB(0);
  inMovimento = false;
}

void setup() {
  pinMode(ENA, OUTPUT); pinMode(IN1, OUTPUT); pinMode(IN2, OUTPUT);
  pinMode(ENB, OUTPUT); pinMode(IN3, OUTPUT); pinMode(IN4, OUTPUT);
  stop();
  Serial.begin(115200);
  Serial.println(F("kARt test motori pronto."));
  Serial.println(F("a/z = motore A avanti/indietro, k/m = motore B, w/x = entrambi, s = stop"));
}

void loop() {
  if (Serial.available()) {
    char c = Serial.read();
    if (c == '\n' || c == '\r') return;
    switch (c) {
      case 'a': motoreA(+1); Serial.println(F("Motore A avanti")); break;
      case 'z': motoreA(-1); Serial.println(F("Motore A indietro")); break;
      case 'k': motoreB(+1); Serial.println(F("Motore B avanti")); break;
      case 'm': motoreB(-1); Serial.println(F("Motore B indietro")); break;
      case 'w': motoreA(+1); motoreB(+1); Serial.println(F("Entrambi avanti")); break;
      case 'x': motoreA(-1); motoreB(-1); Serial.println(F("Entrambi indietro")); break;
      case 's': stop(); Serial.println(F("Stop")); return;
      default:  Serial.println(F("Comando sconosciuto")); return;
    }
    inMovimento = true;
    inizioComando = millis();
  }

  if (inMovimento && millis() - inizioComando > DURATA_MS) {
    stop();
    Serial.println(F("(stop automatico)"));
  }
}
