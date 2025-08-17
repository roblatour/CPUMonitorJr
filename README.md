


# CPU Monitor Jr

CPU Monitor Jr. is an open source project for monitoring the performance of a Windows or Linux computer using a WiFi connected ESP32 board and TFT display.

It uses of two platforms. 

The first is either Windows, with a Windows service gathering and sharing the computer's performance data, or Linux, with a Linux service gathering and sharing the computer's performance data.  

The second is an ESP32, displaying that data on a TFT display.

Below is an example of two (ESP32) LilyGo T-Display S3 devices each monitoring a separate computer at the same time.  Displayed are the names and LAN IP addresses of each computer, as well as, their CPU temperature, memory percentage used, historical total and current core loads. The top device is monitoring a four core Windows computer, the bottom device is monitoring a 24 (48 virtual) core Windows computer.

![TFT Display](/images/TFT.gif)

Monitoring begins as soon as the computer is turned on.

To get the CPU temperature CPU Monitor Jr.'s Windows service uses either [Open Hardware Monitor](https://openhardwaremonitor.org/) or [Core Temp](https://www.alcpu.com/CoreTemp/).  If Core Temp is used, the Core Temp program must also be running. This is explained in more detail, along with other aspects of the project, in the following video:

The Core Temp program can be download from [here](https://www.alcpu.com/CoreTemp/). 

The above file is not needed when running CPUMonitorJR on Linux.

To change the Windows server, edit the file CPUMonitorJr.sln in this repository using Visual Studio
To change the Linux server, edit the file cpumonitorjr.py in this repository in your favourite python/text editor
To change the Arduio sketches, open up the .ino file in Arduino


**As for the hardware ...**

Below are the (affiliated) links for the components used to put this project together:

Version 2:
- [LILYGO T-Display S3](https://s.click.aliexpress.com/e/_DexRAdn) 

<br>
Version 1.1 (designed for an LCD display)

- [ESP32 DOIT Devkit v1](https://www.aliexpress.com/item/1005004268911484.html) 

- [1602 LCD with I2C module](https://s.click.aliexpress.com/e/_DdQpVNT) (please ensure that the LCD monitor with the IC2 module is selected) 
- [DS3231 RTC with AT24C32 IIC module](https://s.click.aliexpress.com/e/_DcRLOPT) 

<br>
Here too are some 3D printable cases I designed for the two versions:

- [Version 2](https://www.printables.com/model/339237-lilygo-t-display-s3-without-header-pins-case)  <br>
- [Version 1.1](https://www.printables.com/model/339266-enclosure-lcd-1602-esp32-ds3232rtc-i2c)

<br>

**I hope this project will be of good use to you!**

## Support CPU Monitor Jr

[<img alt="buy me  a coffee" width="200px" src="https://cdn.buymeacoffee.com/buttons/v2/default-blue.png" />](https://www.buymeacoffee.com/roblatour)

