# ZeroSense v2.3.0 — per-gun Original output and Scorpion refinement

## New controls

- **Original Pattern Output · This Gun** now saves a separate 1–4× multiplier
  for each weapon. Select SMG-12, set its value, and switching to F2 or another
  gun restores that gun's own value. The setting is shared across operators
  using that same gun; it is not a new global or operator-specific control.
- Existing installations inherit their previous global value, so upgrading
  does not unexpectedly change every weapon. **Use default** removes only the
  selected gun's override and restores that inherited value.
- Per-gun values persist across closing/reopening the app, reach the firmware
  through sensitivity readback/configuration hashes, and participate correctly
  in calibration reset/undo. Incomplete numeric edits do not overwrite settings.
- The multiplier remains specific to stock estimated automatic patterns in
  **Original weapon pattern** mode. General, Research, Experimental, custom and
  measured profiles do not acquire this extra multiplier. Master gain,
  per-weapon strength, first-bullet kick and 2.5× boost retain their roles.

## Scorpion EVO 3 A1

The stock Original-mode estimate now follows the supplied range screenshot's
short vertical onset followed by sustained leftward recoil. Compensation
therefore transitions smoothly into **rightward** HID X while continuing
downward HID Y through the magazine. It no longer invents the previous repeating
left/right sequence or an ever-increasing late-magazine stage.

This is a **screenshot-guided estimate**, not a measured per-shot recording.
The image cannot determine HID counts, attachment setup or exact shot timing.
Timing/magazine metadata are unchanged, and output strength remains adjustable.
Research, Experimental, custom and measured Scorpion traces, the original
reference data, and all other weapons' pattern shapes are preserved. Optional
horizontal overrides still apply after profile selection.

## Recovery work retained from the local v2.2 candidate

- Cancelable Windows serial I/O, safe handle/buffer lifetime, USB task/mutex
  coordination, bounded diagnostics, disarmed reconnect retries and watchdog
  recovery are retained. Reconnect never silently re-arms output.
- Separate UF2s for RP2040-Zero, Waveshare RP2350-USB-C and RP2350-USB-A are
  included, along with the Windows app and diagnostic script in a starter bundle.
- RP2350 descriptor capacity and host diagnostics are retained as compatibility
  improvements, **not a confirmed wireless-receiver fix**.

Existing recoil strengths/profiles, rapid fire and its click variance, optional
General timing variance, delta noise, horizontal controls and first-bullet kick
are retained. Nothing changes mouse passthrough timing for the two requested
features; patterns and per-gun settings are calculated by the Windows app.

## Validation and limitations

Regression tests cover migration/persistence, malformed overrides, gun switching,
wire values/configuration hashes, full-magazine Scorpion output and isolation of
other profile sources. Local verification passed 60 core tests, 50 Python tests,
native HID replay, self-contained Windows publishing and all three UF2
build/family checks. Packaged-app UI checks verified separate SMG-12/C75 values,
selected-gun reset, the Scorpion estimate label and saved state after restart.
The tester's original settings were backed up/restored; output was never armed.
GitHub Windows and firmware CI are required before the release tag is published.

This is a **hardware-validation prerelease**. Automated tests do not prove
in-game calibration, zero disconnects or every mouse/receiver combination.
The RP2350-USB-A hot-plug R13 requirement remains a physical board limitation;
see the bundled board guide. Use at most 1000-Hz downstream mice; native 4/8-kHz
passthrough is not supported. Keep a previously working UF2 and backup mouse.

Portable builds are unsigned unless Authenticode signing is explicitly present;
verify `SHA256SUMS.txt` and available GitHub provenance. No certificate is created
by dismissing a Windows reputation warning. Use only in offline/private or
otherwise explicitly permitted environments.
