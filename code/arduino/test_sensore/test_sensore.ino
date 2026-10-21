// kARt — Test sensore a ultrasuoni HC-SR04 (posteriore)
// Apri il Monitor Seriale a 115200 baud: stampa la distanza 5 volte al secondo.
// Prova ad avvicinare e allontanare la mano davanti al sensore.

const int TRIG = A0;   // filo bianco
const int ECHO = A1;   // filo grigio

const unsigned long TIMEOUT_US = 25000;  // circa 4 m: oltre si considera "nessun ostacolo"

// Restituisce la distanza in cm, oppure -1 se non torna nessuna eco
long misuraCm() {
  digitalWrite(TRIG, LOW);
  delayMicroseconds(2);
  digitalWrite(TRIG, HIGH);
  delayMicroseconds(10);
  digitalWrite(TRIG, LOW);

  unsigned long durata = pulseIn(ECHO, HIGH, TIMEOUT_US);
  if (durata == 0) return -1;
  return durata / 58;
}

void setup() {
  pinMode(TRIG, OUTPUT);
  pinMode(ECHO, INPUT);
  Serial.begin(115200);
  Serial.println(F("kARt test sensore pronto."));
}

void loop() {
  long cm = misuraCm();
  if (cm < 0) {
    Serial.println(F("Nessun ostacolo (oltre 4 m o eco persa)"));
  } else {
    Serial.print(F("Distanza: "));
    Serial.print(cm);
    Serial.print(F(" cm  "));
    // barra grafica: un # ogni 5 cm, fino a 1 m
    for (long i = 0; i < min(cm, 100L) / 5; i++) Serial.print('#');
    if (cm < 15) Serial.print(F("  <-- STOP"));
    else if (cm < 30) Serial.print(F("  <-- attenzione"));
    Serial.println();
  }
  delay(200);
}
