@echo off
setlocal
cd /d "%~dp0.."
dotnet publish RmsCollector.csproj -c Release -r win-x64 --self-contained false -o publish\opcua
if errorlevel 1 (
  echo Build OPC UA gagal. Periksa pesan di atas.
  pause
  exit /b 1
)
echo Selesai. Buka folder publish\opcua dan baca PANDUAN_IGNITION_OPCUA.md.
pause
