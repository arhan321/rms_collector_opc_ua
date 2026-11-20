@echo off
setlocal
cd /d "%~dp0.."
dotnet run --project tests-opcua\RmsCollector.OpcUa.Tests.csproj -c Release
exit /b %ERRORLEVEL%
