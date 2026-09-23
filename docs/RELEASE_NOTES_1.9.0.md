# ZeroSense v1.9.0 — hardware-validation prerelease

This build compiles and passes automated tests, but **RP2350 passthrough and
1000 Hz stability have not been re-tested on the affected physical board**.
Use it as a prerelease and keep your known-good rollback UF2. Software-only
tests cannot prove that the intermittent mouse freeze is resolved.

## First-bullet vertical kick

- A per-weapon **First bullet vertical kick** control is available in
  automatic pattern modes. `1.00×` is neutral; the range is `1.00–4.00×`.
  Select Buck and C8-SFW, for example, to tune only that gun.
- The device multiplies only the first vertical pattern point, after the
  Q8.8 profile limit. Later shots, horizontal input, the original source
  pattern, and physical mouse passthrough are unchanged. The saved value is
  inactive in General mode. Experimental first-shot tuning is separate and
  can compound with this control.
- The app saves this setting by weapon name, shows it on selection, includes
  it in diagnostics, requires exact device readback, and binds it into the
  transactional configuration hash. Firmware configuration schema is now 4,
  so **update the app and matching UF2 together**. The firmware reports
  `BUILD:V1.9-FIRST-KICK-20260923` in STATUS.

## RP2350 host recovery candidate

- Restores the confirmed v1.8 rollback's bus sequencing and disconnect
  detection while bounding RX/EOP waits. Invalid or failed transfers are
  discarded and retried instead of reusing stale handshake/report bytes.
- Separates empty HID callbacks from decoder failures in diagnostics. The app
  labels a stalled host task, summarizes current-sample rates, and does not
  mistake idle traffic for a disconnect.

## Retained and compatibility

Stock/research/measured profile sources, master and per-weapon gain, original
pattern multiplier, horizontal tuning, 2.5× optic boost, optional timing
variance, and optional delta noise are retained. Older saved settings remain
readable; absent first-bullet settings default to neutral. RP2040 remains an
output-only board; RP2350 local M1+M2 activation remains device-local.

Download the **Starter Bundle** for the app, setup guide, and one UF2 per
board. Flash only the image matching your board. Verify the bundle against
`SHA256SUMS.txt` before extracting. The portable app may be unsigned;
checksums and GitHub provenance are not an Authenticode publisher signature.

For the investigation and testing limits, see
[the USB audit](HOST_STALL_TEST_2026-09-22.md).
