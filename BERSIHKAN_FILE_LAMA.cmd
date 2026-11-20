@echo off
setlocal
cd /d "%~dp0"
if not exist "RmsCollector.csproj" (
  echo Letakkan file ini di root project Collector yang akan diperbarui.
  pause
  exit /b 1
)
set "RMS_LEGACY_BACKUP=%~dp0..\RMS-Collector-Legacy-Backup-%RANDOM%-%RANDOM%"
if exist "%RMS_LEGACY_BACKUP%" (
  echo Folder backup sudah ada. Jalankan ulang untuk memilih nama baru.
  pause
  exit /b 1
)
mkdir "%RMS_LEGACY_BACKUP%"
if errorlevel 1 goto failed
call :MoveLegacy "src-csharp\Transport.cs"
if errorlevel 1 goto failed
call :MoveLegacy "tests\Program.cs"
if errorlevel 1 goto failed
call :MoveLegacy "tests\RmsCollector.Tests.csproj"
if errorlevel 1 goto failed
call :MoveLegacy "collector.token.example"
if errorlevel 1 goto failed
call :MoveLegacy "collector_windows_csharp.patch"
if errorlevel 1 goto failed
call :MoveLegacy "config\collector.conf"
if errorlevel 1 goto failed
call :MoveLegacy "config\collector.dojo.conf"
if errorlevel 1 goto failed
call :MoveLegacy "config\sample_config_8_robot"
if errorlevel 1 goto failed
call :MoveLegacy "opcua\OpcUaProgram.cs"
if errorlevel 1 goto failed
call :MoveLegacy "opcua\RmsCollector.OpcUa.csproj"
if errorlevel 1 goto failed
call :MoveLegacy "opcua\packages.lock.json"
if errorlevel 1 goto failed
call :MoveLegacy "bin"
if errorlevel 1 goto failed
call :MoveLegacy "obj"
if errorlevel 1 goto failed
call :MoveLegacy "publish"
if errorlevel 1 goto failed
call :MoveLegacy "opcua\bin"
if errorlevel 1 goto failed
call :MoveLegacy "opcua\obj"
if errorlevel 1 goto failed
call :MoveLegacy "tests\bin"
if errorlevel 1 goto failed
call :MoveLegacy "tests\obj"
if errorlevel 1 goto failed
call :MoveLegacy "tests-opcua\bin"
if errorlevel 1 goto failed
call :MoveLegacy "tests-opcua\obj"
if errorlevel 1 goto failed
echo Selesai. File lama dipindah ke: "%RMS_LEGACY_BACKUP%"
echo Konfigurasi OPC UA, konfigurasi robot dan certificate pki-opcua dipertahankan.
echo Berikutnya jalankan scripts\publish-opcua.cmd.
pause
exit /b 0

:MoveLegacy
if not exist "%~1" exit /b 0
for %%F in ("%RMS_LEGACY_BACKUP%\%~1") do if not exist "%%~dpF" mkdir "%%~dpF"
if errorlevel 1 exit /b 1
move "%~1" "%RMS_LEGACY_BACKUP%\%~1" >nul
exit /b %ERRORLEVEL%

:failed
echo Pembersihan belum selesai. Hentikan Collector yang berjalan dan periksa izin folder.
echo File yang sudah dipindah tetap aman di: "%RMS_LEGACY_BACKUP%"
pause
exit /b 1
