# Troubleshooting RP2040-Zero and RP2350-USB-C

## RP2350 proxy is online but the mouse is disconnected

- Confirm this is the two-female-port **RP2350-USB-C**, not RP2350-USB-CM.
- Connect Windows to the native port beside `BOOT`/`RESET` and the mouse to the
  opposite PIO-USB port.
- With power removed, confirm both PIO-port CC selectors are in the
  silkscreen's `1 / Source` position. Never bridge both positions.
- Use data-capable cables and avoid an unpowered hub.
- Reflash `rainbow_recoil_rp2350_usb_c.uf2`, not the RP2040 image.

`MOUSE:UNSUPPORTED:HID_REPORT_DESCRIPTOR` means the device exposed no mouse
layout the firmware could safely decode and boot-protocol fallback was not
available. `MOUSE:HOST_ERROR` means the PIO-USB host could not continue its
report transfer. In either case the app intentionally disables arming. Test a
plain wired mouse to separate descriptor compatibility from cabling/power.

See [RP2350_MOUSE_PROXY.md](RP2350_MOUSE_PROXY.md) for the physical checklist.

The board has two distinct USB states. Diagnose the state before changing tools or reinstalling anything.

| State | Expected Windows result |
|---|---|
| ROM UF2 boot loader | Removable drive named `RPI-RP2`; no firmware COM/HID interfaces |
| Application firmware | USB Composite Device, USB Serial Device (COMx), and HID-compliant mouse; no `RPI-RP2` drive |

## RPI-RP2 does not appear

Use the connected-board button sequence exactly:

1. Hold `BOOT` continuously.
2. Press and release `RESET` while still holding `BOOT`.
3. Keep holding `BOOT` until `RPI-RP2` appears.
4. Release `BOOT`.

Or disconnect the cable, hold `BOOT`, reconnect USB Type-C, wait for `RPI-RP2`, and release `BOOT`.

If the drive still does not appear:

- Replace the cable with one proven to carry USB data. A lit LED proves power, not data.
- Connect directly to another PC USB port and temporarily remove an unpowered hub or dock from the path.
- Check Disk Management or run `Get-Volume -FileSystemLabel RPI-RP2`; File Explorer may not have refreshed yet.
- Check Device Manager for an unknown USB device. Disconnect the board, reboot Windows if needed, reconnect with the alternate BOOT-at-plug-in sequence, and retest.
- Inspect the USB Type-C receptacle for debris or mechanical damage.

The RP2040 ROM boot loader uses `VID_2E8A&PID_0003`. If that device identity never appears with a known-good data cable on multiple ports, the problem is below the application firmware layer.

## RPI-RP2 remains mounted after copying a file

A valid RP2040 UF2 normally causes the volume to eject and the board to restart immediately after the copy completes.

Check that:

- The copied file ends in `.uf2`; an `.elf`, `.bin`, `.ino`, or renamed source file is not flashable through this drive.
- The UF2 came from the `waveshare_rp2040_zero` environment or the Arduino-Pico `Waveshare RP2040 Zero` board selection.
- The copy completed without a Windows I/O error.
- You did not press or hold `BOOT` again during the reboot.

Re-enter UF2 mode and retry with one of these known project outputs:

```text
RP2040_Firmware\rainbow_recoil.uf2
RP2040_Firmware\.pio\build\waveshare_rp2040_zero\firmware.uf2
RP2040_Firmware\arduino-build\rainbow_recoil.ino.uf2
```

The Arduino CLI filename is the normal output name; if a different installed CLI release chooses another name, use the `.uf2` shown in its successful compile output.

## Flash helper says RPI-RP2 is not mounted

`flash-firmware.bat` intentionally searches for a volume whose label is exactly `RPI-RP2`; it does not guess a drive letter.

Run:

```powershell
Get-Volume -FileSystemLabel RPI-RP2 -ErrorAction SilentlyContinue |
    Select-Object DriveLetter, FileSystemLabel
```

If there is no result, enter UF2 mode and rerun the helper. If there is a result but it has no drive letter, assign one in Disk Management or copy from another Windows machine. Do not aim the script at an arbitrary removable drive.

## Firmware builds, but no COM port appears

First confirm `RPI-RP2` is gone. If it is still present, the firmware is not running.

With the firmware running, inspect all interfaces that use Raspberry Pi VID `2E8A`:

```powershell
Get-PnpDevice -PresentOnly |
    Where-Object InstanceId -Match 'VID_2E8A' |
    Select-Object Class, FriendlyName, Status, InstanceId

Get-CimInstance Win32_SerialPort |
    Where-Object PNPDeviceID -Match 'VID_2E8A' |
    Select-Object DeviceID, Name, PNPDeviceID
```

A correct build exposes a composite parent plus CDC and HID children. Windows 10/11 loads its built-in `usbser.sys`, composite-parent, and HID drivers. Do not install a USB-to-UART bridge driver or a CircuitPython serial-driver package; neither matches this native USB firmware.

If HID appears but CDC does not, or CDC appears but HID does not:

- Arduino IDE: confirm **Waveshare RP2040 Zero** and **Tools > USB Stack > Adafruit TinyUSB**, then rebuild and reflash.
- Arduino CLI: confirm the FQBN ends with `:usbstack=tinyusb`.
- PlatformIO: confirm `board = waveshare_rp2040_zero` and `-DUSE_TINYUSB` remain in `platformio.ini`.
- Delete only the firmware build output (`.pio\build` or the chosen Arduino output directory), rebuild, and flash the new UF2. Do not mix outputs from different board selections.

## Windows shows a COM port, but the app stays disconnected

- Close Arduino Serial Monitor, PlatformIO Monitor, PuTTY, or any other program using the port. Windows serial ports normally allow only one owner.
- Unplug the board, wait for the old COM entry to disappear, reconnect, and allow Windows a few seconds to finish creating all composite interfaces.
- Check the port with the `Win32_SerialPort` command above. The PNP device ID should contain `VID_2E8A`.
- Restart the app after the runtime COM port is present.

The app does not search for a bridge-chip description. It considers active COM ports associated with VID `2E8A`, prefers the last working port, and tries every current candidate until one replies `PONG:RAINBOW-RECOIL:4`. A message that no port passed the firmware handshake usually means the board is running an older or unrelated sketch; flash the matching v1.3-or-newer UF2 again.

## The COM number changed

This is normal. Windows can allocate another `COMx` value when you use a different USB socket, change the firmware's USB descriptors, or clear device history. The boot-loader drive has no runtime COM number.

Do not put a fixed COM number in firmware instructions. If more than one RP2040 CDC device is connected, disconnect the unrelated device during initial setup so the intended port is unambiguous.

## Device Manager shows only “HID-compliant mouse”

There may be several entries with that generic name. Open the candidate device's **Properties > Details > Hardware Ids** and look for VID `2E8A`. Then verify that the same composite instance also has a **USB Serial Device (COMx)** child.

If there is no CDC child, rebuild with Adafruit TinyUSB as described above. The desktop app needs the CDC interface even though the HID interface is present.

## Arduino IDE does not list Waveshare RP2040 Zero

Verify the exact Additional Boards Manager URL:

```text
https://github.com/earlephilhower/arduino-pico/releases/download/global/package_rp2040_index.json
```

Then reopen Boards Manager and install **Raspberry Pi Pico/RP2040/RP2350 by Earle F. Philhower III**. A similarly named package from another publisher does not provide the same board menus or FQBN.

If Boards Manager cannot download its index:

- Confirm the URL has no leading/trailing spaces.
- Check whether a corporate proxy or TLS inspection is blocking GitHub release downloads.
- Restart Arduino IDE after the package installation finishes.

## Arduino build cannot find Adafruit_TinyUSB.h

The supported Arduino-Pico core bundles the required TinyUSB integration. This error usually means the wrong core or USB stack was selected.

1. Confirm the installed core publisher is **Earle F. Philhower III**.
2. Reselect **Waveshare RP2040 Zero**.
3. Select **Adafruit TinyUSB** under **Tools > USB Stack**.
4. Close duplicate copies of the sketch and compile the canonical `RP2040_Firmware\rainbow_recoil\rainbow_recoil.ino`.

Do not solve this by adding an unrelated TinyUSB ZIP library on top of the core; duplicate library versions can select incompatible headers.

## PlatformIO reports `pio` is not recognized

If PlatformIO was installed in VS Code, use a PlatformIO terminal. If it was installed with Python, close and reopen PowerShell after installation:

```powershell
py -m pip install --upgrade platformio
pio --version
```

You can also invoke the module directly to confirm it is installed:

```powershell
py -m platformio run -e waveshare_rp2040_zero
```

`build.bat` also checks PlatformIO's standard virtual environment and local
Python installations when the `pio` executable is not on `PATH`.

## PlatformIO fails while cloning or resolving packages

The `platformio.ini` platform is fetched from GitHub, so Git and network access are required. Confirm:

```powershell
git --version
pio --version
```

If the error mentions an excessively long path, move the project to a short local directory such as `C:\src\RainbowRecoil`. If that is not enough, enable long-path support in Windows and Git, then reboot:

```powershell
# Run this command from an elevated terminal only if long-path errors persist.
git config --system core.longpaths true
```

Do not replace `platformio.ini` with the stock PlatformIO RP2040 platform; this project intentionally uses the Arduino-Pico-compatible platform integration.

## Windows app restore or build fails

From `RainbowRecoil\WindowsApp`, run:

```powershell
dotnet --info
dotnet restore .\RainbowRecoil.csproj
dotnet publish .\RainbowRecoil.csproj -c Release -r win-x64 --self-contained true -o .\artifacts\win-x64
```

- If `dotnet` is not found, install the .NET 8 SDK and open a new terminal.
- If NuGet restore fails, verify internet/proxy access to NuGet and retry.
- If the published executable fails before showing a window on a clean machine, install the [Microsoft Visual C++ 2015-2022 Redistributable (x64)](https://aka.ms/vs/17/release/vc_redist.x64.exe) and retry.
- If PowerShell blocks `build.ps1`, run `Set-ExecutionPolicy -Scope Process Bypass` in that terminal; do not change the machine-wide policy for this project.
- Launch only after publish succeeds: `WindowsApp\artifacts\win-x64\zerosense.exe`.

## Report useful diagnostics

First open **Device** in ZeroSense and choose **Copy diagnostic report**. The
report contains bounded recent events and performance/serial counters; it does
not contain screenshots. The **Use simulator** button can distinguish an app or
configuration problem from a physical USB/firmware problem.

Include these items with a hardware/setup issue:

- Windows version (`winver`)
- Firmware build route and the exact board/FQBN
- Whether `RPI-RP2` appears and whether it ejects after the copy
- `pio run -e waveshare_rp2040_zero` or Arduino compile error text
- Output of the two `VID_2E8A` PowerShell queries above
- The board's assigned COM number, if any

Do not include game account details or unrelated application data in a hardware report.
