cls
setlocal EnableExtensions EnableDelayedExpansion

set "INSTALLUTIL=%~dp0InstallUtil.exe"
set "SERVICE_EXE=%~dp0..\bin\Release\CPUMonitorJR.exe"
for %%I in ("%SERVICE_EXE%") do set "SERVICE_DIR=%%~dpI"
set "PAWNIO_SETUP_URL=https://github.com/namazso/PawnIO.Setup/releases/download/2.1.0/PawnIO_setup.exe"

if not exist "%INSTALLUTIL%" (
    echo InstallUtil.exe not found: "%INSTALLUTIL%"
    time /t
    pause
    endlocal
    exit /b 1
)

if not exist "%SERVICE_EXE%" (
    echo Service executable not found: "%SERVICE_EXE%"
    time /t
    pause
    endlocal
    exit /b 1
)

powershell -NoProfile -ExecutionPolicy Bypass -Command "try { $p='%SERVICE_EXE%'; $d='%SERVICE_DIR%'; if (Test-Path $p) { Unblock-File -Path $p -ErrorAction SilentlyContinue }; if (Test-Path $d) { Get-ChildItem -Path $d -File | Unblock-File -ErrorAction SilentlyContinue }; exit 0 } catch { exit 0 }"
set "PAWNIO_SETUP_PATH=%TEMP%\PawnIO_setup.exe"
set "WINGET_EXE=winget"

echo Detecting winget...
where winget >nul 2>&1
if !errorlevel! neq 0 (
    if exist "%LOCALAPPDATA%\Microsoft\WindowsApps\winget.exe" (
        set "WINGET_EXE=%LOCALAPPDATA%\Microsoft\WindowsApps\winget.exe"
    ) else (
        set "WINGET_EXE="
    )
)

echo Checking PawnIO service...
sc query pawnio >nul 2>&1
if !errorlevel! neq 0 (
    echo PawnIO not found. Installing...
    if not "%WINGET_EXE%"=="" (
        echo Installing PawnIO with winget...
        "%WINGET_EXE%" install --id namazso.PawnIO --exact --accept-package-agreements --accept-source-agreements
    ) else (
        echo winget not available. Installing PawnIO from direct download...
        powershell -NoProfile -ExecutionPolicy Bypass -Command "try { $url='%PAWNIO_SETUP_URL%'; $dst='%PAWNIO_SETUP_PATH%'; Invoke-WebRequest -UseBasicParsing -Uri $url -OutFile $dst; Start-Process -FilePath $dst -ArgumentList '/S' -Wait; exit 0 } catch { Write-Host $_.Exception.Message; exit 1 }"
    )

    if !errorlevel! neq 0 (
        echo PawnIO installation failed. Continuing with CPUMonitorJr installation.
    )
) else (
    echo PawnIO already installed.
)

echo Ensuring PawnIO is running...
sc query pawnio >nul 2>&1
if !errorlevel! equ 0 (
    sc query pawnio | find "RUNNING" >nul 2>&1
    if !errorlevel! neq 0 (
        net start pawnio >nul 2>&1
    ) else (
        echo PawnIO is already running.
    )
) else (
    echo PawnIO service not found.
)

echo Checking for existing CPUMonitorJr service...
sc query CPUMonitorJr >nul 2>&1
if !errorlevel! equ 0 (
    echo Existing CPUMonitorJr service found. Removing it before reinstall...
    sc query CPUMonitorJr | find "RUNNING" >nul 2>&1
    if !errorlevel! equ 0 (
        net stop CPUMonitorJr >nul 2>&1
    )
    "%INSTALLUTIL%" /u "%SERVICE_EXE%" >nul 2>&1
    sc delete CPUMonitorJr >nul 2>&1
    timeout /t 2 /nobreak >nul
)

echo Installing CPUMonitorJr service...
"%INSTALLUTIL%" "%SERVICE_EXE%"
if !errorlevel! neq 0 (
    echo CPUMonitorJr installation failed.
    time /t
    pause
    endlocal
    exit /b 1
)

sc query CPUMonitorJr >nul 2>&1
if !errorlevel! equ 0 (
    sc description CPUMonitorJr "Server side of CPU Monitor Jr displaying CPU performance on an ESP32 driven TFT display"
    sc config CPUMonitorJr start= auto
) else (
    echo CPUMonitorJr service was not created.
)
rem net start CPUMonitorJr

time /t
pause
endlocal
