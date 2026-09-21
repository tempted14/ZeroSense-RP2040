# Research recoil profile

The optional **Research stages · Y11S1.3 estimate** mode is derived from the
user-supplied `R6_RECOIL_FOR_AI.md` dataset compiled on 2026-09-20. It is kept
separate from General, the original Weapon Pattern estimate, Experimental
tuning, and measured-profile packs.

## What was imported

- Estimated first-shot and three-stage vertical step ratios for 60 automatic
  weapons.
- The documented long-burst starts for M762 (shot 8), R4-C (shot 8), and K1A
  (shot 6); the supplied default of shot 12 is used by the other rows.
- Source RPM and magazine values as provenance metadata, not as replacements
  for the newer timing and capacity values in the original Y11S3 catalog.

The source table has no XK23 row because its coverage ends at Y11S1.3. Research
mode therefore uses the unchanged original estimate for XK23 and says so in the
profile-quality text.

## Safe unit conversion

The source values are pixels in Ubisoft's 960x540 published-plot reference
frame. They are not mouse HID counts. Sending those numbers directly would be
both unjustified and far too aggressive.

For each supported weapon, ZeroSense:

1. Resolves the existing attachment, operator, optic, and game-build reference
   first, including an exact measured profile when one is available.
2. Replaces only its vertical *shape* with the source's first-shot/stage ratios.
3. Normalizes the result so total vertical HID output over the magazine is the
   same as the original profile.
4. Retains the original horizontal trace because the source contains only a
   spread magnitude, not deterministic per-shot left/right directions.
5. Applies the user's normal per-weapon output multiplier afterward.

This makes the new mode useful for comparing timing and stage shape while
keeping the established output magnitude and original profiles untouched.

## Confidence and limitations

Only the source's MX4 Storm and P10 RONI plot data are measured, and those
captures are from Y6S3.3 (2021). The 94-weapon table is explicitly a model:
cross-class scaling, the Y7S3 uplift, later stage magnitude, and several other
parameters are assumptions. Its game coverage ends at Y11S1.3, while the main
catalog is Y11S3.

Accordingly, the UI labels this mode **Estimated · research model**. A measured
loadout may supply its normalization total and horizontal reference, but the
research stage transformation remains an estimate and must not be represented
as an exact or current measured recoil profile.
