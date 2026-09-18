@echo off
setlocal
set "APP_EXE=%~dp0artifacts\win-x64\zerosense.exe"

if not exist "%APP_EXE%" (
    echo ERROR: The published app was not found.
    echo Run build.ps1 or build_windows.bat first.
    exit /b 1
)

start "" "%APP_EXE%"
exit /b 0
