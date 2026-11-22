@echo off
setlocal
cd /d "%~dp0.."
dotnet run --project RmsCollector.csproj -- --config config\collector.mock8.conf --mock
pause
