@echo off
setlocal
cd /d "%~dp0"
rms_collector_opcua.exe --config config\collector.opcua.conf --mock
set "RMS_RESULT=%ERRORLEVEL%"
pause
exit /b %RMS_RESULT%
