# RP2350 host-stall test build (unreleased)

The prior app-closed freeze capture showed a host-loop age increasing beyond
one minute while `HOST_REPORTS` stopped and the board's COM interface remained
available. The published v1.7.0 firmware removed one known PIO TX EOP wait,
but other host waits remained unbounded. A later failed diagnostic attempt
returned a Windows COM semaphore timeout before STATUS could be sent. That
error does **not** identify which firmware loop, USB device endpoint, cable,
or Windows driver path failed.

This separate test UF2 starts from the local v1.8 multiplier candidate. It
bounds TX completion, RX flag-clear and packet-receive waits; filters brief
SE0 glitches before treating them as disconnects; and
adds `PIO_TX_TIMEOUTS`, `PIO_RX_FLAG_TIMEOUTS`, `PIO_RX_PACKET_TIMEOUTS`, and
`PIO_SE0_GLITCHES` to STATUS. It preserves the v1.8 recoil multiplier,
patterns, 2.5x boost, jitter, delta noise, and configuration contract. The
selected changes are informed by [Pico-PIO-USB PR #206](https://github.com/sekigon-gonnoc/Pico-PIO-USB/pull/206),
which remains unmerged and has device-compatibility reports. The TX timeout
here uses a longer budget than that proposal.

The RP2350 target compiles, the UF2 family and embedded build marker were
verified, and native replay, Python, and .NET tests pass. No physical soak
test has been performed. The first test build marker was
`BUILD:HOST-STALL-TEST-X2-20260922`. That build failed mouse enumeration on
the user's board: `HOST_REPORTS=0` and `PIO_RX_FLAG_TIMEOUTS=30`. Its new
EOP-detector reset has been removed. The RX-start flag-clear loop was also
removed: a fast reply can set `RX_START` immediately after the clear, causing
the loop to wait for a valid packet and miss it. The replacement candidate is marked
`BUILD:HOST-RX-FIX-X2-20260922`. The older desktop app also failed to parse
the four new PIO counters and displayed the raw METRICS line as a connection
detail; the parser and telemetry display now recognize them. Neither
candidate is published. The replacement still needs physical testing before
passthrough or disconnect reliability can be claimed.

Keep a backup mouse connected directly to the PC. Flash only the test UF2 to
the RP2350 in BOOTSEL mode, verify ordinary passthrough with the app closed,
then test at the normal 1000 Hz setting. If a freeze returns, use the backup
mouse to capture three STATUS samples before unplugging, if COM opens. If COM
does not open, record the exact Windows error; the missing counters cannot be
assumed to be zero. Reflash the included rollback UF2 if the test build is
worse. A successful software build is not proof of hardware reliability.
