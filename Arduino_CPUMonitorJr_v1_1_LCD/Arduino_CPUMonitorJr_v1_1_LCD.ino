// CPU Monitor Jr v1.1 (for LCD Display)
//
// Copyright Rob Latour, 2022
// htts://raltour.com/cpumonitorjr
// https://github.com/roblatour/CPUMonitorJr
//
// 
// v1.1 updated to reflect changes made to the PC server side of CPUMONJRv2
// v1   original release supporting LCD display
//
// tested with a doit esp32 devkit v1 board and
// both a 1602 and 2004 LCD dislay (seperately) driven by an I2C Serial Board module;
//
// code below is set up for a LCD 1602 display but is easily changed below in the User Settings section
//
// tested with a real windows machine reporting 8 CPUs, and in theory with one reporting 16 CPUs
// if your windows machine has more that 16 CPU changes will be required to this program
//
// also making use of the following two optional hardware components:
//  DS3231 AT24C32 IIC Module Precision Real Time Clock Memory Module
//
//
// Hardware connections:
//
// connect:
//
// ESP32        IC2 module
// ------------ ----------
// VCC (5.5)    VCC
// GRD          GRD
// D21          SDA
// D22          SCL
//
//
// optionally you may also connect a DS3231 Real Time Clock
// to allow the device to retain the time through a power failure.  However, time will be reset once device can connect to your computer via a websocket once again, added for impatient people :-)
// ESP32        RTC
// ------------ ----------
// 3.3V         VCC
// GRD          GRD
// D21          SDA
// D22          SCL
//
// (yes, D21 and D22 need to each be connected to the IC2 module and the RTC module - you need to join up some wires for this)
//

#include <Wire.h>
#include <Arduino.h>
#include <WiFi.h>
#include <WiFiMulti.h>
#include <WiFiClientSecure.h>
#include <WebSocketsClient.h>
#include "DS3232RTC.h"  // https://github.com/JChristensen/DS3232RTC
#include <time.h>
#include <WiFiUdp.h>
#include <ArduinoOTA.h>
#include "ESPAsyncWebServer.h"  // https://github.com/me-no-dev/ESPAsyncWebServer
#include <LiquidCrystal_I2C.h>  // https://github.com/johnrickman/LiquidCrystal_I2C ( forked from https://github.com/fdebrabander/Arduino-LiquidCrystal-I2C-library )

//----------------------------------------------------------------------------------------------------------
// User Settings:

const char* wifi_name = "WifiNetworkNameGoesHere";         // Wifi network name
const char* wifi_pass = "YourWifeNetworkPasswordGoesHere"; // Wifi password

const int lcdColumns = 16;                                 // number of columns on your LCD display (typically 16 or 20)
const int lcdRows = 2;                                     // number of rows on your LCD display (typically 2 or 4)
const int lcdI2CAddress = 0x27;                            // lcd I2C address; if you don't know your display address, run an I2C scanner sketch

const int udpPort = 44445;                                 // UDP port used to let your computer know your esp32's ip address so your computer can establish a websocket link
const char* udpAddress = "255.255.255.255";                // UDP broadcast address
														   // use "255.255.255.255" if the computer being monitored has a dynamic IP address 
                                                           // use the IP address of the computer being monitored if it has a static IP address
														   // note: not all networks support broadcasting to all devices, if this is your case please set your computer to use a static
														   //       IP address and use that address in this field

const bool reportInFahrenheit = false;                     // if true will report in Farenheit, if false will report in celsius
const bool showTempUnits = false;                          // if true will show 'C' or 'F' after the degree symbol, if false the degree sign will be shown but no 'F' or 'C' after it
const bool showMilitaryTime = false;                       // if true will show 24 hour formatted time, if false will show 12 hour formatted time

const int  secondsSinceLastUpdateReceivedToShowTime = 5;   // if communications have not been recieved from the computer after this many seconds, then show date and time screen rather than bar graph screen
const unsigned long AdvertiseThreshold = 15;               // if no data is received from the computer after this many seconds, make a request for it 

const unsigned long DeviceResetThreshold = 10 * 60 * 1000; // if communications / wifi connections is not available for this period of time (ten minutes default) reset the wifi connection

const char* OTAHostName = "ESP32CPUMonitorJr";             // Host name for Over The Air (OTA) updates
const char* OTAPassword = "CPUMonitorJr";                  // Password for OTA updates

// the following will be automatically set by the program:

uint8_t numberOfCPUs;

bool currentReadingHasChangedFlag;
String currentCPUReadings[lcdRows];
double currentCPUAverage;
String currentTempReading;

const int bottomRow = lcdRows - 1;
const int barsPerSegment = 8;
float scalingFactor;

int secondsSinceLastUpdateReceivedFromComputer = 0;
int secondsSinceLastAdvertisementWasNeeded = 0;

//----------------------------------------------------------------------------------------------------------
// Real Time Clock


DS3232RTC rtc;

void setupTime() {

    // Set system date and time from the real time clock

    rtc.begin();

    time_t rtcTime = rtc.get();

    if (rtcTime > 0) {
        setTime(rtcTime);
    }

}

//----------------------------------------------------------------------------------------------------------

LiquidCrystal_I2C lcd(lcdI2CAddress, lcdColumns, lcdRows);

#define noBars        ' '
#define oneBar        char(0)
#define twoBars       char(1)
#define threeBars     char(2)
#define fourBars      char(3)
#define fiveBars      char(4)
#define sixBars       char(5)
#define sevenBars     char(6)
#define eightBars     char(255)
#define DegreeSymbol  char(7)
#define PercentSymbol char(37)


uint8_t x1[8] = { 0x0,  0x0,  0x0,  0x0,  0x0,  0x0,  0x00, 0x1F };    // bottom 1 row turned on
uint8_t x2[8] = { 0x0,  0x0,  0x0,  0x0,  0x0,  0x0,  0x1F, 0x1F };    // bottom 2 rows turned on
uint8_t x3[8] = { 0x0,  0x0,  0x0,  0x0,  0x0,  0x1F, 0x1F, 0x1F };    // bottom 3 rows turned on
uint8_t x4[8] = { 0x0,  0x0,  0x0,  0x0,  0x1F, 0x1F, 0x1F, 0x1F };    // bottom 4 rows turned on
uint8_t x5[8] = { 0x0,  0x0,  0x0,  0x1F, 0x1F, 0x1F, 0x1F, 0x1F };    // bottom 5 rows turned on
uint8_t x6[8] = { 0x0,  0x0,  0x1F, 0x1F, 0x1F, 0x1F, 0x1F, 0x1F };    // bottom 6 rows turned on
uint8_t x7[8] = { 0x0,  0x1F, 0x1F, 0x1F, 0x1F, 0x1F, 0x1F, 0x1F };    // bottom 7 rows turned on
// all 8 rows turned on - use char 255
uint8_t Degree[8] = { 0x4, 0xA, 0x4, 0x0, 0x0, 0x0, 0x0, 0x0 };        // Degree symbol

void setupDisplay() {

    lcd.begin(lcdColumns, lcdRows);
    lcd.init();
    lcd.backlight();
    lcd.noAutoscroll();
    lcd.noCursor();

    lcd.createChar(0, x1);
    lcd.createChar(1, x2);
    lcd.createChar(2, x3);
    lcd.createChar(3, x4);
    lcd.createChar(4, x5);
    lcd.createChar(5, x6);
    lcd.createChar(6, x7);
    lcd.createChar(7, Degree);

    lcd.clear();
    lcd.home();


    //  scalingFactor = round up to the nearest integer( 100 / lcdRows / barsPerSegment )
    //  scalingFactor = round( 100 / ( lcdRows * barsPerSegment) + 0.5);
    scalingFactor = 100.0 / float(lcdRows * barsPerSegment);

    // Serial.print("scaling factor "); Serial.println(scalingFactor);

    currentReadingHasChangedFlag = false;

}

void updateDisplay() {

    //
    // There are two screens that can shown:
    //   1. the CPU and temperature screen, and
    //   2. the time and date screen.
    //
    // The CPU and temperature screen will be show as long as this program continues to recieve status from the computer
    // otherwise the time and date screen will be shown.
    //
    // When an update from the computer is received the variable secondsSinceLastUpdateReceivedFromComputer is set to zero.
    //
    // However, the code below keeps adding 1 to the secondsSinceLastUpdateReceivedFromComputer.
    //
    // In that way as long as the computer data is being received the variable secondsSinceLastUpdateReceivedFromComputer should not exceed one or two seconds.
    //
    // If the secondsSinceLastUpdateReceivedFromComputer exceeds five seconds (default can be changed in user setting) it means the computer date is not being received and in that case
    // the date and time screen will be shown rather than the CPU and temperature screen.
    //
    // Having that said,once the data starts coming back in then the code below will revert to displaying the CPU and temperature screen.
    //

    uint8_t thisSecond = second();
    uint8_t static previousSecond = 61;

    bool currentlyShowingTime = true;
    static bool previousilyShowingTime = true;

    if (thisSecond != previousSecond) {
        secondsSinceLastUpdateReceivedFromComputer += 1;
        previousSecond = thisSecond;
    }

    currentlyShowingTime = (secondsSinceLastUpdateReceivedFromComputer > secondsSinceLastUpdateReceivedToShowTime);

    if (currentlyShowingTime != previousilyShowingTime) {
        previousilyShowingTime = currentlyShowingTime;
        lcd.clear();
    }

    if (currentlyShowingTime) {
        showShowDateAndTime();
    } else {
        showCPUandTempertature();
    }

}

void lcdCentrePrint(int row, String message) {

    int column = ((lcdColumns - message.length()) / 2);
    lcd.setCursor(column, row);
    lcd.print(message);

}


void showShowDateAndTime() {

    uint8_t thisSecond;
    uint8_t static previousSecond = 61;

    thisSecond = second();

    if (thisSecond != previousSecond) {

        previousSecond = thisSecond;

        const int EpocYear = 1970;

        time_t t = now();

        tmElements_t myTime;

        myTime.Year = year(t) - EpocYear;
        myTime.Month = month(t);
        myTime.Day = day(t);
        myTime.Hour = hour(t);
        myTime.Minute = minute(t);
        myTime.Second = second(t);
        myTime.Wday = weekday(t);

        uint8_t RevisedHour = myTime.Hour;

        char WorkingString[16];
        String displayTime;

        if (showMilitaryTime) {

            char TimeString[9];
            sprintf(TimeString, "%02u:%02u:%02u", myTime.Hour, myTime.Minute, myTime.Second);
            displayTime = String(TimeString);

        } 
        else
        {

            String AM_PM = "a.m.";

            if (RevisedHour >= 12)
            {
                RevisedHour = RevisedHour - 12;
                AM_PM = "p.m.";
            }

            if (RevisedHour == 0)
            {
                RevisedHour = 12;
            }

            sprintf(WorkingString, " %01u:%02u:%02u %s ", RevisedHour, myTime.Minute, myTime.Second, AM_PM);
            displayTime = String(WorkingString);

        };

        sprintf(WorkingString, "%4u-%02u-%02u", (int)myTime.Year + EpocYear, (int)myTime.Month, (int)myTime.Day);
        String displayDate = String(WorkingString);

        lcdCentrePrint(0, displayTime);
        lcdCentrePrint(1, displayDate);

    }

}

void showCPUandTempertature() {

    if (currentReadingHasChangedFlag) {

        // show performance bars
        for (int r = 0; r < lcdRows; r++) {
            lcd.setCursor(0, r);
	    lcd.print(currentCPUReadings[r]);
        }

        if ((lcdColumns == 16) && (numberOfCPUs > 8)) {

            // if the computer has more than 8 CPUs
            // and you are using a LCD display with only 16 columns then
            // there will not be enough room to also show the CPU average and temperarture
            // and only the bar graph will be shown

        }
        else {

      // clear the first two digits of the old average CPU value off the screen
      // this to ensure old values don't remain displayed when unwanted
      int cpuCol = lcdColumns - 4;
      lcd.setCursor(cpuCol, 0);
      lcd.print("  ");

      // show average CPU usage
      int iUsage = round(currentCPUAverage);
      String sUsage = String(iUsage) + PercentSymbol;
      cpuCol = lcdColumns - sUsage.length();
      lcd.setCursor(cpuCol, 0);
      lcd.print(sUsage);

      // show temperature
      // clearing previousily higher values in not needed; as this is effectively handed in the 'updateTemperature' routine below
      int tempCol = lcdColumns - currentTempReading.length();
      lcd.setCursor(tempCol, lcdRows - 1);
      lcd.print(currentTempReading);

            currentReadingHasChangedFlag = false;

        }

    }

}

//----------------------------------------------------------------------------------------------------------

void updateTemperature(byte WholeNumber, byte DecimalNumber) {

  // if there are > 15 CPUs room to display the space it take to display the temperature must be minimized, so the value will be rounded and the decimals removed
  // temperature is passed from the computer in Celsius, so if the option to display in Fahrenheith is choosen in the user settings the temperature value is converted here

    char formatbuffer[9];

    if (reportInFahrenheit) {

        if (numberOfCPUs > 15) {
            int roundedNumber = round(double(WholeNumber * 100 + DecimalNumber) / 100);
            sprintf(formatbuffer, "%3d%c", roundedNumber, DegreeSymbol);
        }
        else {
            sprintf(formatbuffer, "%3d.%2d%c", WholeNumber, DecimalNumber, DegreeSymbol);
        }

        currentTempReading = formatbuffer;

    }
    else {

    double celsius = double(WholeNumber * 100 + DecimalNumber) / double(100);
    double fahrenheit = double(celsius) * double(9) / double(5) + double(32);

    if (numberOfCPUs > 15) {
      int icelsius = round(celsius);
      sprintf(formatbuffer, "%3d", icelsius);
    } else {
      dtostrf(celsius, 5, 2, formatbuffer);
    }

        currentTempReading = formatbuffer;
        currentTempReading += DegreeSymbol;

    }

    if ((showTempUnits) && (numberOfCPUs <= 15)) {

        if (reportInFahrenheit) {
            currentTempReading += 'F';
        }
        else {
            currentTempReading += 'C';
        }

    }

}

void updateBarGraph(uint8_t CPUValue[]) {

    // clear the bar graph

    for (int r = 0; r < lcdRows; r++)
        currentCPUReadings[r] = "";

    // load the Bar Graph matrix ( currentCPUReadings[] )
    //
    // loading from left to right, column by column, and within each
    // column loading from the bottom most row to the top most row

    for (int c = 0; c < numberOfCPUs; c++) {

        int thisCPUValue = CPUValue[c];
        int numberOfBarsNeeded = thisCPUValue / scalingFactor;
        int r = bottomRow;

        while ((r >= 0) && (numberOfBarsNeeded >= 0)) {

            switch (numberOfBarsNeeded) {

            case 0:
                currentCPUReadings[r] += noBars;
                break;

            case 1:
                currentCPUReadings[r] += oneBar;
                break;

            case 2:
                currentCPUReadings[r] += twoBars;
                break;

            case 3:
                currentCPUReadings[r] += threeBars;
                break;

            case 4:
                currentCPUReadings[r] += fourBars;
                break;

            case 5:
                currentCPUReadings[r] += fiveBars;
                break;

            case 6:
                currentCPUReadings[r] += sixBars;
                break;

            case 7:
                currentCPUReadings[r] += sevenBars;
                break;

            default:
                currentCPUReadings[r] += eightBars;
                break;

            };

            r -= 1;
            numberOfBarsNeeded -= barsPerSegment;

        }

        // set any remaining rows, which were not set above, to conatin a 'nobars' character
        for (int r1 = r; r1 >= 0; r1--) {
            currentCPUReadings[r1] += noBars;
        }

    }

}

//----------------------------------------------------------------------------------------------------------

//Wifi

unsigned long LastTimeWiFiWasConnected;

const int External_Button = 19;

WebSocketsClient webSocket;

AsyncWebServer server(80);
AsyncWebSocket ws("/cpumonitorjr" + String(udpPort));

void setupWiFi() {

    bool notyetconnected = true;
    int  counter;

    int TimeBeforeRetryingInSeconds = 3;

    // String message;

    // Serial.println("Attempting to connect to WiFi");

  lcd.clear();
  lcd.setCursor(0, 0);
  lcd.print("Connecting to");
  lcd.setCursor(0, 1);
  lcd.print("WiFi ");

    while (notyetconnected)
    {

        WiFi.mode(WIFI_STA);
        WiFi.begin(wifi_name, wifi_pass);
        delay(1000);

        counter = 0;

        while ((WiFi.status() != WL_CONNECTED) && (counter < TimeBeforeRetryingInSeconds))
        {
            delay(1000);
            lcd.print(".");
            counter++;
        }

        if (WiFi.status() == WL_CONNECTED)
        {
            notyetconnected = false;
            LastTimeWiFiWasConnected = millis();
        }
        else
        {
            TimeBeforeRetryingInSeconds++;

            // Serial.println("Attempting to connect to WiFi");

            WiFi.disconnect(true);
            delay(1000);

            WiFi.mode(WIFI_STA);
            delay(1000);
        }

    };

    // message = "Connected to ";
    // message.concat(wifi_name);
    // message.concat(" at IP address ");
    // message.concat(WiFi.localIP().toString());
    // Serial.println(message);

    if (lcdRows == 2) {
        lcd.clear();
        lcd.setCursor(0, 0); lcd.print("Connected to");
        lcd.setCursor(0, 1); lcd.print(WiFi.localIP().toString());
    }
    else
    {
        lcd.setCursor(0, 3); lcd.print("Connected to");
        lcd.setCursor(0, 4); lcd.print(WiFi.localIP().toString());
    }
	
	 delay(1500);

};

void checkWiFiConnection() {

    // if connection has been out for over two minutes restart

    if (WiFi.status() == WL_CONNECTED) {
        LastTimeWiFiWasConnected = millis();
    }
    else
    {
        if ((millis() - LastTimeWiFiWasConnected) > DeviceResetThreshold) {
            ESP.restart();
        }

    }

}

//----------------------------------------------------------------------------------------------------------

WiFiUDP udp;

void setupUDP() {

    udp.begin(udpPort);

}

void Advertise() {

    // used to let this program tell your computer what it's ip address is so that a websocket link can be (re)established
    // also when a websocket link is (re)established the time will be updated based on your windows computer's time

    secondsSinceLastAdvertisementWasNeeded = 0;

    String message;

    message = "CPUMonitorJr;";
    message.concat(WiFi.localIP().toString());
    message.concat("; ");

    int len = message.length();

    uint8_t buffer[len];

    message.getBytes(buffer, len);

    udp.beginPacket(udpAddress, udpPort);
    udp.write(buffer, len - 1);
    udp.endPacket();

    memset(buffer, 0, len - 1);

    delay(450); // do not remove this pause

}

//----------------------------------------------------------------------------------------------------------

void onWsEvent(AsyncWebSocket* server, AsyncWebSocketClient* client, AwsEventType type, void* arg, uint8_t* data, size_t len) {

    // used to handle messages sent from your windows computer

    if (type == WS_EVT_CONNECT) {

        // Websocket client connection received

    }
    else if (type == WS_EVT_DISCONNECT) {

        // Client disconnected
        secondsSinceLastUpdateReceivedFromComputer = secondsSinceLastUpdateReceivedToShowTime + 1;

    }

    else if (type == WS_EVT_DATA) {

        int tranactionCode = data[0];  // trans code 0 = date and time; trans code 1 = cpu and temperature readings

        switch (tranactionCode) {

        case 0:

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

            int revisedYear;
            revisedYear = data[1];
            revisedYear += 2000;

            // set esp32's time
            setTime(data[5], data[6], data[7], data[3], data[2], revisedYear);

            // set the real time clock's time
            rtc.set(now());

            break;

      case 1:
        /*
            byte 0                  = is a '1' to represent this is a Computer name and IP address stream
            byte 1 ... byte n       = Computer name
            byte n + 1              = ';'
            byte n + 2 ... byte m   = LAN IP Address 
            byte m + 1              = ';'
            byte m + 2 ... byte z   = External IP Address 
            final byte              = ';'
          */
        break;

      case 2:

        /*
            byte 0 = is a '2' to represent this is a temperature and cpu data stream 
            byte 1 = percent of memory used whole number
            byte 2 = percent of memory used decimal
            byte 3 = average temp whole number
            byte 4 = average temp decimal
            byte 3 = max temp whole number
            byte 5 = max temp decimal
            byte 7 = number of cpus
            byte 8 and on  = cpu busy of each cpu
          */

        secondsSinceLastUpdateReceivedFromComputer = 0;
        secondsSinceLastAdvertisementWasNeeded = 0;

        // update the average tempurature from passed data
        // whole part of the temperature value is in byte 1, decimal part is in byte 2
        updateTemperature(data[3], data[4]);

        // byte 7 contains the number of CPUs
        // byte 8 and beyond contain the individual percent busy CPU ratings for each cpu

        // load CPUValues into an array to pass to updateBarGraph
        numberOfCPUs = data[7];
        uint8_t CPUValues[numberOfCPUs];
        for (uint8_t i = 0; i < numberOfCPUs; i++)
          CPUValues[i] = data[i + 8];

            // update the display's bar chart
            updateBarGraph(CPUValues);

            // calculate average CPU value
            double workingTotal = 0;
            for (uint8_t i = 0; i < numberOfCPUs; i++)
                workingTotal += CPUValues[i];

            currentCPUAverage = workingTotal / numberOfCPUs;

            // set this flaf as a trigger to update the display
            currentReadingHasChangedFlag = true;

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

    uint8_t thisSecond;
    uint8_t static previousSecond = 61;

    thisSecond = second();

    if (thisSecond != previousSecond) {
        previousSecond = thisSecond;
        secondsSinceLastAdvertisementWasNeeded++;
    }

    if (secondsSinceLastAdvertisementWasNeeded > AdvertiseThreshold) {
        Advertise();
    }

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
        else // U_SPIFFS
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

//----------------------------------------------------------------------------------------------------------

void setup()
{

    // uncomment if you want to add in tracing / debuging:
    // Serial.begin(115200);
    // delay(100);
    // Serial.println("Starting");

    setupTime();
    setupDisplay();
    setupWiFi();
    setupOTAUpdate();
    setupUDP();
    setupWebSocket();
    Advertise();

}

void loop() {

    checkWiFiConnection();
    checkWebSocket();

    updateDisplay();

    ArduinoOTA.handle();

}
