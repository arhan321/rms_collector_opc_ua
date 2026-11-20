@echo off
setlocal
cd /d "%~dp0.."
call "%~dp0publish-opcua.cmd"
exit /b %ERRORLEVEL%
