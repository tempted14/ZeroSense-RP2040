# Windows 10/11 installation

This guide starts with an unconfigured Windows 10 or Windows 11 x64 machine and
either a TENSTAR/Waveshare RP2040-Zero or the two-female-port Waveshare
RP2350-USB-C (SKU 34641). Paths are relative to the `RainbowRecoil` directory.

## Recommended release install (no developer tools)

1. Open the [releases page](https://github.com/tempted14/ZeroSense-RP2040/releases) and select the intended version. Validation prereleases may not appear under GitHub's `/latest` link.
2. Download `SHA256SUMS.txt` and the recommended
   `ZeroSense-<version>-Starter-Bundle.zip`. It contains the portable app,
   `START_HERE.txt`, and both board images. The same app and firmware files are
   also published separately for users who prefer individual downloads:
   - RP2040-Zero: `ZeroSense-RP2040-Zero-<version>.uf2`
   - Waveshare RP2350-USB-C: `ZeroSense-RP2350-USB-C-<version>.uf2`
3. Run `Get-FileHash -Algorithm SHA256 <downloaded-file>` in PowerShell for each
   downloaded release asset and compare the result with `SHA256SUMS.txt`. Do
   not continue if a hash differs.
4. Extract the entire starter bundle or Windows ZIP to a normal local folder.
   Do not launch the executable from inside the ZIP and do not separate
   `zerosense.exe` from the files beside it.
5. Put the board into UF2 boot mode: hold `BOOT`, tap `RESET`, release `BOOT`
   when `RPI-RP2` appears, and copy the matching UF2 to that drive. The board
   restarts automatically.
6. Run `zerosense.exe`, select **Scan for device**, and confirm the Device page
   reports the expected RP2040 or RP2350 identity before arming output.

The portable release includes .NET and the Windows App SDK. It does not require
the .NET SDK, Git, Arduino IDE, or PlatformIO. If the app fails before showing a
window, install the Microsoft Visual C++ 2015–2022 x64 Redistributable linked in
the troubleshooting guide. The rest of this document is the source-build and
hardware-diagnostics path.

## 1. Check the hardware

You need:

- A TENSTAR/Waveshare RP2040-Zero, or Waveshare RP2350-USB-C with `BOOT` and `RESET`
- A data-capable cable that connects to the board's USB Type-C receptacle
- RP2350 only: a data-capable mouse cable and CC1/CC2 on the PIO-USB port set to
  the silkscreen's `1 / Source` position
- A free USB port on the PC

A cable that supplies power but does not carry data can light the board without ever creating an `RPI-RP2` drive or a COM port. If the cable's data capability is unknown, test with one already proven to transfer files from another USB device.

Both boards use native USB toward Windows. Windows 10/11 already includes the
required composite-device, USB CDC serial, and HID class drivers. Do not install
a third-party serial driver. RP2350 users must read
[RP2350_MOUSE_PROXY.md](RP2350_MOUSE_PROXY.md) before connecting the mouse.

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

Arduino IDE is the most direct RP2040 route. PlatformIO builds both targets and
is required for an RP2350 source build because it applies the checked-in 2 MB
board definition, 120 MHz clock, compile guard, and pinned PIO-USB library.

### Option A: Arduino IDE 2 (RP2040 only)

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

Do not use this Arduino path for the RP2350 proxy. Do not choose a generic Pico
board or a similarly named board package from another publisher.

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
RP2040_Firmware\.pio\build\waveshare_rp2350_usb_c\firmware.uf2
```

`build.bat` also creates the convenient copy:

```text
RP2040_Firmware\rainbow_recoil.uf2
RP2040_Firmware\rainbow_recoil_rp2350_usb_c.uf2
```

The first build downloads the Arduino-Pico-compatible platform, compiler, framework, and libraries, so it requires internet access and may take several minutes.

## 5. Enter UF2 boot mode

Use the board's two buttons in this order while it is connected:

1. Press and hold `BOOT`.
2. While continuing to hold `BOOT`, press and release `RESET`.
3. Keep holding `BOOT` until File Explorer shows a removable drive named `RPI-RP2`.
4. Release `BOOT`.

If the board is disconnected, the equivalent sequence is to hold `BOOT`, connect the USB Type-C cable, wait for `RPI-RP2`, and release `BOOT`.

In this state, the RP2 ROM is waiting for a UF2 file. The application firmware
is not running, so its COM port and HID interface are not expected to exist.

## 6. Flash the firmware

Drag the board-specific `.uf2` to the root of `RPI-RP2`, or run the checked-in
helper from `RP2040_Firmware` with an explicit target:

```powershell
.\flash-firmware.bat rp2040
# or
.\flash-firmware.bat rp2350
```

Omitting the argument keeps the backward-compatible RP2040 default. When the
copy completes successfully, the drive disappears and the board restarts.

Do not rename or copy an `.elf`, `.bin`, or source file to the drive. Never mix
the two UF2 targets.

## 7. Confirm Windows runtime enumeration

Wait several seconds after the board restarts, then open Device Manager. A correct CDC + HID build presents:

- **USB Composite Device** under the USB device/controller view
- **USB Serial Device (COMx)** under **Ports (COM & LPT)**
- **HID-compliant mouse** under the HID or pointing-device view

With RP2040, the new HID entry remains separate from the user's directly
connected physical mouse. With RP2350, it is the upstream proxy that forwards
the mouse attached to the board; Windows does not enumerate that downstream
mouse separately.

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
2. RP2350 only: connect the PC to the native port beside `BOOT`/`RESET`, then
   connect the mouse to the opposite female PIO-USB port.
3. Close Arduino Serial Monitor, PlatformIO Monitor, and any other terminal that may have the COM port open.
4. Run `WindowsApp\artifacts\win-x64\zerosense.exe`.
5. Wait for the app's board status to show a connection on the enumerated COM port.
6. RP2350 only: confirm the Device page reports the RP2350 identity and the
   downstream mouse's VID:PID. Arming remains unavailable until the mouse is
   successfully decoded.

The first run initializes the supplied calibration: 1600 DPI, horizontal/vertical 55, `MouseSensitivityMultiplierUnit=0.001000`, and ADS values 38/67/72/74 for 1.0x/2.5x/3.5x/8.0x. Resolution and aspect ratio are not calibration inputs because USB mouse reports use relative counts. Select the active weapon and optic magnification in the app; both the numeric weapon profile and per-axis scale are then sent over CDC.

The app lists all 115 weapons in the current catalog. Select **General** for a steady adjustment, **Weapon pattern (video-derived estimate)** for the original deterministic RPM-timed trace, **Experimental (per-weapon calibrated)** to tune a separate copy with first/early/mid/late/horizontal gains, or **Research stages (Y11S1.3 estimate)** to compare the supplied model's normalized vertical stage ratios without changing the original profile. Pattern modes are available for 61 automatic weapons and stop after the current magazine size; Experimental falls back to General elsewhere, while Research mode falls back to the original estimate for XK23. Supported semi-automatic profiles enable rapid fire by default and apply recoil per generated shot. The patterns are deliberately labeled estimates: public videos include mouse input and Ubisoft does not publish raw per-shot recoil coordinates.

The Overview page exposes both **Master Recoil Gain** and a remembered
**Per-weapon Output Strength**. The master calibration starts at `12.00×`, while
`1.00×` is neutral for an individual weapon. Both accept any finite value from
`0` through the device's real `127`-unit HID/Q8.8 limit; their product is capped
at that same representable limit and the UI shows the effective total. They
scale every output mode, including semi-automatic per-shot recoil. Custom
automatic profile RPM and magazine-size values are honored by both the firmware
schedule and generated curve length.

Select **Arm output**, then hold right mouse (aim) and left mouse (fire) together
to begin a burst. Releasing either button stops generated output immediately;
the next aim-plus-fire press starts the selected pattern again at shot one. On
RP2350, ordinary physical motion and non-substituted buttons continue through
the proxy even while generated output is stopped or active. The host also sends
a 250 ms keepalive to a 750 ms firmware watchdog. Arming by itself must not move
the pointer. Complete the desktop checklist in
[RP2350_MOUSE_PROXY.md](RP2350_MOUSE_PROXY.md) before using the RP2350 build.

Every automatic profile applies the Vertical Grip modifier. Automatic profiles also use Flash Hider wherever they previously selected Compensator. Ela's SCORPION EVO 3 A1 keeps Compensator as its barrel exception while using the same Vertical Grip modifier. Older saved profiles are migrated to these preferences when loaded. The F2 entry uses vertical grip and flash hider. Shared weapons can resolve different attachments by operator: Y11S3 removes the muzzle brake from Aruni's Mk 14 EBR and Tubarão's AR-15.50 while retaining it for Dokkaebi and Maverick. Semi-automatic profiles show rapid-fire rate and per-shot correction in the UI. Attachment recommendations can change with Siege balance patches, so check the research date in the project README before relying on them.

The app discovers currently present ports associated with Raspberry Pi VID
`2E8A`, then requires a `PONG:RAINBOW-RECOIL:4` reply before accepting one. It
also consumes the subsequent board identity and RP2350 mouse-health messages.
It does not rely on a bridge-chip name, fixed runtime PID, or hard-coded COM
number. If it does not connect, follow
[TROUBLESHOOTING.md](TROUBLESHOOTING.md).
