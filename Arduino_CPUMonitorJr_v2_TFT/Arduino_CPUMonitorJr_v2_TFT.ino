// CPU Monitor Jr v2 (for TFT Display)
//
// Copyright Rob Latour, 2022
// htts://raltour.com
// https://github.com/roblatour/CPUMonitorJr
//
// Compile and upload using Arduino IDE (2.0.1 or greater)
//
// Physical board:                 LILYGO T-Display-S3
// Board in Arduino board manager: ESP32S3 Dev Module
//
// Arduino Tools settings:
// USB CDC On Boot:                Enabled
// CPU Frequency:                  240MHz
// USB DFU On Boot:                Enabled
// Core Debug Level:               None
// Erase All Flash Before Upload:  Disabled
// Events Run On:                  Core 1
// Flash Mode:                     QIO 80Mhz
// Flash Size:                     8MB (64Mb)
// Arduino Runs On:                Core 1
// USB Firmware MSC On Boot:       Disabled
// PSRAM:                          OPI PSRAM
// Partition Scheme:               8MB with spifs (3MB APP/1.5 SPIFS)
// USB Mode:                       Hardware CDC and JTAG
// Upload Mode:                    UART0 / Hardware CDC
// Upload Speed:                   921600
// Programmer                      ESPTool

#include <Arduino.h>
#include <EEPROM.h>
#include <WiFi.h>
#include <WiFiMulti.h>
#include <WiFiClientSecure.h>
#include <WebSocketsClient.h>
#include <time.h>
#include <TimeLib.h>
#include <WiFiUdp.h>
#include <ArduinoOTA.h>
#include <AsyncTCP.h>           // https://github.com/esphome/AsyncTCP (put all files in src directory into the AsyncTCP directory)
#include <ESPAsyncWebServer.h>  // https://github.com/esphome/ESPAsyncWebServer (put all files in src directory into the ESPAsyncWebServer directory)
#include <TFT_eSPI.h>           // please use the TFT_eSPI library found here: https://github.com/Xinyuan-LilyGO/T-Display-S3/tree/main/lib
#include "pin_config.h"         // found at https://github.com/Xinyuan-LilyGO/T-Display-S3/tree/main/example/factory

#include "user_secrets.h"
#include "user_settings.h"
#include "Rob.h"

// Wifi stuff
const char* wifi_SSID = SECRET_WIFI_SSID;
const char* wifi_password = SECRET_WIFI_PASSWORD;
const unsigned long DeviceResetThreshold = DEVICE_WILL_RESET_AFTER_THIS_MANY_SECONDS_WITH_NO_WIFI_CONNECTION * 1000;
unsigned long LastTimeWiFiWasConnected;

// OTA stuff
const char* OTAHostName = SECRET_OTA_HOSTNAME;
const char* OTAPassword = SECRET_OTA_PASSWORD;

// Comunication with computer stuff
const int udpPort = UDP_PORT;
const char* udpBroadcastAddress = UDP_BROADCAST_ADDRESS;

WebSocketsClient webSocket;

AsyncWebServer server(80);
AsyncWebSocket ws("/cpumonitorjr" + String(udpPort));

const int secondsSinceLastUpdateReceivedToShowTime = 5;  // if communications have not been recieved from the computer after this many seconds, then show date and time screen rather than bar graph screen
const unsigned long AdvertiseThreshold = 15;             // if no data is received from the computer after this many seconds, make a request for it

unsigned long lastTimeDataWasReceivedFromComputer = 0;

// Readings

bool currentReadingsAreAvailable = false;

bool clearGraphData = true;

double currentCPUValue[MAX_NUMBER_OF_PROCESSORS];

uint32_t currentNumberOfProcessors;

double currentCPUAverage;

float currentAverageTempReading;

float currentMaximumTempReading;

float currentPercentOfMemoryUsed;

String currentComputerName = "";

String currentLANAddress = "";

String currentExternalAddress = "";

// Time stuff
const char* ntpServer = TIME_SERVER;
const long gmtOffset_sec = 3600 * TIME_ZONE;
const int daylightOffset_sec = 3600 * DAYLIGHT_SAVINGS_TIME;

char timeHour[3] = "00";
char timeMin[3] = "00";
char timeSec[3];
String timeAmPm;

char m[12];
char y[5];
char d[3];
//char dw[12];

// display stuff
TFT_eSPI showPanel = TFT_eSPI();
TFT_eSprite sprite = TFT_eSprite(&showPanel);

// settings
bool showComputerName = SHOW_COMPUTER_NAME;

bool showIPAddress = SHOW_IP_ADDRESS;
bool showLANAddress = SHOW_LAN_ADDESS;

bool showTemperature = SHOW_TEMPERATURE;
bool showAverageTemperature = SHOW_AVERAGE_TEMPERATURE;
bool showTemperatureInCelsius = SHOW_TEMPERATURE_IN_CELSIUS;

bool showPercentOfMemoryUsed = SHOW_PERCENT_OF_MEMORY_USED;

bool showTime = SHOW_TIME;
bool showTimeIn12HourFormat = SHOW_TIME_IN_TWELVE_HOUR_FORMAT;
bool showAmPmIndicator = SHOW_AM_PM_INDICATOR;
bool showSeconds = SHOW_SECONDS;

bool showDate = SHOW_DATE;

bool showHistoricalLineGraph = SHOW_HISTORICAL_LINE_GRAPH;
uint32_t historicalLineGraphColour = HISTORICAL_LINE_GRAPH_COLOUR;

bool showCurrentCPUBarGraphs = SHOW_CURRENT_CPU_BAR_GRAPHS;
uint32_t currentCPUBarGraphColour = CURRENT_CPU_BAR_GRAPHS_COLOUR;

// EEPROM stuff
const byte eepromInitializationConfirmationValue = 'T';
const int eepromInitializationConfirmationAddress = 0;
const int eepromDataStartingAddress = eepromInitializationConfirmationAddress + 1;
const int numberOfSavedSettingsInEEPROM = 14;

// Button stuff
#define TOP_BUTTON 14    // Button marked 'Key' on LILYGO T-Display-S3
#define BOTTOM_BUTTON 0  // Button marked 'BOT' on LILYGO T-Display-S3

// other misc stuff

const bool showTempUnits = false;     // if true will show 'C' or 'F' after the degree symbol, if false the degree sign will be shown but no 'F' or 'C' after it
const bool showMilitaryTime = false;  // if true will show 24 hour formatted time, if false will show 12 hour formatted time

const int32_t TFT_Width = 320;   // TFT show Width
const int32_t TFT_Height = 170;  // TFT show Height

bool showTheSettingsScreen = false;
bool showTheAboutScreen = false;

unsigned long setupCompleteTime;

void setupSerial_Monitor() {

  Serial.begin(SERIAL_MONITOR_SPEED);
}

void setupButtons() {

  // Setup buttons on LILYGO T-Display-S3
  pinMode(TOP_BUTTON, INPUT_PULLUP);
  pinMode(BOTTOM_BUTTON, INPUT_PULLUP);
}


void SetupEEPROM() {

  // The EEPROM is used to save and store settings so that they are retained when the device is powered off and restored when it is powered back on

  byte Settings[numberOfSavedSettingsInEEPROM];

  EEPROM.begin(numberOfSavedSettingsInEEPROM + 1);

  byte TestForInitializedEEPROM = EEPROM.read(eepromInitializationConfirmationAddress);

  if (TestForInitializedEEPROM == eepromInitializationConfirmationValue) {
    // The EEPROM has previousily been intialized, so load the settings based on the values in the EEPROM
    LoadSettingsFromNonVolatileMemory();

  } else {
    // The EEPROM has not been intialized, so initialize it now
    EEPROM.write(eepromInitializationConfirmationAddress, eepromInitializationConfirmationValue);
    EEPROM.commit();
    // store the default values into the EEPROM for use next time
    StoreSettingsInNonVolatileMemory();
  };
}

void StoreSettingsInNonVolatileMemory() {

  byte Setting[numberOfSavedSettingsInEEPROM];

  Setting[0] = showComputerName;
  Setting[1] = showIPAddress;
  Setting[2] = showLANAddress;
  Setting[3] = showTemperature;
  Setting[4] = showAverageTemperature;
  Setting[5] = showTemperatureInCelsius;
  Setting[6] = showPercentOfMemoryUsed;
  Setting[7] = showTime;
  Setting[8] = showTimeIn12HourFormat;
  Setting[9] = showAmPmIndicator;
  Setting[10] = showSeconds;
  Setting[11] = showDate;
  Setting[12] = showHistoricalLineGraph;
  Setting[13] = showCurrentCPUBarGraphs;

  // write the settings to their respective eeprom storage locations only if they need updating
  bool commitRequired = false;
  for (int i = 0; i < numberOfSavedSettingsInEEPROM; i++) {
    if (EEPROM.read(i + eepromDataStartingAddress) != Setting[i]) {
      EEPROM.write(i + eepromDataStartingAddress, Setting[i]);
      commitRequired = true;
    };
  };

  if (commitRequired)
    EEPROM.commit();
}

void LoadSettingsFromNonVolatileMemory() {

  byte Setting[numberOfSavedSettingsInEEPROM];

  for (int i = 0; i < numberOfSavedSettingsInEEPROM; i++)
    Setting[i] = EEPROM.read(i + eepromDataStartingAddress);

  showComputerName = Setting[0];
  showIPAddress = Setting[1];
  showLANAddress = Setting[2];
  showTemperature = Setting[3];
  showAverageTemperature = Setting[4];
  showTemperatureInCelsius = Setting[5];
  showPercentOfMemoryUsed = Setting[6];
  showTime = Setting[7];
  showTimeIn12HourFormat = Setting[8];
  showAmPmIndicator = Setting[9];
  showSeconds = Setting[10];
  showDate = Setting[11];
  showHistoricalLineGraph = Setting[12];
  showCurrentCPUBarGraphs = Setting[13];
}

void setupDisplay() {

  // Turn on power to LCD display
  digitalWrite(PIN_POWER_ON, HIGH);
  pinMode(PIN_POWER_ON, OUTPUT);

  // Setup show on LILYGO T-Display-S3
  showPanel.init();

  showPanel.begin();

  sprite.createSprite(TFT_Width, TFT_Height);
  sprite.setSwapBytes(true);

  showPanel.fillScreen(TFT_BLACK);
  showPanel.setRotation(1);  // 0 = 0 degrees , 1 = 90 degrees, 2 = 180 degrees, 3 = 270 degrees
}

void setupWiFi() {

  bool notyetconnected = true;
  int attempt = 0;
  int waitThisManySecondsForAConnection = 1;
  float timeWaited;

  const int leftBoarder = 0;
  const int displayLineNumber[] = { 0, 16, 32, 48, 64, 80 };

  String message;

  if (SHOW_WIFI_CONNECTING_STATUS) {

    showPanel.setTextColor(TFT_GREEN, TFT_BLACK);
    sprite.setTextDatum(TL_DATUM);  // position text at the top left

    message = "Attempting to connect to ";
    message.concat(wifi_SSID);

    showPanel.drawString(message, leftBoarder, displayLineNumber[0], 1);
  };

  while (notyetconnected) {

    attempt++;

    WiFi.mode(WIFI_STA);
    WiFi.begin(wifi_SSID, wifi_password);

    unsigned long startedWaiting = millis();
    unsigned long waitUntil = startedWaiting + (waitThisManySecondsForAConnection * 1000);

    while ((WiFi.status() != WL_CONNECTED) && (millis() < waitUntil)) {

      if (SHOW_WIFI_CONNECTING_STATUS) {

        message = "Attempt ";
        message.concat(attempt);
        message.concat("     ");  // add a few spaces to the end of the message to effectively clear the balance of the show line

        showPanel.drawString(message, leftBoarder, displayLineNumber[2], 1);
        //drawDisplay();

        message = "Waited ";

        timeWaited = millis() - startedWaiting;
        message.concat(String(timeWaited / 1000, 1));

        message.concat(" seconds in this attempt");
        message.concat("     ");  // add a few spaces to the end of the message to effectively clear the balance of the show line

        showPanel.drawString(message, leftBoarder, displayLineNumber[3], 1);
        //drawDisplay();
      };
    };

    if (WiFi.status() == WL_CONNECTED) {

      notyetconnected = false;
      LastTimeWiFiWasConnected = millis();

    } else {

      // make  the next connection attempt cycle to wait a second longer than we did the previous  time; up to a maximum wait time 60 seconds
      if (waitThisManySecondsForAConnection < 60)
        waitThisManySecondsForAConnection++;

      WiFi.disconnect(true);
    };
  };

  if (SHOW_WIFI_CONNECTING_STATUS) {

    message = "Connected at IP address ";
    message.concat(WiFi.localIP().toString());

    showPanel.drawString(message, leftBoarder, displayLineNumber[5], 1);
  };
};

void checkWiFiConnection() {

  // if connection has been out for > Device reset threshold restart

  if (WiFi.status() == WL_CONNECTED) {
    LastTimeWiFiWasConnected = millis();
  } else {
    if ((millis() - LastTimeWiFiWasConnected) > DeviceResetThreshold) {
      ESP.restart();
    }
  }
}

void setupTime() {

  // when the time will originally be drawn and set from the intenet
  // (this in case the connection to the computer being monitored is down at the time the esp32 device is powered on)
  configTime(gmtOffset_sec, daylightOffset_sec, ntpServer);

  // later in processing, the time will be drawn and set from the remote computer's time if the option UPDATE_TIME_FROM_CONNECTED_COMPUTER is true
}

void checkButtonsOnMainWindow() {

  if (checkButton(TOP_BUTTON))
    showTheSettingsScreen = true;

  if (checkButton(BOTTOM_BUTTON))
    showTheAboutScreen = true;
}

void clearDisplay() {

  sprite.fillSprite(TFT_BLACK);
}

void updateNotConnectedMessage() {

  sprite.setTextColor(TFT_YELLOW, TFT_BLACK);

  // align text to Middle of the show
  sprite.setTextDatum(MC_DATUM);

  // for a more seemly startup only show the 'not connected' messages if the program's setup function completed more than five seconds ago

  if ((millis() - setupCompleteTime) > 5000)
    if (WiFi.status() == WL_CONNECTED)
      sprite.drawString("Not connected to computer", 160, 85, 4);
    else
      sprite.drawString("Not connected to WIFI", 160, 85, 4);
}

void updateConnectedComputer() {

  // Dimensions of this show object are 124x55

  // Top left most pixel
  const int32_t xOffset = 0;
  const int32_t yOffset = 12;

  sprite.setTextColor(TFT_WHITE, TFT_BLACK);

  // align text to Top Left
  sprite.setTextDatum(TL_DATUM);

  // tidbit: Windows Computer names can be a maximum of 15 characters
  if (showComputerName) sprite.drawString(currentComputerName, xOffset, yOffset, 2);
  if (showIPAddress)
    if (showLANAddress)
      sprite.drawString(currentLANAddress, xOffset, yOffset + 20, 2);
    else
      sprite.drawString(currentExternalAddress, xOffset, yOffset + 20, 2);
}

void updateTemperature() {

  // Dimensions of this show object are 56X56
  const int objectW = 60;
  const int objectH = 25;

  // Top left most pixel
  const int xOffset = 125;
  const int yOffset = 0;

  if (!showTemperature) return;

  sprite.setTextColor(TFT_WHITE, TEMPERATURE_COLOUR);

  // show temperature

  sprite.fillRoundRect(xOffset, yOffset, objectW, objectH, 4, TEMPERATURE_COLOUR);

  if (showTemperature && ((showTemperatureInCelsius) && (currentAverageTempReading == 0)) || ((!showTemperatureInCelsius) && (currentAverageTempReading == 32))) {

    // align text to middle center
    sprite.setTextDatum(MC_DATUM);

    // if reporting zero degress Celsius / 32 degrees Fahrenheit then really the reading is not available
    sprite.drawString("N/A", xOffset + objectW / 2, yOffset + objectH / 2, 2);

  } else {

    String sCurrentTempReading;

    if (showTemperature) {

      // align text to middle right
      sprite.setTextDatum(MR_DATUM);

      if (showAverageTemperature)
        sCurrentTempReading = String(currentAverageTempReading, 1);
      else
        sCurrentTempReading = String(currentMaximumTempReading, 1);

      if (showTemperatureInCelsius)
        sCurrentTempReading.concat("  C");
      else
        sCurrentTempReading.concat("  F");

      // draw the temperture value
      sprite.drawString(sCurrentTempReading, xOffset + objectW - 2, yOffset + objectH / 2, 2);

      // draw the degree symbol
      sprite.drawString("o", xOffset + objectW - 13, yOffset + objectH / 2 - 5, 1);
    };
  };
}

void updatePercentOfMemoryUsed() {

  // Dimensions of this show object are 56X56
  const int objectW = 60;
  const int objectH = 25;

  // Top left most pixel
  const int xOffset = 125;
  const int yOffset = 30;

  if (!showPercentOfMemoryUsed) return;

  // align text to middle center
  sprite.setTextDatum(MC_DATUM);

  sprite.setTextColor(TFT_WHITE, MEMORY_COLOUR);

  // show Percent Of Memory Used

  sprite.fillRoundRect(xOffset, yOffset, objectW, objectH, 4, MEMORY_COLOUR);

  String sPercentOfMemoryUsed = String(currentPercentOfMemoryUsed, 1) + "%";

  sprite.drawString(sPercentOfMemoryUsed, xOffset + objectW / 2, yOffset + objectH / 2, 2);
}

void updateClock() {

  if (!showTime) return;

  // Dimensions of this show object are 131x55

  // Top left most pixel
  const int xOffset = 188;
  const int yOffset = 0;

  // align text to middle centre
  sprite.setTextDatum(MC_DATUM);

  sprite.setTextColor(TFT_WHITE, TIME_COLOUR);

  getLocalTime();

  // show hours
  sprite.fillRoundRect(xOffset, yOffset, 40, 32, 4, TIME_COLOUR);
  if (showTimeIn12HourFormat & (timeHour[0] == '0')) timeHour[0] = ' ';
  sprite.drawString(String(timeHour), xOffset + 20, yOffset + 19, 4);

  // show minutes
  sprite.fillRoundRect(xOffset + 46, yOffset, 40, 32, 4, TIME_COLOUR);
  sprite.drawString(String(timeMin), xOffset + 66, yOffset + 19, 4);

  // show am/pm
  if (showTimeIn12HourFormat) {
    sprite.fillRoundRect(xOffset + 92, yOffset, 40, 18, 4, TIME_COLOUR);
    sprite.drawString(timeAmPm, xOffset + 112, yOffset + 10, 2);
  }

  // show seconds

  if (showSeconds) {
    if (showTimeIn12HourFormat) {
      sprite.fillRoundRect(xOffset + 92, yOffset + 21, 40, 12, 4, TIME_COLOUR);
      sprite.drawString(String(timeSec), xOffset + 112, yOffset + 27, 1);
    } else {
      sprite.fillRoundRect(xOffset + 92, yOffset, 40, 32, 4, TIME_COLOUR);
      sprite.drawString(String(timeSec), xOffset + 112, yOffset + 19, 4);
    };
  };

  // show date
  if (showDate) {
    sprite.setTextColor(TFT_WHITE, DATE_COLOUR);
    sprite.fillRoundRect(xOffset, yOffset + 37, 131, 18, 4, DATE_COLOUR);

    String dateStringA = String(m) + " " + String(d) + ", " + String(y);

    // a bunch of work to remove spaces that appear in serial; for example: "November  3, 2022" -> "November 3, 2022"
    // remember, its the little things in life that matter!

    String dateStringB = "";
    int spaceFound = 0;
    for (int i = 0; i < dateStringA.length(); i++) {

      if (dateStringA.charAt(i) == char(' ')) {
        spaceFound++;
        if (spaceFound < 2)
          dateStringB.concat(String(dateStringA.charAt(i)));
      } else {
        spaceFound = 0;
        dateStringB.concat(String(dateStringA.charAt(i)));
      }
    };

    sprite.drawString(dateStringB, xOffset + 65, yOffset + 46, 2);
  };
};

void getLocalTime() {

  struct tm timeinfo;

  if (UPDATE_TIME_FROM_CONNECTED_COMPUTER) {

    time_t t = now();

    timeinfo.tm_year = year(t) - 1900;
    timeinfo.tm_mon = month(t) - 1;
    timeinfo.tm_mday = day(t);
    timeinfo.tm_hour = hour(t);
    timeinfo.tm_min = minute(t);
    timeinfo.tm_sec = second(t);

  } else {

    if (!getLocalTime(&timeinfo)) {
      // Failed to obtain time
      return;
    };
  };

  if (showTimeIn12HourFormat) {

    if (timeinfo.tm_hour < 12) {

      timeAmPm = "AM";

      if (timeinfo.tm_hour == 0) { timeinfo.tm_hour = 12; };

    } else {

      timeAmPm = "PM";

      if (timeinfo.tm_hour > 12) { timeinfo.tm_hour -= 12; };
    };
  };

  strftime(timeHour, 3, "%H", &timeinfo);
  strftime(timeMin, 3, "%M", &timeinfo);
  strftime(timeSec, 3, "%S", &timeinfo);

  strftime(y, 5, "%Y", &timeinfo);   // %Y = four digit year
  strftime(m, 12, "%B", &timeinfo);  // %B = month name
  //strftime(dw, 10, "%A", &timeinfo);
  strftime(d, 3, "%e", &timeinfo);  // %e = date without a leading zero
}

void updateGraph() {

  // Dimensions of this show object are 320x105
  const int objectW = TFT_Width;
  const int objectH = 105;

  // Top left most pixel
  const int xOffset = 0;
  const int yOffset = 60;

  // other working dimensions
  const int32_t boarder = 2;   // boarder beyond frame and graph
  const int32_t spacing = 10;  // grid spacing

  // graph readings storage array
  const int maxReadings = objectW - 2 * boarder;
  static int readings[maxReadings] = { 0 };

  if (clearGraphData) {
    for (int i = 0; i < maxReadings; i++)
      readings[i] = 0;
    clearGraphData = false;
  }

  int32_t gx = xOffset + boarder;  // graph x axis starting pixel
  int32_t gy = yOffset + boarder;  // graph y axis starting pixel

  int32_t gw = objectW - 2 * boarder;  // graph width
  int32_t gh = objectH - 2 * boarder;  // graph hieght

  if (showCurrentCPUBarGraphs || showHistoricalLineGraph) {
    // draw frame
    sprite.drawRect(gx - boarder, gy - boarder, gw + boarder, gh + boarder + 1, GRAPH_FRAME_COLOUR);

    // draw horizontal grid lines
    for (int y = gy; y <= gy + gh + 1; y += spacing)
      sprite.drawLine(gx, y, gx + gw, y, GRAPH_GRIDLINE_COLOUR);
  };

  if (showCurrentCPUBarGraphs) {

    // draw a bar graph, with a bar for each CPU

    const int32_t spacingBetweenBars = 1;
    int32_t barWidth = (int32_t)(gw / currentNumberOfProcessors) - spacingBetweenBars;
    int32_t barWidthPlusSpacingBetweenBars = barWidth + spacingBetweenBars;
    int32_t xOffsetToCentreBarsInGraph = (gw - barWidthPlusSpacingBetweenBars * currentNumberOfProcessors) / 2 - 1;
    if (xOffsetToCentreBarsInGraph < 0) xOffsetToCentreBarsInGraph = 0;

    for (int32_t i = 0; i < currentNumberOfProcessors; i++)
      sprite.fillRect(gx + xOffsetToCentreBarsInGraph + i * barWidthPlusSpacingBetweenBars, gy + gh - (int32_t)currentCPUValue[i] - 1, barWidth, (int32_t)currentCPUValue[i], currentCPUBarGraphColour);
  }

  if (showHistoricalLineGraph) {

    // shift all graph data to the right by 1 array element
    for (int i = maxReadings - 1; i > 0; i--)
      readings[i] = readings[i - 1];

    // insert new current data in the first array element
    readings[0] = currentCPUAverage;

    // draw historical line graph
    for (int32_t i = 0; i < maxReadings - 1; i++)
      if (showCurrentCPUBarGraphs) {
        // draw just the line
        sprite.drawLine(i + gx - 1, gy + gh - readings[i] - 1, i + gx, gy + gh - readings[i + 1] - 1, historicalLineGraphColour);
      } else {
        // draw just the line and fill beneith it
        sprite.drawLine(i + gx - 1, gy + gh - readings[i] - 1, i + gx, gy + gh - 1, historicalLineGraphColour);  // draw and fill
      };
  };
}


void drawDisplay() {

  sprite.pushSprite(0, 0);
}

bool checkButton(int pinNumber) {

  bool returnValue = false;

  if (digitalRead(pinNumber) == 0) {

    // weed out false positives caused by debounce
    delay(10);
    if (digitalRead(pinNumber) == 0)

      returnValue = true;

    // hold here until the button is released
    while (digitalRead(pinNumber) == 0)
      delay(10);
  };

  return returnValue;
}


//----------------------------------------------------------------------------------------------------------

WiFiUDP udp;

void setupUDP() {

  udp.begin(udpPort);
}

void Advertise() {

  // used to let this program tell your computer what it's ip address is so that a websocket link can be (re)established
  // also when a websocket link is (re)established the time will be updated based on your windows computer's time

  String message;

  message = "CPUMonitorJr;";
  message.concat(WiFi.localIP().toString());
  message.concat("; ");

  int len = message.length();

  uint8_t buffer[len];

  message.getBytes(buffer, len);

  udp.beginPacket(udpBroadcastAddress, udpPort);
  udp.write(buffer, len - 1);
  udp.endPacket();

  memset(buffer, 0, len - 1);

  if (DEBUG_IS_ON) Serial.println("Advertise() broadcasted: " + message);

  delay(2500);  // do not remove this pause
}

void setupComplete() {

  setupCompleteTime = millis();
}

//----------------------------------------------------------------------------------------------------------

void onWsEvent(AsyncWebSocket* server, AsyncWebSocketClient* client, AwsEventType type, void* arg, uint8_t* data, size_t len) {

  lastTimeDataWasReceivedFromComputer = millis();

  // used to handle messages sent from your windows computer

  if (type == WS_EVT_CONNECT) {

    // Websocket client connection received
    if (DEBUG_IS_ON) Serial.println("connected");

  } else if (type == WS_EVT_DISCONNECT) {

    // Client disconnected
    if (DEBUG_IS_ON) Serial.println("disconnected");
    lastTimeDataWasReceivedFromComputer = 0;  // this will force a re-advertising for data

  }

  else if (type == WS_EVT_DATA) {

    int tranactionCode = data[0];  // trans code 0 = date and time; trans code 1 = computer name and ip addresses; trans code 2 = memory, temperature and cpu readings

    // if (DEBUG_IS_ON) Serial.println("Transaction code: " + String(tranactionCode));

    switch (tranactionCode) {

      case 0:  // date and time stream
        {

          /*
              byte 0 = is a 0 to represent this is a time stream
              byte 1 = year  (incoming year is current year - 2000) ' warning this will need to change for the year 2256 :-)
              byte 2 = month
              byte 3 = day
              byte 4 = dayofweek
              byte 5 = hour
              byte 6 = minute
              byte 7 = second
          */


          if (DEBUG_IS_ON) Serial.println(String(data[5]) + ":" + String(data[6]) + ":" + String(data[7]) + " " + (String)(data[1] + 2000) + "/" + String(data[2]) + "/" + String(data[3]));

          if (UPDATE_TIME_FROM_CONNECTED_COMPUTER)
            // set esp32's time to match computer time
            setTime((int)data[5], (int)data[6], (int)data[7], (int)data[3], (int)data[2], (int)((int)data[1] + 2000));
        };

        break;

      case 1:  // Computer name and the IP Addresses stream

        /*
            byte 0 = is a 0 to represent this is a time stream
            byte 1 .. n:
              data is deliminated by a semi-colon (";") in the format of:
                  Computer name;LAN address;External address;
              for example:
                  CharlesPC;192.168.1.100;80.169.33.150;
        */

        {

          currentComputerName = "";
          currentLANAddress = "";
          currentExternalAddress = "";

          int index = 0;
          for (int i = 1; i < len; i++) {

            if (data[i] == ';') {
              index++;
            } else {

              if (index == 0)
                currentComputerName.concat(String((char)data[i]));

              if (index == 1)
                currentLANAddress.concat(String((char)data[i]));

              if (index == 2)
                currentExternalAddress.concat(String((char)data[i]));
            };
          };
        };

        if (DEBUG_IS_ON) Serial.println(currentComputerName + " " + currentLANAddress + " " + currentExternalAddress);

        break;

      case 2:  // memory, temperature can cpu loads stream

        /*
          byte 0 = is a '2' to represent this is a memory, temperature and cpu data stream 
          byte 1 = percent of memory used whole number
          byte 2 = percent of memory used decimal
          byte 3 = average temp whole number
          byte 4 = average temp decimal
          byte 3 = max temp whole number
          byte 5 = max temp decimal
          byte 7 = number of cpus
          byte 8 and on  = cpu busy of each cpu
        */

        {

          currentReadingsAreAvailable = true;

          // update the percent of memory used from passed data
          // whole part of the the percent of memory used value is in byte 1, decimal part is in byte 2

          currentPercentOfMemoryUsed = data[1] + (float(data[2]) / float(10));


          // this can be turned on, however my cause problems if data is coming in too fast
          //if (DEBUG_IS_ON) Serial.print(" " + String(currentPercentOfMemoryUsed));


          // update the current average tempurature from passed data
          // whole part of the average temperature value is in byte 3, decimal part is in byte 4

          currentAverageTempReading = data[3] + (float(data[4]) / float(10));

          if (showTemperatureInCelsius) {
          } else {
            currentAverageTempReading = currentAverageTempReading * 9 / 5 + 32;
          };

          // this can be turned on, however my cause problems if data is coming in too fast
          // if (DEBUG_IS_ON) Serial.println(String(currentAverageTempReading));


          // update the current maximum tempurature from passed data
          // whole part of the maximum temperature value is in byte 5, decimal part is in byte 5

          currentMaximumTempReading = data[5] + (float(data[6]) / float(10));

          if (showTemperatureInCelsius) {
          } else {
            currentMaximumTempReading = currentMaximumTempReading * 9 / 5 + 32;
          };

          // this can be turned on, however may cause problems if data is coming in too fast
          // if (DEBUG_IS_ON) Serial.println(String(currentAverageTempReading));


          // byte 7 contains the number of CPUs
          // byte 8 and beyond contain the individual percent busy CPU ratings for each cpu

          // load CPUValues into an array to populate the Bar Graph
          currentNumberOfProcessors = data[7];
          for (uint8_t i = 0; i < currentNumberOfProcessors; i++)
            currentCPUValue[i] = data[i + 8];

          // calculate average CPU value to polulate the Line Graph
          double workingTotal = 0;
          for (uint8_t i = 0; i < currentNumberOfProcessors; i++)
            workingTotal += currentCPUValue[i];

          currentCPUAverage = workingTotal / currentNumberOfProcessors;

          // this can be turned on, however my cause problems if data is coming in too fast
          //if (DEBUG_IS_ON) Serial.println(String(currentAverageTempReading) + " " + String(currentPercentOfMemoryUsed) + " " + String(currentCPUAverage));
        };

        break;
    }
  }
}

//----------------------------------------------------------------------------------------------------------

void setupWebSocket() {

  ws.onEvent(onWsEvent);
  server.addHandler(&ws);

  server.begin();
}

void checkWebSocket() {

  int SecondsSinceLastTimeDataWasReceivedOrRequestedFromComputer = (millis() - lastTimeDataWasReceivedFromComputer) / 1000;

  if (SecondsSinceLastTimeDataWasReceivedOrRequestedFromComputer > AdvertiseThreshold) {
    currentReadingsAreAvailable = false;
    clearGraphData = true;
    lastTimeDataWasReceivedFromComputer = millis();
    if (DEBUG_IS_ON) Serial.println("checkWebSocket() Advertise");
    Advertise();
  };
}

//----------------------------------------------------------------------------------------------------------


void setupOTAUpdate() {

  // Port defaults to 3232
  // ArduinoOTA.setPort(3232);

  // Hostname defaults to esp3232-[MAC]
  ArduinoOTA.setHostname(OTAHostName);

  // No authentication by default
  ArduinoOTA.setPassword(OTAPassword);

  ArduinoOTA
    .onStart([]() {
      String type;
      if (ArduinoOTA.getCommand() == U_FLASH)
        type = "sketch";
      else  // U_SPIFFS
        type = "filesystem";

      // NOTE: if updating SPIFFS this would be the place to unmount SPIFFS using SPIFFS.end()
      // Serial.println("Start updating " + type);
    })
    .onEnd([]() {
      // Serial.println("\nEnd");
      //ESP.restart();
    })
    .onProgress([](unsigned int progress, unsigned int total) {
      // Serial.printf("Progress: %u%%\r", (progress / (total / 100)));
    })
    .onError([](ota_error_t error) {
      // Serial.printf("Error[%u]: ", error);
      // if (error == OTA_AUTH_ERROR) Serial.println("Auth Failed");
      // else if (error == OTA_BEGIN_ERROR) Serial.println("Begin Failed");
      // else if (error == OTA_CONNECT_ERROR) Serial.println("Connect Failed");
      // else if (error == OTA_RECEIVE_ERROR) Serial.println("Receive Failed");
      // else if (error == OTA_END_ERROR) Serial.println("End Failed");
    });

  ArduinoOTA.begin();
}

void showSettings() {

  const int32_t numberOfSettingsShownOnSettingScreen = 10;  // count includes the Exit selection; the actual Settings and the Setting options can be seen below
  const int32_t maxOptionsPerSetting = 7;                   // each Setting has up to this many options

  static String selection[numberOfSettingsShownOnSettingScreen][maxOptionsPerSetting];
  static int selectectionsChosenOption[numberOfSettingsShownOnSettingScreen];

  const int32_t spacingBetweenLines = 15;

  static bool initialized = false;

  bool aButtonHasBeenPushed = true;

  int currentSettingIndex = 0;
  int currentOptionIndex = 0;

  if (!initialized) {

    // this only needs to be done once

    for (int i = 0; i < numberOfSettingsShownOnSettingScreen; i++)
      for (int j = 0; j < maxOptionsPerSetting; j++)
        selection[i][j] = "";

    for (int i = 0; i < numberOfSettingsShownOnSettingScreen; i++)
      selectectionsChosenOption[i] = 0;

    selection[0][0] = "Show computer name";
    selection[0][1] = "Don't show computer name";

    selection[1][0] = "Show LAN IP Address";
    selection[1][1] = "Show external IP Address";
    selection[1][2] = "Don't show an IP Address";

    selection[2][0] = "Show average temperature in Celsius";
    selection[2][1] = "Show maximum temperature in Celsius";
    selection[2][2] = "Show average temperature in Fahrenheit";
    selection[2][3] = "Show maximum temperature in Fahrenheit";
    selection[2][4] = "Don't show temperature";

    selection[3][0] = "Show percent of memory used";
    selection[3][1] = "Don't show percent of memory used";

    selection[4][0] = "Show 12H time with am/pm with seconds";
    selection[4][1] = "Show 12H time with am/pm without seconds";
    selection[4][2] = "Show 12H time without am/pm with seconds";
    selection[4][3] = "Show 12H time without am/pm without seconds";
    selection[4][4] = "Show 24H time with seconds";
    selection[4][5] = "Show 24H time without seconds";
    selection[4][6] = "Don't show time";

    selection[5][0] = "Show date";
    selection[5][1] = "Don't show date";

    selection[6][0] = "Show historical average CPU graph";
    selection[6][1] = "Don't show historical average CPU graph";

    selection[7][0] = "Show CPU bar graph";
    selection[7][1] = "Don't show CPU bar graph";

    selection[8][0] = "Save these settings";
    selection[8][1] = "Discard changes";
    selection[8][2] = "Restore factory defaults";

    selection[9][0] = "Exit";

    initialized = true;
  };

  LoadSettingsFromNonVolatileMemory();

  if (showComputerName)
    selectectionsChosenOption[0] = 0;
  else
    selectectionsChosenOption[0] = 1;

  if (showIPAddress)
    if (showLANAddress)
      selectectionsChosenOption[1] = 0;
    else
      selectectionsChosenOption[1] = 1;
  else
    selectectionsChosenOption[1] = 2;

  if (showTemperature)
    if (showTemperatureInCelsius)
      if (showAverageTemperature)
        selectectionsChosenOption[2] = 0;
      else
        selectectionsChosenOption[2] = 1;
    else if (showAverageTemperature)
      selectectionsChosenOption[2] = 2;
    else
      selectectionsChosenOption[2] = 3;
  else
    selectectionsChosenOption[2] = 4;

  if (showPercentOfMemoryUsed)
    selectectionsChosenOption[3] = 0;
  else
    selectectionsChosenOption[3] = 1;

  if (showTime && showTimeIn12HourFormat && showAmPmIndicator && showSeconds) selectectionsChosenOption[4] = 0;
  else if (showTime && showTimeIn12HourFormat && showAmPmIndicator && !showSeconds) selectectionsChosenOption[4] = 1;
  else if (showTime && showTimeIn12HourFormat && !showAmPmIndicator && showSeconds) selectectionsChosenOption[4] = 2;
  else if (showTime && showTimeIn12HourFormat && !showAmPmIndicator && !showSeconds) selectectionsChosenOption[4] = 3;
  else if (showTime && !showTimeIn12HourFormat && showSeconds) selectectionsChosenOption[4] = 4;
  else if (showTime && !showTimeIn12HourFormat && !showSeconds) selectectionsChosenOption[4] = 5;
  else selectectionsChosenOption[4] = 6;

  if (showDate)
    selectectionsChosenOption[5] = 0;
  else
    selectectionsChosenOption[5] = 1;

  if (showHistoricalLineGraph)
    selectectionsChosenOption[6] = 0;
  else
    selectectionsChosenOption[6] = 1;

  if (showCurrentCPUBarGraphs)
    selectectionsChosenOption[7] = 0;
  else
    selectectionsChosenOption[7] = 1;

  selectectionsChosenOption[8] = 0;

  // contine to loop here until the user chooses to exit the Settings windows

  while (showTheSettingsScreen) {

    if (checkButton(TOP_BUTTON)) {

      aButtonHasBeenPushed = true;

      if (currentSettingIndex == (numberOfSettingsShownOnSettingScreen - 1))

        // User has choosen to return to the top setting
        currentSettingIndex = 0;

      else

        // User has choosen to advance to the mext setting
        currentSettingIndex++;
    };

    if (checkButton(BOTTOM_BUTTON)) {

      if (currentSettingIndex == (numberOfSettingsShownOnSettingScreen - 1)) {

        // User has chosen to exit

        if (selectectionsChosenOption[numberOfSettingsShownOnSettingScreen - 2] == 0) {

          // save settings

          showComputerName = (selectectionsChosenOption[0] == 0);

          showIPAddress = (selectectionsChosenOption[1] < 2);
          showLANAddress = (selectectionsChosenOption[1] == 0);

          showTemperature = (selectectionsChosenOption[2] < 4);
          showAverageTemperature = ((selectectionsChosenOption[2] == 0) || (selectectionsChosenOption[2] == 2));
          showTemperatureInCelsius = (selectectionsChosenOption[2] < 2);

          showPercentOfMemoryUsed = (selectectionsChosenOption[3] == 0);

          showTime = (selectectionsChosenOption[4] < 6);
          showTimeIn12HourFormat = (selectectionsChosenOption[4] < 4);
          showAmPmIndicator = (selectectionsChosenOption[4] < 2);
          showSeconds = ((selectectionsChosenOption[4] == 0) || (selectectionsChosenOption[4] == 2) || (selectectionsChosenOption[4] == 4));

          showDate = (selectectionsChosenOption[5] == 0);

          showHistoricalLineGraph = (selectectionsChosenOption[6] == 0);

          showCurrentCPUBarGraphs = (selectectionsChosenOption[7] == 0);

          StoreSettingsInNonVolatileMemory();
        };

        if (selectectionsChosenOption[numberOfSettingsShownOnSettingScreen - 2] == 2) {

          // retore factory defaults

          showComputerName = SHOW_COMPUTER_NAME;

          showIPAddress = SHOW_IP_ADDRESS;
          showLANAddress = SHOW_LAN_ADDESS;

          showTemperature = SHOW_TEMPERATURE;
          showAverageTemperature = SHOW_AVERAGE_TEMPERATURE;
          showTemperatureInCelsius = SHOW_TEMPERATURE_IN_CELSIUS;

          showPercentOfMemoryUsed = SHOW_PERCENT_OF_MEMORY_USED;

          showTime = SHOW_TIME;
          showTimeIn12HourFormat = SHOW_TIME_IN_TWELVE_HOUR_FORMAT;
          showAmPmIndicator = SHOW_AM_PM_INDICATOR;
          showSeconds = SHOW_SECONDS;

          showDate = SHOW_DATE;

          showHistoricalLineGraph = SHOW_HISTORICAL_LINE_GRAPH;

          showCurrentCPUBarGraphs = SHOW_CURRENT_CPU_BAR_GRAPHS;

          StoreSettingsInNonVolatileMemory();
        };

        // some house keeping before returning the main processing loop:

        // prevent main loop from thinking the wifi / socket connections were down (as niether had been confirmed as up all the time the user was in the settings window)
        LastTimeWiFiWasConnected = millis();
        lastTimeDataWasReceivedFromComputer = millis();

        // zero out all historical cpu readings
        clearGraphData = true;

        showTheSettingsScreen = false;

      } else {

        // Change setting
        aButtonHasBeenPushed = true;

        currentOptionIndex = selectectionsChosenOption[currentSettingIndex] = selectectionsChosenOption[currentSettingIndex] + 1;
        if ((currentOptionIndex == maxOptionsPerSetting) || (selection[currentSettingIndex][currentOptionIndex] == ""))
          currentOptionIndex = 0;

        selectectionsChosenOption[currentSettingIndex] = currentOptionIndex;
      };
    };

    if (aButtonHasBeenPushed) {

      // update the display to reflect the change resulting from a button being pushed

      clearDisplay();

      // align text to Top Left
      sprite.setTextDatum(TL_DATUM);

      sprite.setTextColor(TFT_LIGHTGREY, TFT_BLACK);
      sprite.drawString("Settings:", 0, 0, 2);

      for (int32_t i = 0; i < numberOfSettingsShownOnSettingScreen; i++) {

        if (i == currentSettingIndex) {

          sprite.setTextColor(TFT_WHITE, TFT_BLACK);
          sprite.drawString(String("*"), 0, (i + 1) * spacingBetweenLines, 2);
          sprite.drawString(selection[i][selectectionsChosenOption[i]], 15, (i + 1) * spacingBetweenLines, 2);

        } else {

          sprite.setTextColor(TFT_LIGHTGREY, TFT_BLACK);
          sprite.drawString(selection[i][selectectionsChosenOption[i]], 15, (i + 1) * spacingBetweenLines, 2);
        }
      };

      if (currentSettingIndex == numberOfSettingsShownOnSettingScreen - 1) {

        sprite.setTextDatum(TR_DATUM);
        sprite.drawString("Return to top ->", TFT_Width, 11, 1);

        sprite.setTextDatum(BR_DATUM);
        String abbreviatedExitOption = "";
        switch (selectectionsChosenOption[numberOfSettingsShownOnSettingScreen - 2]) {
          case 0:
            {
              sprite.setTextColor(TFT_GREEN, TFT_BLACK);
              abbreviatedExitOption = "Save";
              break;
            }
          case 1:
            {
              sprite.setTextColor(TFT_YELLOW, TFT_BLACK);
              abbreviatedExitOption = "Discard";
              break;
            }
          case 2:
            {
              sprite.setTextColor(TFT_RED, TFT_BLACK);
              abbreviatedExitOption = "Restore";
              break;
            };
        };
        sprite.drawString(abbreviatedExitOption + " and exit ->", TFT_Width, TFT_Height - 12, 1);


      } else {

        sprite.setTextDatum(TR_DATUM);
        sprite.drawString("Next ->", TFT_Width, 11, 1);

        sprite.setTextDatum(BR_DATUM);

        if (currentSettingIndex == numberOfSettingsShownOnSettingScreen - 2) {
          sprite.drawString("Change ->", TFT_Width, TFT_Height - 12, 1);
        } else {
          sprite.drawString("Change setting ->", TFT_Width, TFT_Height - 12, 1);
        };
      };

      drawDisplay();

      // prevents the screen from being updated again until the user presses another button
      aButtonHasBeenPushed = false;
    };
  };
};


void showAbout() {

  // Display the About screen

  clearDisplay();

  sprite.setTextColor(TFT_WHITE, TFT_BLACK);

  sprite.setTextDatum(TR_DATUM);
  sprite.drawString("Settings ->", TFT_Width, 11, 1);

  sprite.setTextDatum(TL_DATUM);
  sprite.drawString("CPU Monitor Jr.", 202, 38, 2);
  sprite.drawString("v2", 307, 44, 1);
  sprite.drawString("Copyright", 202, 54, 2);
  sprite.drawString("Rob Latour, 2022", 202, 70, 2);
  sprite.drawString("rlatour.com", 202, 86, 2);

  String message;
  message = "  IP: ";
  message.concat(WiFi.localIP().toString());
  sprite.drawString(message, 202, 120, 1);
  message = "Port: ";
  message.concat(String(UDP_PORT));
  sprite.drawString(message, 202, 130, 1);

  sprite.setTextDatum(BR_DATUM);
  sprite.drawString("Main screen ->", TFT_Width, TFT_Height - 12, 1);

  // I woun't be heart broken if you remove this next line :-)
  sprite.pushImage(0, 0, 192, 170, image_data_Rob);

  drawDisplay();

  while (showTheAboutScreen) {

    delay(50);

    if (checkButton(TOP_BUTTON)) {
      showTheAboutScreen = false;
      showTheSettingsScreen = true;
    };

    if (checkButton(BOTTOM_BUTTON))
      showTheAboutScreen = false;
  };
}


void setup() {

  setupSerial_Monitor();

  SetupEEPROM();

  setupButtons();

  setupDisplay();

  setupWiFi();

  setupTime();

  setupOTAUpdate();

  setupWebSocket();

  setupUDP();

  Advertise();

  setupComplete();
}

void loop() {

  // unsigned long startloop = millis();

  checkWiFiConnection();

  checkWebSocket();

  checkButtonsOnMainWindow();

  if (showTheAboutScreen)
    showAbout();

  if (showTheSettingsScreen)
    showSettings();

  clearDisplay();

  if (currentReadingsAreAvailable) {

    updateConnectedComputer();

    updateTemperature();

    updatePercentOfMemoryUsed();

    updateGraph();

  } else {

    updateNotConnectedMessage();
  };

  updateClock();

  drawDisplay();

  ArduinoOTA.handle();

  // loop time should be about 35ms regardless of if incomming data is being processed or not
  // unsigned long endloop = millis();
  // if (DEBUG_IS_ON) Serial.println("Loop time: " + String(endloop - startloop));
}