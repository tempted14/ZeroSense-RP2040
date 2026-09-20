# Recoil data and calculation audit

Last checked: 2026-09-19

## Source priority

1. Ubisoft release notes and Designer's Notes determine the current weapon,
   magazine, attachment, and recoil-stage rules.
2. The maintained `rainbow-six-siege-weapon-statistics` dataset supplies
   hand-measured timing where Ubisoft does not publish a machine-readable RPM.
3. Video-derived shapes and the built-in curves are calibration estimates only.

This distinction matters: Ubisoft does not publish numeric per-shot horizontal
and vertical recoil vectors. The app must not label a generated curve as exact
game data. A versioned measured-profile pack is the only source treated as a
measured X/Y trace, and it applies only to its exact weapon, grip, barrel, optic,
game build, and RPM.

## Current facts pinned by tests

- Y11S3 reduces the SMG-12 magazine to 22 rounds. The older third-party JSON
  still says 32, so the official release note overrides it.
- Y10S3 reduces the MK17 CQB magazine to 20 rounds.
- Y11S1 restores grip choices to the F2; the built-in automatic setup uses the
  requested Vertical Grip.
- Y11S2.3 moves the REAPER-MK2 recoil stage starts from bullets 0/3/7/13 to
  0/3/10/25. The generator now uses those exact stage boundaries, while clearly
  treating the stage magnitudes and left/right trace as estimates.
- Y11S3 removes Muzzle Brake from Aruni's Mk 14 EBR and Tubarão's AR-15.50.
  Operator-specific attachment resolution preserves it for Dokkaebi and
  Maverick.
- Ubisoft's published attachment values are modeled once: Vertical Grip reduces
  vertical recoil by 20%, Compensator reduces horizontal recoil by 35%, and
  Muzzle Brake reduces first-shot recoil by 50%. The current Flash Hider numeric
  factor is still labeled as an estimate.

## Calculation invariants

- A catalog compensation value is a continuous HID-count rate expressed per
  8 ms general-mode tick. Per-shot displacement is therefore
  `value * (60000 / RPM) / 8` before the deterministic weapon-stage scale.
- Tests compare different RPM values and require displacement-per-minute to be
  invariant. Changing RPM changes the size of each point, not the underlying
  compensation rate.
- Muzzle Brake changes only point zero. Compensator changes only the generated
  horizontal component. Tests enforce both factors across the full pattern.
- Firmware phase-locks each point to `60,000,000 / RPM` microseconds. Integer
  division remainder is carried into later intervals, so one complete RPM cycle
  totals exactly 60,000,000 microseconds without systematic rounding drift. Each
  point is spread over the full shot interval in 1 ms HID frames using Q16 fixed
  point. Normal magazine completion rounds the final sub-count residue once and
  preserves queued HID deltas; emergency stop, disconnect, and watchdog paths
  still clear correction state immediately.
- Pattern mode and rapid-fire mode are mutually exclusive in firmware, avoiding
  two recoil generators operating on the same shot.

## Primary references

- [Ubisoft weapon recoil overhaul](https://www.ubisoft.com/en-us/game/rainbow-six/siege/news-updates/1k3EGuOGxKxe6mhOlFBhbj/weapon-recoil-overhaul)
- [Ubisoft Brutal Swarm recoil stages](https://www.ubisoft.com/en-au/game/rainbow-six/siege/news-updates/seasons/brutalswarm)
- [Ubisoft Y11S3 Split Fire notes](https://www.ubisoft.com/en-us/game/rainbow-six/siege/news-updates/seasons/splitfire)
- [Ubisoft Y11S2.3 patch notes](https://www.ubisoft.com/pt-br/game/rainbow-six/siege/news-updates/2EIn06EmkAIG7su2fpITue/y11s23-patch-notes)
- [Ubisoft Y11S1 Silent Hunt notes](https://www.ubisoft.com/en-us/game/rainbow-six/siege/news-updates/seasons/silenthunt)
- [Ubisoft Y10S3 Designer's Notes](https://www.ubisoft.com/en-gb/game/rainbow-six/siege/news-updates/4czc3hlHtXrd27crELGDcc)
- [Ubisoft Y9S1 Designer's Notes](https://www.ubisoft.com/es-es/game/rainbow-six/siege/news-updates/3jBlCdtRBQx2sCjmY2umNu/y9s1-designers-notes)
- [Ubisoft Y8S1 Designer's Notes](https://www.ubisoft.com/en-gb/game/rainbow-six/siege/news-updates/2fOZy6mrCdlSuGhNFSYrK9/y8s1-designers-notes)
- [Hand-measured Y11 weapon statistics](https://github.com/hanslhansl/rainbow-six-siege-weapon-statistics)
