@echo off
:: ============================================================
:: Not Actually Extinct — Detection Server Launcher
:: ============================================================
:: Run this BEFORE launching the SpeciesDetector WPF app,
:: OR let the WPF app auto-start it (it does this automatically).
::
:: The server loads MegaDetector + SpeciesNet + BioCLIP on startup.
:: First run takes ~2 min (BioCLIP is warmed up eagerly); subsequent runs
:: are faster once model weights are cached on disk.
::
:: Press Ctrl+C to stop the server.
:: ============================================================

setlocal
set "SCRIPT_DIR=%~dp0"
set "VENV_PYTHON=%SCRIPT_DIR%..\venv\Scripts\python.exe"
set "SERVER_SCRIPT=%SCRIPT_DIR%detection_server.py"

echo.
echo  Not Actually Extinct -- Detection Server
echo  ==========================================
echo  Serving on http://127.0.0.1:5050
echo  First startup loads 3 ML models -- please wait ~2 minutes.
echo.

if not exist "%VENV_PYTHON%" (
    echo  ERROR: Python venv not found at:
    echo    %VENV_PYTHON%
    echo.
    echo  Please set up the venv first:
    echo    cd %SCRIPT_DIR%..\
    echo    python -m venv venv
    echo    venv\Scripts\pip install -r SpeciesDetector\requirements.txt
    echo.
    pause
    exit /b 1
)

if not exist "%SERVER_SCRIPT%" (
    echo  ERROR: detection_server.py not found at:
    echo    %SERVER_SCRIPT%
    pause
    exit /b 1
)

"%VENV_PYTHON%" "%SERVER_SCRIPT%"
if errorlevel 1 (
    echo.
    echo  Server exited with an error. Check the output above.
    pause
)
