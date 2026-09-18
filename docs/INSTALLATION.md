# Windows 10/11 installation

This guide starts with an unconfigured Windows 10 or Windows 11 x64 machine and a TENSTAR RP2040-Zero. Paths are shown relative to the `RainbowRecoil` directory that contains `WindowsApp`, `RP2040_Firmware`, and `Tests`.

## 1. Check the hardware

You need:

- A TENSTAR RP2040-Zero with `BOOT` and `RESET` buttons
- A data-capable cable that connects to the board's USB Type-C receptacle
- A free USB port on the PC

A cable that supplies power but does not carry data can light the board without ever creating an `RPI-RP2` drive or a COM port. If the cable's data capability is unknown, test with one already proven to transfer files from another USB device.

The board uses native RP2040 USB. Windows 10/11 already includes the required composite-device, USB CDC serial, and HID class drivers. Do not install a third-party serial-driver package for this board.

## 2. Put the source in a short local path

Extract or clone the project to a local folder such as:

```text
C:\src\RainbowRecoil
```

A short path avoids Windows path-length problems when PlatformIO and NuGet create deeply nested dependency folders. Do not build from inside a ZIP archive, cloud placeholder, or read-only folder.

## 3. Install the Windows build prerequisites

The SDK is required to build the Windows app. In PowerShell:

```powershell
winget install Microsoft.DotNet.SDK.8
```

Alternatively, use Microsoft's [.NET 8 download page](https://dotnet.microsoft.com/download/dotnet/8.0). Close and reopen PowerShell after installation, then confirm:

```powershell
dotnet --info
```

Also install the [Microsoft Visual C++ 2015-2022 Redistributable (x64)](https://aka.ms/vs/17/release/vc_redist.x64.exe). The app is published with .NET and the Windows App SDK files beside it, but an unpackaged Windows App SDK app still relies on the supported Microsoft C++ runtime.

## 4. Choose and install one firmware toolchain

Arduino IDE is the most direct clean-machine route. PlatformIO is useful for a repeatable command-line build. Both build the same sketch.

### Option A: Arduino IDE 2

1. Install [Arduino IDE 2](https://www.arduino.cc/en/software) using Arduino's Windows EXE or ZIP download. Do not use the Microsoft Store build; Arduino-Pico documents board-detection problems with it.
2. Open **File > Preferences**.
3. Add the following exact URL to **Additional Boards Manager URLs**. If other URLs are already present, use the list button and add this as another line.

   ```text
   https://github.com/earlephilhower/arduino-pico/releases/download/global/package_rp2040_index.json
   ```

4. Open **Tools > Board > Boards Manager**.
5. Search for `pico` and install **Raspberry Pi Pico/RP2040/RP2350 by Earle F. Philhower III**.
6. Open `RP2040_Firmware\rainbow_recoil\rainbow_recoil.ino`.
7. Select **Tools > Board > Raspberry Pi Pico/RP2040/RP2350 > Waveshare RP2040 Zero**.
8. Select **Tools > USB Stack > Adafruit TinyUSB**.
9. Choose **Sketch > Verify/Compile**.
10. Choose **Sketch > Export Compiled Binary**. Arduino IDE writes a `.uf2` with the exported binaries next to the sketch.

Do not choose a generic Pico board and do not install a similarly named board package from another publisher. The selected Waveshare definition supplies the correct 2 MB flash layout, and the Arduino-Pico core supplies the TinyUSB library used by the project.

### Option B: PlatformIO

1. Install [Git for Windows](https://git-scm.com/download/win).
2. Install the [PlatformIO IDE extension](https://platformio.org/install/ide?install=vscode) in VS Code, or install PlatformIO Core with Python:

   ```powershell
   py -m pip install --upgrade platformio
   ```

3. Open a new terminal and confirm `pio --version` works.
4. Build from the firmware directory:

   ```powershell
   cd .\RP2040_Firmware
   .\build.bat
   ```

The PlatformIO build output is:

```text
RP2040_Firmware\.pio\build\waveshare_rp2040_zero\firmware.uf2
```

`build.bat` also creates the convenient copy:

```text
RP2040_Firmware\rainbow_recoil.uf2
```

The first build downloads the Arduino-Pico-compatible platform, compiler, framework, and libraries, so it requires internet access and may take several minutes.

## 5. Enter UF2 boot mode

Use the board's two buttons in this order while it is connected:

1. Press and hold `BOOT`.
2. While continuing to hold `BOOT`, press and release `RESET`.
3. Keep holding `BOOT` until File Explorer shows a removable drive named `RPI-RP2`.
4. Release `BOOT`.

If the board is disconnected, the equivalent sequence is to hold `BOOT`, connect the USB Type-C cable, wait for `RPI-RP2`, and release `BOOT`.

In this state, the RP2040 ROM is waiting for a UF2 file. The application firmware is not running, so its COM port and HID interface are not expected to exist.

## 6. Flash the firmware

Either drag the built `.uf2` to the root of `RPI-RP2`, or run the checked-in helper from `RP2040_Firmware`:

```powershell
.\flash-firmware.bat
```

The helper looks up the volume by its `RPI-RP2` label; it does not assume a drive letter. When the copy completes successfully, the drive disappears and the board restarts on its own.

Do not rename or copy an `.elf`, `.bin`, or source file to the drive. Only use the `.uf2` generated for `waveshare_rp2040_zero`.

## 7. Confirm Windows runtime enumeration

Wait several seconds after the board restarts, then open Device Manager. A correct CDC + HID build presents:

- **USB Composite Device** under the USB device/controller view
- **USB Serial Device (COMx)** under **Ports (COM & LPT)**
- **HID-compliant mouse** under the HID or pointing-device view

The new HID entry does not replace the user's physical mouse; both can be present.

For a command-line check, open PowerShell and run:

```powershell
Get-CimInstance Win32_SerialPort |
    Where-Object PNPDeviceID -Match 'VID_2E8A' |
    Select-Object DeviceID, Name, PNPDeviceID

Get-PnpDevice -PresentOnly |
    Where-Object InstanceId -Match 'VID_2E8A' |
    Select-Object Class, FriendlyName, InstanceId
```

`2E8A` is Raspberry Pi's USB vendor ID. The ROM boot loader uses PID `0003`, but the running firmware's PID may change with the USB interface configuration or core version. Do not treat one PID or one COM number as permanent.

## 8. Build the Windows app

From the project root:

```powershell
cd .\WindowsApp
.\build.ps1
```

The script restores NuGet dependencies and publishes a self-contained Windows x64 build to:

```text
WindowsApp\artifacts\win-x64\zerosense.exe
```

An internet connection is required for the first restore. The publish output is a directory; keep its files together even though `zerosense.exe` is the launch point.

If local PowerShell policy blocks the script, permit it only for the current terminal and rerun:

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\build.ps1
```

## 9. Start and verify the app

1. Confirm that `RPI-RP2` is no longer mounted and **USB Serial Device (COMx)** is present.
2. Close Arduino Serial Monitor, PlatformIO Monitor, and any other terminal that may have the COM port open.
3. Run `WindowsApp\artifacts\win-x64\zerosense.exe`.
4. Wait for the app's board status to show a connection on the enumerated COM port.

The first run initializes the supplied calibration: 1600 DPI, horizontal/vertical 55, `MouseSensitivityMultiplierUnit=0.001000`, and ADS values 38/67/72/74 for 1.0x/2.5x/3.5x/8.0x. Resolution and aspect ratio are not calibration inputs because USB mouse reports use relative counts. Select the active weapon and optic magnification in the app; both the numeric weapon profile and per-axis scale are then sent over CDC.

The app lists all 115 weapons in the current catalog. Select **General** for a steady adjustment, **Weapon pattern (video-derived estimate)** for a deterministic RPM-timed trace, or opt into **Experimental (per-weapon calibrated)** to tune a separate copy with first/early/mid/late/horizontal gains. Pattern modes are available for 61 automatic weapons and stop after the current magazine size; Experimental falls back to General elsewhere. Supported semi-automatic profiles enable rapid fire by default and apply recoil per generated shot. The patterns are deliberately labeled estimates: public videos include mouse input and Ubisoft does not publish raw per-shot recoil coordinates.

The Overview page's per-weapon output strength is the safe first calibration control. `1.00×` is neutral, and the selected weapon remembers its own value. It scales every output mode, including semi-automatic per-shot recoil. Custom automatic profile RPM and magazine-size values are honored by both the firmware schedule and generated curve length.

Select **Arm output**, then hold right mouse (aim) and left mouse (fire) together to begin a burst. Releasing either button stops the RP2040 immediately; the next aim-plus-fire press starts the selected pattern again at shot one. The host also sends a 250 ms keepalive to a 750 ms firmware watchdog. Arming by itself must not move the pointer. Test this behavior on the desktop before opening the game, then calibrate one weapon at a time in the shooting range.

Every automatic profile applies the Vertical Grip modifier. Automatic profiles also use Flash Hider wherever they previously selected Compensator. Ela's SCORPION EVO 3 A1 keeps Compensator as its barrel exception while using the same Vertical Grip modifier. Older saved profiles are migrated to these preferences when loaded. The F2 entry uses vertical grip and flash hider. Shared weapons can resolve different attachments by operator: Y11S3 removes the muzzle brake from Aruni's Mk 14 EBR and Tubarão's AR-15.50 while retaining it for Dokkaebi and Maverick. Semi-automatic profiles show rapid-fire rate and per-shot correction in the UI. Attachment recommendations can change with Siege balance patches, so check the research date in the project README before relying on them.

The app discovers currently present ports associated with Raspberry Pi VID `2E8A`, then requires a `PONG:RAINBOW-RECOIL:3` reply before accepting one. This prevents another connected Pico-class CDC device from being mistaken for this firmware. It does not rely on a bridge-chip name, fixed runtime PID, or hard-coded COM number. If it does not connect, follow [TROUBLESHOOTING.md](TROUBLESHOOTING.md) and include the PowerShell enumeration output when reporting a problem.
