# ZeroSense v1.8.0 candidate — not released

The original automatic-weapon pattern mode has a saved, adjustable 1–4×
output multiplier with a 2× default. It scales X and Y of the supplied
estimated patterns after the per-point profile transfer limit, preserving the
source pattern and shot timing. Measured profiles and General, Experimental,
and Research modes do not use this multiplier.

The existing 2.5× vertical boost still applies independently. The matching
RP2040/RP2350 firmware increases the validated sensitivity-factor ceiling to
16× so the strongest combined setting can be transferred exactly. App and
firmware must be updated together for settings above the v1.7.0 firmware's 8×
limit. Physical recoil strength and RP2350 USB-host stability still require
real-board testing before this candidate should be published as a release.
