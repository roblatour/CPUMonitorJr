cls
setlocal EnableExtensions EnableDelayedExpansion

echo Stopping CPUMonitorJr...
sc query CPUMonitorJr >nul 2>&1
if !errorlevel! equ 0 (
    sc query CPUMonitorJr | find "RUNNING" >nul 2>&1
    if !errorlevel! equ 0 (
        net stop CPUMonitorJr
    ) else (
        echo CPUMonitorJr is not running.
    )
) else (
    echo CPUMonitorJr service not found.
)

echo Stopping PawnIO...
sc query pawnio >nul 2>&1
if !errorlevel! equ 0 (
    sc query pawnio | find "RUNNING" >nul 2>&1
    if !errorlevel! equ 0 (
        net stop pawnio
    ) else (
        echo PawnIO is not running.
    )
) else (
    echo PawnIO service not found.
)

time /t
endlocal
