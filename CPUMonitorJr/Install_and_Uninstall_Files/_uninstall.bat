cls
setlocal EnableExtensions EnableDelayedExpansion

set "INSTALLUTIL=%~dp0InstallUtil.exe"
set "SERVICE_EXE=%~dp0..\bin\Release\CPUMonitorJR.exe"
for %%I in ("%SERVICE_EXE%") do set "SERVICE_DIR=%%~dpI"
set "WINGET_EXE=winget"

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

echo Stopping CPUMonitorJr...
sc query CPUMonitorJr >nul 2>&1
if !errorlevel! equ 0 (
    sc query CPUMonitorJr | find "RUNNING" >nul 2>&1
    if !errorlevel! equ 0 (
        net stop CPUMonitorJr >nul 2>&1
    )

    "%INSTALLUTIL%" /u "%SERVICE_EXE%"
    if !errorlevel! neq 0 (
        echo CPUMonitorJr uninstall failed.
        time /t
        pause
        endlocal
        exit /b 1
    )

    sc delete CPUMonitorJr >nul 2>&1
) else (
    echo CPUMonitorJr service not found.
)

echo Detecting winget...
where winget >nul 2>&1
if !errorlevel! neq 0 (
    if exist "%LOCALAPPDATA%\Microsoft\WindowsApps\winget.exe" (
        set "WINGET_EXE=%LOCALAPPDATA%\Microsoft\WindowsApps\winget.exe"
    ) else (
        set "WINGET_EXE="
    )
)

set "UNINSTALL_PAWNIO=N"
set /p UNINSTALL_PAWNIO="Uninstall PawnIO as well? (Y/N, default N): "
if "%UNINSTALL_PAWNIO%"=="" set "UNINSTALL_PAWNIO=N"

if /I "%UNINSTALL_PAWNIO%"=="Y" (
    if not "%WINGET_EXE%"=="" (
        "%WINGET_EXE%" uninstall --id namazso.PawnIO --exact --accept-source-agreements
    ) else (
        echo winget not available. Please uninstall PawnIO manually from Apps & Features.
    )
)

time /t
pause
endlocal
