cls
setlocal EnableExtensions EnableDelayedExpansion

set "INSTALLUTIL=E:\Documents\VBNet\CPUMonitorJr\CPUMonitorJr\Install_and_Uninstall_Files\InstallUtil.exe"
set "SERVICE_EXE=E:\Documents\VBNet\CPUMonitorJr\CPUMonitorJr\bin\Release\CPUMonitorJR.exe"
set "WINGET_EXE=winget"

echo Stopping CPUMonitorJr...
sc query CPUMonitorJr >nul 2>&1
if !errorlevel! equ 0 (
    sc query CPUMonitorJr | find "RUNNING" >nul 2>&1
    if !errorlevel! equ 0 (
        net stop CPUMonitorJr >nul 2>&1
    )

    "%INSTALLUTIL%" /u "%SERVICE_EXE%"
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
