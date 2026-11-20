@echo off
setlocal
cd /d "%~dp0"
if not exist "rms_collector_opcua.exe" (
  echo Jalankan file ini dari folder hasil publish OPC UA.
  echo Untuk build source, gunakan scripts\publish-opcua.cmd dari root project.
  pause
  exit /b 1
)
rms_collector_opcua.exe --config config\collector.opcua.conf --real
if errorlevel 1 pause
