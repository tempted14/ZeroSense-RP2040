# Scorpion reference interpretation — 2026-10-19

The supplied shooting-range screenshot shows impacts starting near the lower
center of the target and drifting left as they rise. The user reports that
this continues to the magazine's end. The screenshot is a single trajectory,
not a time-stamped sequence or a repeatability study. It is deliberately not
uploaded with the release or represented as a calibrated measurement.

The v2.3 stock Original-mode estimate keeps the existing RPM, magazine length
and reference conversion (`counts per 8 ms × shot interval / 8`). It replaces
the generic Scorpion alternating horizontal sequence with an estimated ramp
from zero correction over the first few shots to positive/rightward X. A
Compensator's existing horizontal factor is applied once. Vertical correction
settles into a sustained tail rather than growing throughout the magazine.

The onset, transition and amplitude are model parameters, not per-shot values
extracted from pixels. Perspective, sensitivity, distance, aim motion, optic
and attachments prevent a direct screen-pixels-to-HID conversion. Absolute
shot indices keep the onset stable if a custom magazine length is selected;
the settled tail continues until the configured pattern finishes.

The refinement is applied only in `RecoilProfileResolver` after exact loadout/
measured-profile resolution and before output gains/horizontal overrides.
The stored reference catalog, Research, Experimental, custom and measured
traces are not mutated. Tests require nonnegative/rightward correction,
continuous downward correction, a stable full-magazine tail and unchanged
reference data. Physical calibration/repeatability remain unverified.
