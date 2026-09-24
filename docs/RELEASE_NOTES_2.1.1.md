# ZeroSense v2.1.1 — RP2040 semi-auto validation prerelease

Use the **v2.1.1 Windows app** with the RP2040-Zero. The firmware image has
not changed functionally from v2.1.0; both board UF2s are rebuilt and included
for convenience. Flash only the UF2 matching your board.

The RP2040 is a second USB mouse, not a pass-through device. Previously the app
read the combined Windows M1/M2 state. A synthetic M1 release from the RP2040
could make the app send STOP while the physical mouse was still held. That
stopped rapid fire after its first pulse and cleared queued per-shot recoil.
The app now tracks M1 and M2 from the same physical mouse via Windows Raw Input.
The trigger remains M1+M2, and physical release or mouse removal still stops
output. Automatic-weapon activation is unchanged.

**Important limitation:** This fixes the app feedback loop, not the fundamental
two-mouse button aggregation. Your physical mouse's M1 remains held while the
RP2040 sends extra M1 pulses. Whether Rainbow Six registers those as separate
shots is not established by software tests and may vary with the game's input
path. Do not treat RP2040 rapid fire as hardware-verified. The RP2350 proxy is
the only board that can control the upstream M1 state of a connected mouse.

The software suite, Windows build, and UF2 structure checks passed. With a
backup mouse, physically verify a semi-auto profile (for example TCSG12):
select it, confirm Rapid Fire is ON, arm output, hold M2+M1 in the game, and
check that per-shot movement continues for the entire hold. Test whether each
shot actually repeats; if not, capture a diagnostic report and do not assume
more RP2040 firmware tuning will solve the two-mouse limitation.

The portable Windows ZIP is unsigned unless a separately signed installer is
present in the release. Verify downloaded files with `SHA256SUMS.txt`.
