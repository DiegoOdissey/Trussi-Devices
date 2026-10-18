// ─────────────────────────────────────────────
//  TrussiRing Firmware v1.0
//  ESP32-C3 SuperMini
//
//  Part of the AxiDeck / TrussiApp ecosystem.
//  4x CherryMX switches behave exactly like AxiDeck's.
//  EC11 encoder: rotate = adjust volume of the selected
//  target, press = open the application-selection menu.
// ─────────────────────────────────────────────

#include <Wire.h>
#include <U8g2lib.h>
#include <Preferences.h>
#include <math.h>

// ─────────────────────────────────────────────
//  PINS
// ─────────────────────────────────────────────
#define ENCODER_A_PIN     1
#define ENCODER_B_PIN     0
#define ENCODER_BTN_PIN   3   // ASSUMPTION: S1->GND, S2->this pin (INPUT_PULLUP, LOW = pressed)
#define LED_PIN            4
// Physical switch pins — remapped to match corrected wiring:
// old BTN2->new BTN1, old BTN1->new BTN2, old BTN4->new BTN3, old BTN3->new BTN4
#define SW1_PIN             7   // was SW2
#define SW2_PIN            10   // was SW1
#define SW3_PIN             5   // was SW4
#define SW4_PIN             6   // was SW3
#define OLED_SDA_PIN        8
#define OLED_SCL_PIN        9

const int SW_PINS[4] = { SW1_PIN, SW2_PIN, SW3_PIN, SW4_PIN };

// ─────────────────────────────────────────────
//  CONFIG
// ─────────────────────────────────────────────
const unsigned long APP_SELECT_TIMEOUT    = 5000;   // ms — app menu cancel
const unsigned long APP_SEARCH_TIMEOUT    = 30000;  // ms — fallback to Master
const unsigned long LED_BLINK_PERIOD      = 2000;   // ms — waiting-for-PC blink
const unsigned long LED_BREATH_PERIOD     = 4000;   // ms — connected breathing
const unsigned long VOLUME_SCREEN_TIMEOUT = 1500;   // ms — revert to idle after last turn
const unsigned long PING_INTERVAL         = 5000;   // ms
const unsigned long PING_TIMEOUT          = 12000;  // ms
const unsigned long DEBOUNCE_MS           = 40;     // switches + encoder button
const int           MAX_APPS              = 20;
const int           ENCODER_STEPS_PER_CLICK = 4;    // EC11 = 4 transitions per detent
const int           LED_PWM_FREQ          = 5000;
const int           LED_PWM_RES           = 8;      // bits

// ─────────────────────────────────────────────
//  STATE MACHINE
// ─────────────────────────────────────────────
enum DeviceState {
  ST_BOOT,
  ST_WAITING_FOR_PC,
  ST_CONNECTING,          // reserved — handshake here is single-step, rarely visited
  ST_CONNECTED,           // transient — immediately resolves into IDLE or APPLICATION_SEARCH
  ST_IDLE,
  ST_APPLICATION_SEARCH,
  ST_APPLICATION_MENU,
  ST_VOLUME_CONTROL,
  ST_MASTER_VOLUME_FALLBACK
};

DeviceState state          = ST_BOOT;
DeviceState stateBeforeMenu = ST_IDLE;

// Placeholder functions
void onEncoderPress();
void selectApp(int index);
int findAppIndex(String name);

// ─────────────────────────────────────────────
//  OLED  (u8g2, hardware I2C, custom pins via Wire.begin)
// ─────────────────────────────────────────────
U8G2_SSD1306_128X64_NONAME_F_HW_I2C u8g2(U8G2_R0, /* reset=*/ U8X8_PIN_NONE);

// ─────────────────────────────────────────────
//  PERSISTENCE
// ─────────────────────────────────────────────
Preferences prefs;

// ─────────────────────────────────────────────
//  GLOBAL STATE
// ─────────────────────────────────────────────
String currentTime  = "00:00";
String currentDate  = "";

String  trackTitle    = "";
String  trackArtist   = "";
String  trackDuration = "";
int     trackProgress = 0;
bool    musicActive   = false;

String appList[MAX_APPS];
int    appCount        = 0;
String activeAppName   = "Master";
String savedAppName    = "";

int  menuIndex          = 0;
int  preMenuActiveIndex = 0;

float displayVolume = 50.0f;  // optimistic local estimate, corrected by VOLLVL from PC

bool          searching       = false;
String        searchTargetName = "";
unsigned long searchStartTime  = 0;

unsigned long lastMenuActivity   = 0;
unsigned long lastVolumeActivity = 0;

bool          isConnected      = false;
unsigned long lastPingSent     = 0;
unsigned long lastPongReceived = 0;

bool          screenDirty        = true;
unsigned long lastWaitAnimToggle = 0;
bool          waitAnimFrame      = false;
String        serialLineBuf      = "";

// Switch debounce state
unsigned long swLastChange[4] = {0, 0, 0, 0};
bool          swStable[4]     = {false, false, false, false};
bool          swRaw[4]        = {false, false, false, false};

// Encoder button debounce state
unsigned long encBtnLastChange = 0;
bool          encBtnStable     = false;
bool          encBtnRaw        = false;

// Encoder quadrature decode (interrupt-driven)
volatile int16_t encoderDelta     = 0;
volatile uint8_t encoderPrevState = 0;
int32_t          encoderAccum     = 0;

// Full-step quadrature transition table.
// index = (prevState << 2) | currState  ->  -1 / 0 / +1
// Signs are negated relative to the original to match the physical wiring
// (encoder A and B inputs are electrically swapped on this build).
static const int8_t QUAD_TABLE[16] = {
   0,  1, -1,  0,
  -1,  0,  0,  1,
   1,  0,  0, -1,
   0, -1,  1,  0
};

// ─────────────────────────────────────────────
//  ENCODER  — interrupt-driven quadrature decode
// ─────────────────────────────────────────────
void IRAM_ATTR onEncoderChange() {
  uint8_t a = digitalRead(ENCODER_A_PIN);
  uint8_t b = digitalRead(ENCODER_B_PIN);
  uint8_t currState = (a << 1) | b;
  uint8_t index = (encoderPrevState << 2) | currState;
  encoderDelta += QUAD_TABLE[index];
  encoderPrevState = currState;
}

void handleEncoderRotation(int8_t dir) {
  if (state == ST_APPLICATION_MENU) {
    if (appCount > 0)
      menuIndex = ((menuIndex + dir) % appCount + appCount) % appCount;
    lastMenuActivity = millis();
    screenDirty = true;
    return;
  }

  if (state == ST_IDLE || state == ST_VOLUME_CONTROL ||
      state == ST_MASTER_VOLUME_FALLBACK || state == ST_APPLICATION_SEARCH) {
    Serial.print("VOL:");
    Serial.print(activeAppName);
    Serial.println(dir > 0 ? ":+" : ":-");

    // Optimistic local feedback — corrected later by an incoming VOLLVL message
    displayVolume = constrain(displayVolume + (dir > 0 ? 5.0f : -5.0f), 0.0f, 100.0f);

    state = ST_VOLUME_CONTROL;
    lastVolumeActivity = millis();
    screenDirty = true;
  }
}

void handleEncoderButtonPoll() {
  bool raw = (digitalRead(ENCODER_BTN_PIN) == LOW);
  unsigned long now = millis();

  if (raw != encBtnRaw) { encBtnRaw = raw; encBtnLastChange = now; }

  if ((now - encBtnLastChange) >= DEBOUNCE_MS && raw != encBtnStable) {
    encBtnStable = raw;
    if (raw) onEncoderPress();  // fire only on the press edge
  }
}

void onEncoderPress() {
  if (state == ST_IDLE || state == ST_VOLUME_CONTROL ||
      state == ST_MASTER_VOLUME_FALLBACK || state == ST_APPLICATION_SEARCH) {
    // Enter application menu — remember where we came from so a
    // cancelled/timed-out menu resumes exactly what was happening before.
    stateBeforeMenu     = state;
    preMenuActiveIndex  = findAppIndex(activeAppName);
    menuIndex           = (preMenuActiveIndex >= 0) ? preMenuActiveIndex : 0;
    state               = ST_APPLICATION_MENU;
    lastMenuActivity    = millis();
    screenDirty         = true;
  }
  else if (state == ST_APPLICATION_MENU) {
    // Confirm selection — this is the only action that cancels a pending search
    if (appCount > 0) {
      selectApp(menuIndex);
      searching = false;
    }
    state = ST_IDLE;
    screenDirty = true;
  }
}

// ─────────────────────────────────────────────
//  CHERRYMX SWITCHES  — identical behavior to AxiDeck
// ─────────────────────────────────────────────
void handleSwitches() {
  unsigned long now = millis();
  for (int i = 0; i < 4; i++) {
    bool raw = (digitalRead(SW_PINS[i]) == LOW);  // GND-wired, pulled up -> LOW = pressed

    if (raw != swRaw[i]) { swRaw[i] = raw; swLastChange[i] = now; }

    if ((now - swLastChange[i]) >= DEBOUNCE_MS && raw != swStable[i]) {
      swStable[i] = raw;
      Serial.print("BTN:");
      Serial.print(i + 1);
      Serial.println(raw ? ":DOWN" : ":UP");
    }
  }
}

// ─────────────────────────────────────────────
//  STATUS LED  — blink while waiting, breathe while connected
// ─────────────────────────────────────────────
bool isOperationalState(DeviceState s) {
  return s == ST_IDLE || s == ST_APPLICATION_SEARCH || s == ST_APPLICATION_MENU ||
         s == ST_VOLUME_CONTROL || s == ST_MASTER_VOLUME_FALLBACK || s == ST_CONNECTED;
}

void updateLED() {
  if (isOperationalState(state)) {
    float phase = (millis() % LED_BREATH_PERIOD) / (float)LED_BREATH_PERIOD * 2.0f * PI;
    uint8_t duty = (uint8_t)(((sinf(phase - PI / 2.0f) + 1.0f) / 2.0f) * 255.0f);
    ledcWrite(0, duty);
  } else if (state == ST_WAITING_FOR_PC || state == ST_CONNECTING) {
    bool on = (millis() % LED_BLINK_PERIOD) < (LED_BLINK_PERIOD / 2);
    ledcWrite(0, on ? 255 : 0);
  } else {
    ledcWrite(0, 0);  // BOOT — LED off, boot screen speaks for itself
  }
}

// ─────────────────────────────────────────────
//  HELPERS
// ─────────────────────────────────────────────
String truncateToFit(String s, int maxChars) {
  if ((int)s.length() <= maxChars) return s;
  return s.substring(0, maxChars);
}

int findAppIndex(String name) {
  for (int i = 0; i < appCount; i++)
    if (appList[i] == name) return i;
  return -1;
}

void selectApp(int index) {
  if (index < 0 || index >= appCount) return;
  activeAppName = appList[index];
  displayVolume = 50.0f;  // reset optimistic estimate until PC corrects it

  if (activeAppName != savedAppName) {
    savedAppName = activeAppName;
    prefs.putString("selApp", savedAppName);  // only write on actual change
  }

  Serial.print("[TrussiRing] Active target -> ");
  Serial.println(activeAppName);
  Serial.print("APPSELECT:");
  Serial.println(activeAppName);  // inform PC which target is now active
}

// ─────────────────────────────────────────────
//  APPLICATION LIST  (from TrussiApp)
// ─────────────────────────────────────────────
void updateAppList(String payload) {
  appCount = 0;
  int start = 0;
  while (start <= (int)payload.length() && appCount < MAX_APPS) {
    int sep = payload.indexOf('|', start);
    String item = (sep < 0) ? payload.substring(start) : payload.substring(start, sep);
    item.trim();
    if (item.length() > 0) appList[appCount++] = item;
    if (sep < 0) break;
    start = sep + 1;
  }

  Serial.print("[TrussiRing] App list updated, count=");
  Serial.println(appCount);

  if (searching) {
    int idx = findAppIndex(searchTargetName);
    if (idx >= 0) {
      Serial.println("[TrussiRing] Search target found — selecting.");
      selectApp(idx);
      searching = false;
      state = ST_IDLE;
    }
  }
  else if ((state == ST_IDLE || state == ST_MASTER_VOLUME_FALLBACK) &&
           activeAppName != "Master" && findAppIndex(activeAppName) < 0) {
    // The currently active app vanished — search for it again rather
    // than immediately dropping to Master (spec section 20).
    Serial.println("[TrussiRing] Active app disappeared — searching again.");
    searching        = true;
    searchTargetName = activeAppName;
    searchStartTime  = millis();
    state            = ST_APPLICATION_SEARCH;
  }

  screenDirty = true;
}

void attemptRestoreOrSearch() {
  int idx = findAppIndex(savedAppName);

  if (savedAppName.length() > 0 && idx >= 0) {
    selectApp(idx);
    state = ST_IDLE;
  }
  else if (savedAppName.length() > 0) {
    searching        = true;
    searchTargetName = savedAppName;
    searchStartTime  = millis();
    state            = ST_APPLICATION_SEARCH;
    Serial.println("[TrussiRing] Saved app not found yet — searching...");
  }
  else {
    int mIdx = findAppIndex("Master");
    selectApp(mIdx >= 0 ? mIdx : 0);
    state = ST_IDLE;
  }
  screenDirty = true;
}

void handleSearchTimeout() {
  if (!searching) return;
  if (millis() - searchStartTime >= APP_SEARCH_TIMEOUT) {
    Serial.println("[TrussiRing] Search timed out — falling back to Master.");
    int mIdx = findAppIndex("Master");
    if (mIdx >= 0) selectApp(mIdx);
    else           activeAppName = "Master";  // no list info at all yet
    searching = false;
    state = ST_MASTER_VOLUME_FALLBACK;
    screenDirty = true;
  }
}

void handleMenuTimeout() {
  if (state != ST_APPLICATION_MENU) return;
  if (millis() - lastMenuActivity >= APP_SELECT_TIMEOUT) {
    Serial.println("[TrussiRing] Menu timed out — cancelling.");
    state = stateBeforeMenu;  // resume idle or a still-pending search, unchanged
    screenDirty = true;
  }
}

void handleVolumeScreenTimeout() {
  if (state != ST_VOLUME_CONTROL) return;
  if (millis() - lastVolumeActivity >= VOLUME_SCREEN_TIMEOUT) {
    state = ST_IDLE;
    screenDirty = true;
  }
}

// ─────────────────────────────────────────────
//  SERIAL PROTOCOL
//
//  PC -> Device:
//    CONNECT                          Handshake
//    PING / PONG                      Keepalive
//    TIME:HH:MM                       Clock
//    DATE:dd Mese yyyy                Date
//    TRACK:title|artist|dur|progress  Now playing (progress 0-100)
//    NOTRACK                          Nothing playing
//    APPLIST:Master|App1|App2|...     Available volume targets
//    VOLLVL:<name>:<percent>          Authoritative volume correction
//
//  Device -> PC:
//    ACK:TRUSSIRING                   Handshake reply, self-identifies
//    PING / PONG                      Keepalive
//    BTN:N:DOWN / BTN:N:UP            Switch events (N = 1-4)
//    VOL:<name>:+ / VOL:<name>:-      Volume delta for active target
//    APPSELECT:<name>                 New active target chosen
// ─────────────────────────────────────────────
void parseSerialLine(String msg) {
  msg.trim();
  if (msg.length() == 0) return;

  if (msg == "CONNECT") {
    isConnected      = true;
    lastPongReceived = millis();
    Serial.println("ACK:TRUSSIRING");
    if (state == ST_WAITING_FOR_PC || state == ST_CONNECTING) {
      state = ST_CONNECTED;
      attemptRestoreOrSearch();
    }
    screenDirty = true;
    return;
  }

  if (msg == "PING") { lastPongReceived = millis(); Serial.println("PONG"); return; }
  if (msg == "PONG") { lastPongReceived = millis(); return; }

  if (msg.startsWith("TIME:")) { currentTime = msg.substring(5); screenDirty = true; return; }
  if (msg.startsWith("DATE:")) { currentDate = msg.substring(5); screenDirty = true; return; }

  if (msg.startsWith("TRACK:")) {
    String p  = msg.substring(6);
    int p1 = p.indexOf('|');
    int p2 = p.indexOf('|', p1 + 1);
    int p3 = p.indexOf('|', p2 + 1);
    if (p1 < 0 || p2 < 0 || p3 < 0) return;

    trackTitle    = p.substring(0, p1);
    trackArtist   = p.substring(p1 + 1, p2);
    trackDuration = p.substring(p2 + 1, p3);
    trackProgress = p.substring(p3 + 1).toInt();
    musicActive   = true;
    screenDirty   = true;
    return;
  }

  if (msg == "NOTRACK") { musicActive = false; screenDirty = true; return; }

  if (msg.startsWith("APPLIST:")) { updateAppList(msg.substring(8)); return; }

  if (msg.startsWith("VOLLVL:")) {
    String p = msg.substring(7);
    int sep = p.indexOf(':');
    if (sep < 0) return;
    String name = p.substring(0, sep);
    int pct = p.substring(sep + 1).toInt();
    if (name == activeAppName) {
      displayVolume = constrain(pct, 0, 100);
      if (state == ST_VOLUME_CONTROL) screenDirty = true;
    }
    return;
  }
}

void handlePingWatchdog() {
  if (!isConnected) return;
  unsigned long now = millis();

  if (now - lastPingSent >= PING_INTERVAL) {
    Serial.println("PING");
    lastPingSent = now;
  }

  if (now - lastPongReceived >= PING_TIMEOUT) {
    Serial.println("[TrussiRing] PC timeout — returning to waiting screen.");
    isConnected = false;
    state = ST_WAITING_FOR_PC;
    screenDirty = true;
  }
}

// ─────────────────────────────────────────────
//  OLED SCREENS
// ─────────────────────────────────────────────
// Boot-screen logo bitmap (32×32 px, XBM format, from Lopaka)
static const unsigned char image_imgbitmap_png_bits[] U8X8_PROGMEM = {
  0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,
  0xff,0xff,0xff,0xff,0xff,0xff,0xff,0xff,
  0xff,0xff,0xff,0x7f,0xff,0xff,0xff,0x3f,
  0xff,0xff,0xff,0x1f,0xff,0xff,0xff,0x0f,
  0xff,0xff,0xff,0x07,0xff,0xff,0xff,0x03,
  0x00,0xf8,0x07,0x00,0x00,0xf8,0x07,0x00,
  0x00,0xf8,0x07,0x00,0x00,0xf8,0x07,0x00,
  0x00,0xf8,0x07,0x00,0x00,0xf8,0x07,0x00,
  0x00,0xf8,0x07,0x00,0x00,0xf8,0x07,0x00,
  0x00,0xf8,0x07,0x00,0x00,0xf8,0x07,0x00,
  0x00,0xf8,0x07,0x00,0x00,0xf8,0x07,0x00,
  0x00,0xf8,0x07,0x00,0x00,0xf8,0x03,0x00,
  0x00,0xf8,0x01,0x00,0x00,0xf8,0x00,0x00,
  0x00,0x78,0x00,0x00,0x00,0x38,0x00,0x00,
  0x00,0x18,0x00,0x00,0x00,0x08,0x00,0x00,
  0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00
};

void drawBootScreen() {
  u8g2.clearBuffer();
  u8g2.setFontMode(1);
  u8g2.setBitmapMode(1);
  u8g2.setDrawColor(1);

  // 32×32 logo centered horizontally, sitting in upper half
  u8g2.drawXBMP(48, 7, 32, 32, image_imgbitmap_png_bits);

  // "TrussiRing" label below the logo
  u8g2.setFont(u8g2_font_profont17_tr);
  u8g2.drawStr(19, 57, "TrussiRing");

  u8g2.sendBuffer();
}

void drawWaitingScreen() {
  u8g2.clearBuffer();
  u8g2.setFontMode(1);
  u8g2.setBitmapMode(1);
  u8g2.setDrawColor(1);

  u8g2.setFont(u8g2_font_profont15_tr);
  u8g2.drawStr(10, 30, "In attesa del PC");

  u8g2.setFont(u8g2_font_profont12_tr);
  u8g2.drawStr(10, 44, waitAnimFrame ? "..." : ".");

  u8g2.sendBuffer();
}

// Faithful to uploaded IdleClock.txt
void drawNormalClock() {
  u8g2.clearBuffer();
  u8g2.setFontMode(1);
  u8g2.setBitmapMode(1);
  u8g2.setDrawColor(1);

  u8g2.setFont(u8g2_font_profont29_tr);
  u8g2.drawStr(24, 35, currentTime.c_str());

  u8g2.setFont(u8g2_font_profont15_tr);
  u8g2.drawStr(15, 47, currentDate.c_str());

  u8g2.sendBuffer();
}

// Faithful to uploaded ClockWithSong.txt
void drawClockWithSong() {
  u8g2.clearBuffer();
  u8g2.setFontMode(1);
  u8g2.setBitmapMode(1);
  u8g2.setDrawColor(1);

  u8g2.setFont(u8g2_font_profont29_tr);
  u8g2.drawStr(24, 25, currentTime.c_str());

  u8g2.setFont(u8g2_font_profont12_tr);
  String title  = truncateToFit(trackTitle, 21);
  String author = truncateToFit(trackArtist, 21);
  u8g2.drawStr(0, 41, title.c_str());
  u8g2.drawStr(0, 51, author.c_str());

  u8g2.drawFrame(0, 53, 128, 11);
  int fillW = map(constrain(trackProgress, 0, 100), 0, 100, 0, 124);
  if (fillW > 0) u8g2.drawBox(2, 55, fillW, 7);

  u8g2.sendBuffer();
}

// Faithful to uploaded ApplicationSelectScreen.txt, generalized to N apps
// with wraparound and the same XOR-invert-box highlight technique.
void drawApplicationSelect() {
  u8g2.clearBuffer();
  u8g2.setFontMode(1);
  u8g2.setBitmapMode(1);

  if (appCount == 0) {
    u8g2.setDrawColor(1);
    u8g2.setFont(u8g2_font_profont15_tr);
    u8g2.drawStr(6, 34, "Nessuna app disponibile");
    u8g2.sendBuffer();
    return;
  }

  const int8_t slotY[5]      = { 9, 21, 38, 52, 64 };
  const int8_t slotOffset[5] = { -2, -1, 0, 1, 2 };

  // Selected entry — drawn first, normal color, larger font
  u8g2.setDrawColor(1);
  u8g2.setFont(u8g2_font_profont17_tr);
  {
    int idx = ((menuIndex % appCount) + appCount) % appCount;
    String name = truncateToFit(appList[idx], 12);
    int x = (128 - (int)name.length() * 10) / 2;
    if (x < 0) x = 0;
    u8g2.drawStr(x, slotY[2], name.c_str());
  }

  // Surrounding entries — XOR color, smaller font
  u8g2.setDrawColor(2);
  u8g2.setFont(u8g2_font_profont15_tr);
  for (int s = 0; s < 5; s++) {
    if (s == 2) continue;
    int idx = ((menuIndex + slotOffset[s]) % appCount + appCount) % appCount;
    String name = truncateToFit(appList[idx], 14);
    int x = (128 - (int)name.length() * 8) / 2;
    if (x < 0) x = 0;
    u8g2.drawStr(x, slotY[s], name.c_str());
  }

  // Inverting highlight box over the selected row (same trick as the reference)
  u8g2.drawBox(0, 24, 128, 17);
  u8g2.setDrawColor(1);  // reset for whichever screen draws next

  u8g2.sendBuffer();
}

void drawVolumeScreen() {
  u8g2.clearBuffer();
  u8g2.setFontMode(1);
  u8g2.setBitmapMode(1);
  u8g2.setDrawColor(1);

  u8g2.setFont(u8g2_font_profont15_tr);
  String name = truncateToFit(activeAppName, 16);
  int x = (128 - (int)name.length() * 8) / 2;
  if (x < 0) x = 0;
  u8g2.drawStr(x, 20, name.c_str());

  u8g2.drawFrame(10, 28, 108, 14);
  int fillW = map((int)displayVolume, 0, 100, 0, 104);
  if (fillW > 0) u8g2.drawBox(12, 30, fillW, 10);

  u8g2.setFont(u8g2_font_profont12_tr);
  char pct[6];
  snprintf(pct, sizeof(pct), "%d%%", (int)displayVolume);
  int px = (128 - (int)strlen(pct) * 7) / 2;
  if (px < 0) px = 0;
  u8g2.drawStr(px, 58, pct);

  u8g2.sendBuffer();
}

void renderCurrentScreen() {
  switch (state) {
    case ST_WAITING_FOR_PC:
    case ST_CONNECTING:
      drawWaitingScreen();
      break;

    case ST_APPLICATION_MENU:
      drawApplicationSelect();
      break;

    case ST_VOLUME_CONTROL:
      drawVolumeScreen();
      break;

    case ST_IDLE:
    case ST_APPLICATION_SEARCH:
    case ST_MASTER_VOLUME_FALLBACK:
    case ST_CONNECTED:
      if (musicActive) drawClockWithSong();
      else             drawNormalClock();
      break;

    default:
      break;  // ST_BOOT — drawn once in setup(), never revisited
  }
}

void updateWaitAnimation() {
  if (state != ST_WAITING_FOR_PC) return;
  if (millis() - lastWaitAnimToggle >= 500) {
    lastWaitAnimToggle = millis();
    waitAnimFrame = !waitAnimFrame;
    screenDirty = true;
  }
}

// ─────────────────────────────────────────────
//  SETUP
// ─────────────────────────────────────────────
void setup() {
  Serial.begin(115200);
  Serial.setTimeout(0);


  // Switches — GND-wired, same as AxiDeck
  for (int i = 0; i < 4; i++) pinMode(SW_PINS[i], INPUT_PULLUP);

  // Encoder
  pinMode(ENCODER_A_PIN, INPUT_PULLUP);
  pinMode(ENCODER_B_PIN, INPUT_PULLUP);
  pinMode(ENCODER_BTN_PIN, INPUT_PULLUP);
  encoderPrevState = (digitalRead(ENCODER_A_PIN) << 1) | digitalRead(ENCODER_B_PIN);
  attachInterrupt(digitalPinToInterrupt(ENCODER_A_PIN), onEncoderChange, CHANGE);
  attachInterrupt(digitalPinToInterrupt(ENCODER_B_PIN), onEncoderChange, CHANGE);

  // Status LED
  ledcSetup(0, LED_PWM_FREQ, LED_PWM_RES);
  ledcAttachPin(LED_PIN, 0);

  // OLED
  Wire.begin(OLED_SDA_PIN, OLED_SCL_PIN);
  u8g2.begin();

  // Persistence
  prefs.begin("trussiring", false);
  savedAppName = prefs.getString("selApp", "");

  drawBootScreen();
  delay(2000);  // one-shot boot flash — same precedent as AxiDeck's boot delay

  state = ST_WAITING_FOR_PC;
  screenDirty = true;
}

// ─────────────────────────────────────────────
//  LOOP  — fully non-blocking
// ─────────────────────────────────────────────
void loop() {
  while (Serial.available() > 0)
  {
    char c = (char)Serial.read();
    if (c == '\n')
    {
      serialLineBuf.trim();
      if (serialLineBuf.length() > 0)
        parseSerialLine(serialLineBuf);
      serialLineBuf = "";
    }
    else if (c != '\r')
    {
      serialLineBuf += c;
    }
  }

  handleSwitches();
  handleEncoderButtonPoll();

  int8_t delta;
  noInterrupts();
  delta = encoderDelta;
  encoderDelta = 0;
  interrupts();

  if (delta != 0) {
    encoderAccum += delta;
    while (encoderAccum >= ENCODER_STEPS_PER_CLICK) {
      handleEncoderRotation(+1);
      encoderAccum -= ENCODER_STEPS_PER_CLICK;
    }
    while (encoderAccum <= -ENCODER_STEPS_PER_CLICK) {
      handleEncoderRotation(-1);
      encoderAccum += ENCODER_STEPS_PER_CLICK;
    }
  }

  handleMenuTimeout();
  handleVolumeScreenTimeout();
  handleSearchTimeout();
  handlePingWatchdog();
  updateLED();
  updateWaitAnimation();

  if (screenDirty) {
    renderCurrentScreen();
    screenDirty = false;
  }
}
