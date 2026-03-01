cls
setlocal EnableExtensions EnableDelayedExpansion

echo Checking PawnIO service...
sc query pawnio >nul 2>&1
if !errorlevel! neq 0 (
    echo PawnIO service not found. CPUMonitorJr may not be able to read temperatures via LibreHardwareMonitor.
) else (
    sc query pawnio | find "RUNNING" >nul 2>&1
    if !errorlevel! neq 0 (
        echo Starting PawnIO...
        net start pawnio
    ) else (
        echo PawnIO is already running.
    )
)

echo Starting CPUMonitorJr...
net start CPUMonitorJr
time /t
pause
endlocal
