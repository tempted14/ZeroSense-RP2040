# Quick start: RP2040-Zero or RP2350-USB-C on Windows

Use this checklist after extracting the project. For first-time tool installation, use [INSTALLATION.md](INSTALLATION.md).

## Hardware

- TENSTAR/Waveshare RP2040-Zero, or Waveshare RP2350-USB-C with two female ports
- Data-capable USB cable with USB Type-C at the board
- RP2350 only: a second data-capable cable for the mouse and PIO-port CC1/CC2
  configured as `1 / Source`
- Windows 10 or Windows 11 x64

The board uses native RP2040 USB. Windows installs its own CDC serial, composite, and HID class drivers automatically.

## Build the firmware with PlatformIO

Prerequisites: PlatformIO Core and Git. The helper finds `pio` on `PATH`, the
standard PlatformIO virtual environment, or a local Python installation with
the `platformio` module.

```powershell
cd .\RP2040_Firmware
.\build.bat
```

Successful outputs:

```text
RP2040_Firmware\.pio\build\waveshare_rp2040_zero\firmware.uf2
RP2040_Firmware\rainbow_recoil.uf2
RP2040_Firmware\.pio\build\waveshare_rp2350_usb_c\firmware.uf2
RP2040_Firmware\rainbow_recoil_rp2350_usb_c.uf2
```

If building RP2040 with Arduino IDE instead, install the Earle F. Philhower Arduino-Pico core from:

```text
https://github.com/earlephilhower/arduino-pico/releases/download/global/package_rp2040_index.json
```

Then open `RP2040_Firmware\rainbow_recoil\rainbow_recoil.ino` and select:

```text
Board:     Waveshare RP2040 Zero
USB Stack: Adafruit TinyUSB
```

Use PlatformIO for the RP2350 source build. The release already contains both
board-specific UF2 files.

## Flash

1. Connect USB Type-C.
2. Hold `BOOT`.
3. Press and release `RESET` while holding `BOOT`.
4. Release `BOOT` when `RPI-RP2` appears in File Explorer.
5. From `RP2040_Firmware`, run the command for the exact board:

   ```powershell
   .\flash-firmware.bat rp2040
   # or
   .\flash-firmware.bat rp2350
   ```

   You can also manually copy the matching generated UF2 to `RPI-RP2`.

6. Wait for `RPI-RP2` to eject and the board to restart.

For RP2350, connect the PC to the native port beside `BOOT`/`RESET`, then connect
the mouse to the opposite female PIO-USB port. Follow
[RP2350_MOUSE_PROXY.md](RP2350_MOUSE_PROXY.md) before applying power.

## Confirm the board is running

Device Manager should now show all of the following:

- USB Composite Device
- USB Serial Device (COMx)
- HID-compliant mouse

The RP2350 Device page must additionally show its board identity and the
downstream mouse VID:PID. The app keeps output disabled until that mouse is
connected and decoded.

`RPI-RP2` is expected only in boot-loader mode. If the drive remains mounted, the application firmware is not running.

## Build and run the Windows app

```powershell
cd ..\WindowsApp
.\build.ps1
.\artifacts\win-x64\zerosense.exe
```

Close Arduino Serial Monitor or any other serial terminal before starting the app; only one process can own the COM port at a time.

In the app, select the operator or individual weapon and choose **General**, **Original weapon pattern**, **Experimental (per-weapon calibrated)**, or **Supplied research profile (Y11S1.3 estimate)**. The dedicated **Use supplied research profile** switch provides quick A/B switching between the original and supplied profiles. Research mode imports the supplied model's vertical stage ratios for 60 automatic weapons, keeps the resolved reference total output and horizontal trace, and does not edit the original profile. It now resolves the exact operator/attachment/optic reference first, including a matching measured profile when available. Optic selection is automatic by default: attackers use 2.5x, ordinary defenders use 1.0x, and defender DMR exceptions use 2.5x for TCSG12, Tubarão's AR-15.50, and Aruni's Mk 14 EBR. Turn automatic selection off to unlock the magnification dropdown as a manual override. The checked-in calibration starts at 1600 DPI, 55/55, multiplier `0.001000`, and ADS 38/67/72/74 for 1.0x/2.5x/3.5x/8.0x. Resolution and aspect ratio are intentionally not calibration inputs because the RP2040 emits relative HID counts. Profile and sensitivity changes are sent to the RP2040 immediately while connected and are not reported as synchronized until the firmware acknowledges them.

Experimental mode leaves both established modes unchanged. Its five gains start at `1.00`: first shot, early magazine (under 25%), middle (25–70%), late (70–100%), and horizontal. In the shooting range, change one value by `0.05`, fire a full magazine at the same distance, and repeat. The values are remembered separately for each weapon; **Reset weapon** clears only the selected weapon. Weapons without an automatic pattern use the General fallback.

Research mode is also labeled Estimated. The supplied table ends at Y11S1.3 and is mostly model output rather than measurement; XK23 therefore uses the unchanged original estimate. See [the research-profile notes](RESEARCH_RECOIL_PROFILE.md) before comparing it in the shooting range.

Use **Per-weapon output strength** for the quickest adjustment: leave it at `1.00×` to preserve the profile, increase it when the group still climbs, or decrease it when correction pulls downward. It is saved independently for each weapon and also works for semi-automatic per-shot recoil. Use Experimental stage gains only after the overall strength is close.

Use **Horizontal recoil pattern** to tune lateral movement independently for
the selected weapon. **Profile pattern** keeps the resolved source trace;
**Mirror profile**, **Gun pulls left**, **Gun pulls right**, and **Alternating
sway** are deterministic overrides. The left/right choices describe the gun's
recoil, so ZeroSense emits the opposite correction. Turn the switch off for
vertical-only output. Strength, mode, and enabled state are saved per weapon;
the original source profile is never edited. Catalog directions are estimates
unless the selected loadout has an exact measured X/Y profile.

The recoil settings also include **General timing variance (±8%)**. It is off by default for repeatable compensation. Turning it on varies only the General-mode update cadence; Pattern and rapid-fire RPM timing stay exact. The app saves the choice and requires an exact firmware acknowledgement when synchronizing it.

**Delta noise (±2–3 counts)** is also off by default. When enabled, firmware adds small paired offsets only to generated recoil reports and repays the offset before adding a new one. This avoids cumulative cursor drift and leaves the RP2350's physical mouse deltas unchanged. HID counts are device input units and are not guaranteed to equal Windows screen pixels.

On the Detection page, optional weapon OCR scans the named primary and secondary
cards on Siege's **Loadout** screen. Use **Detect loadout now** before entering
the round; the icon-only in-round HUD is intentionally not OCRed. Global `1` and `2` presses
select the remembered primary and secondary profiles respectively while Rainbow
Six has focus. Both the OCR feature and slot shortcuts can be disabled.

Select **Arm output**. Arming alone does not send movement. The app starts a burst only while right mouse (aim) and left mouse (fire) are held together and stops when either is released. Pattern mode resets to shot one at every new press.

For a board-free check, open **Device** and select **Use simulator**. It runs the
complete validation, profile calculation, and protocol-encoding path without
creating mouse movement. Use **Validate now** to check the selected profile and
**Copy diagnostic report** to capture bounded connection, OCR, memory, and recent
error information. Continuous OCR waits for two identical consecutive matches;
manual detection still applies immediately.

The Calibration page's **Reset calibration** action resets the reference values
and active weapon strength together. **Undo reset** restores the previous values.
In the weapon controls, **2.5× auto vertical boost** adds 1–4× extra downward
output only for automatic weapons when the active optic is 2.5×. It is applied
after the profile's 127-point limit, so it can still strengthen an already
maxed master gain. The default is 1×; start with 1.5× or 2× and calibrate in
the shooting range. Other optics and semi-automatic weapons are unchanged.
This control needs both the updated app and UF2; the old executable will not
show it, and the old UF2's General-mode smoothing can suppress it.

The 115 weapon entries are a complete selection catalog; 61 automatic entries include current RPM/magazine timing and deterministic video-derived pattern estimates. Supported semi-automatic weapons default to rapid fire with per-shot recoil, while automatic weapons and manually cycled weapons are excluded. Every automatic profile applies Vertical Grip. Automatic profiles use Flash Hider instead of Compensator, except Ela's SCORPION EVO 3 A1, which uses Vertical Grip plus Compensator. The F2 entry uses vertical grip plus flash hider. Operator selection also applies side-specific Y11S3 restrictions: Aruni's Mk 14 EBR and Tubarão's AR-15.50 do not inherit the attacker version's muzzle brake. These curves are starting estimates, not raw Ubisoft data, so validate one weapon at a time in the shooting range.

## Fast diagnostics

If the RP2350 stays connected but the mouse freezes even with ZeroSense closed,
leave both USB connections in place. Close ZeroSense if it was open, then run
`.\tools\read_usb_status.ps1 -Port COM4` from the repository root, replacing
`COM4` with the port shown below. Move and click the mouse during its three
STATUS samples. The command does not arm or configure output. Save the output,
and note whether unplugging only the mouse restores movement or the board needs
a reset. `docs/AUDIT_2026-09-22.md` explains the counters. The extended fields
require the diagnostic firmware build and cannot diagnose freezes retroactively.
The current RP2350 host-fix and optic-boost build identifies itself as
`BUILD:HOST-FIX-OPTIC-20260922`.
If this build still freezes, capture STATUS before unplugging anything: the
status should report `MOUSE:HOST_ERROR`, and the host task age will distinguish
another stalled host loop from a different failure.

```powershell
Get-Volume -FileSystemLabel RPI-RP2 -ErrorAction SilentlyContinue

Get-CimInstance Win32_SerialPort |
    Where-Object PNPDeviceID -Match 'VID_2E8A' |
    Select-Object DeviceID, Name, PNPDeviceID
```

Boot-loader identity: `VID_2E8A&PID_0003`. For the running composite firmware, match VID `2E8A`, the present CDC/HID interfaces, and the assigned COM port instead of assuming one fixed PID.
