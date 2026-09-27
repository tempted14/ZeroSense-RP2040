# ZeroSense v2.1.2 — CDC connection recovery validation prerelease

Install the **v2.1.2 Windows app and the matching board UF2** to get both sides
of this fix. RP2040-Zero users need `ZeroSense-RP2040-Zero-2.1.2.uf2`, not the
RP2350 image. Keep the physical mouse connected directly to the PC when using
RP2040. The one-download starter bundle contains the app and both UF2s; flash
only the one matching your board. Your saved profiles/settings are preserved.

## What changed

- The app repeats safe PING probes for up to three seconds instead of giving up
  after a single lost startup reply. Firmware identity is still checked exactly.
- A failed reader or handshake releases the UI promptly while cleanup runs in
  the background. Reconnect waits for the old native COM handle and reader to
  finish rather than racing a blocked close operation.
- Serial framing retains partial replies across read timeouts. Malformed UTF-8
  and oversized response lines are rejected without dropping a healthy port;
  corrupt acknowledgements cannot satisfy exact readback checks.
- Firmware resets partial protocol state, rolls back uncommitted configuration,
  and stops generated output when a CDC/DTR session closes or opens. A COM port
  staying visible in Device Manager no longer implies the old session is valid.
- Diagnostics now retain serial exception types and error codes. The connection
  error heading no longer calls every native COM failure a firmware handshake
  failure.

M1+M2 activation, profiles, multipliers, first-bullet kick, jitter, delta noise,
rapid-fire configuration, and USB/HID polling logic are unchanged. The RP2040
two-mouse rapid-fire limitation documented in v2.1.1 still applies.

## Validation and remaining limits

New executable tests inject lost handshake replies, wrong firmware identity,
fragmented/malformed/oversized replies, reader failures, and stalled native
closes through the actual connection class. Existing profile, protocol,
scheduler/replay, Windows, and both firmware build checks remain release gates.

This is a **validation prerelease**, not a promise that every physical USB
freeze is eliminated. No physical RP2040/RP2350 soak test was performed here.
COM remaining present and no unplug sound suggest a session/transport stall,
but do not prove the cable, power, USB controller, or driver is healthy.

After flashing, confirm diagnostics show `BUILD:V2.1.2-CDC-RECOVERY-20260927`.
Test app reconnect, several M1+M2 holds/releases, then a longer session. If it
fails again, export the app's diagnostics before closing it and note whether
the COM port remains visible. Close ZeroSense before running
`tools/read_usb_status.ps1 -Port COM4 -Samples 3`; only one process can own COM4.
If Windows itself keeps Open/Close stalled, one board unplug/replug may still
be necessary. The app will not force-reset unrelated USB devices.

The portable Windows ZIP is unsigned unless a separately signed installer is
present. Verify downloads using `SHA256SUMS.txt`; release CI also generates
an SPDX SBOM and artifact provenance attestations.
