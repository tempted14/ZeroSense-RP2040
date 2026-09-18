@echo off
setlocal
cd /d "%~dp0"

set "PIO_EXE="
for %%I in (pio.exe) do set "PIO_EXE=%%~$PATH:I"
if not defined PIO_EXE if exist "%USERPROFILE%\.platformio\penv\Scripts\pio.exe" set "PIO_EXE=%USERPROFILE%\.platformio\penv\Scripts\pio.exe"

set "PIO_PYTHON="
if not defined PIO_EXE (
    for %%I in (py.exe python.exe) do if not defined PIO_PYTHON (
        "%%~$PATH:I" -m platformio --version >nul 2>nul && set "PIO_PYTHON=%%~$PATH:I"
    )
)
if not defined PIO_EXE if not defined PIO_PYTHON (
    for /d %%D in ("%LOCALAPPDATA%\Programs\Python\Python*") do if not defined PIO_PYTHON (
        "%%~fD\python.exe" -m platformio --version >nul 2>nul && set "PIO_PYTHON=%%~fD\python.exe"
    )
)

if not defined PIO_EXE if not defined PIO_PYTHON (
    echo ERROR: PlatformIO Core was not found.
    echo Install VS Code plus the PlatformIO extension, or run:
    echo   py -m pip install --upgrade platformio
    echo See README.md for the Arduino IDE build path.
    exit /b 1
)

echo Building for Waveshare RP2040 Zero with Arduino-Pico and Adafruit TinyUSB...
if defined PIO_EXE (
    "%PIO_EXE%" run -e waveshare_rp2040_zero
) else (
    "%PIO_PYTHON%" -m platformio run -e waveshare_rp2040_zero
)
if errorlevel 1 exit /b %errorlevel%

set "PIO_UF2=.pio\build\waveshare_rp2040_zero\firmware.uf2"
if not exist "%PIO_UF2%" (
    echo ERROR: PlatformIO completed but did not create "%PIO_UF2%".
    exit /b 1
)

copy /y "%PIO_UF2%" "rainbow_recoil.uf2" >nul
if errorlevel 1 exit /b %errorlevel%

echo.
echo Build complete: %~dp0rainbow_recoil.uf2
echo To flash while USB-C is connected: hold BOOT, tap RESET, release BOOT when
echo RPI-RP2 appears, then run flash-firmware.bat or copy this UF2 to that drive.
exit /b 0
