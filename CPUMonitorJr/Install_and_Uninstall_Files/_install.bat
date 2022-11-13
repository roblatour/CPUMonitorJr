cls
InstallUtil.exe "..\bin\Release\CPUMonitorJR.exe"
sc description CPUMonitorJr "Server side of CPU Monitor Jr displaying CPU performance on an ESP32 driven display"
sc config CPUMonitorJr start=auto
rem net start CPUMonitorJr 
time /t
pause

