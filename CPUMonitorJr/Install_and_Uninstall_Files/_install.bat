cls
setlocal EnableExtensions EnableDelayedExpansion

set "INSTALLUTIL=E:\Documents\VBNet\CPUMonitorJr\CPUMonitorJr\Install_and_Uninstall_Files\InstallUtil.exe"
set "SERVICE_EXE=E:\Documents\VBNet\CPUMonitorJr\CPUMonitorJr\bin\Release\CPUMonitorJR.exe"
set "PAWNIO_SETUP_URL=https://github.com/namazso/PawnIO.Setup/releases/download/2.1.0/PawnIO_setup.exe"
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
sc description CPUMonitorJr "Server side of CPU Monitor Jr displaying CPU performance on an ESP32 driven TFT display"
sc config CPUMonitorJr start= auto
rem net start CPUMonitorJr

time /t
pause
endlocal
