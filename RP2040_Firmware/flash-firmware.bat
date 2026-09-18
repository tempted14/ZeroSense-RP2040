@echo off
setlocal
cd /d "%~dp0"

set "UF2=%~dp0rainbow_recoil.uf2"
if not exist "%UF2%" set "UF2=%~dp0.pio\build\waveshare_rp2040_zero\firmware.uf2"
if not exist "%UF2%" (
    echo ERROR: No firmware UF2 was found.
    echo Run build.bat, or export a compiled binary from Arduino IDE first.
    exit /b 1
)

set "RPI_DRIVE="
for /f "usebackq delims=" %%D in (`powershell -NoProfile -Command "$volumes = Get-Volume -FileSystemLabel 'RPI-RP2' -ErrorAction SilentlyContinue; foreach ($volume in $volumes) { if ($volume.DriveLetter) { [Console]::WriteLine('{0}:', $volume.DriveLetter); break } }"`) do set "RPI_DRIVE=%%D"

if not defined RPI_DRIVE (
    echo The RPI-RP2 boot drive is not mounted.
    echo.
    echo 1. Connect the board with a data-capable USB-C cable.
    echo 2. Hold BOOT.
    echo 3. While holding BOOT, press and release RESET.
    echo 4. Release BOOT when the RPI-RP2 drive appears.
    echo 5. Run this script again.
    echo.
    echo Alternative: with the cable unplugged, hold BOOT while plugging it in.
    exit /b 2
)

echo Copying "%UF2%" to %RPI_DRIVE%\ ...
copy /y "%UF2%" "%RPI_DRIVE%\rainbow_recoil.uf2" >nul
if errorlevel 1 (
    echo ERROR: Copy failed. Re-enter RPI-RP2 mode and try again.
    exit /b 1
)

echo Flash complete. RPI-RP2 should eject and the board should restart.
echo Windows will then enumerate one USB Serial Device COM port and one HID mouse.
exit /b 0
