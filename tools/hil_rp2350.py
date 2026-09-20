#!/usr/bin/env python3
"""Non-destructive RP2350 CDC/HID hardware-in-the-loop checks.

Requires pyserial and a flashed RP2350. Add --interactive to verify that raw
downstream M1+M2, rather than the upstream synthesized report, activates output.
"""

from __future__ import annotations

import argparse
import struct
import sys
import time

try:
    import serial
except ImportError as exc:  # pragma: no cover - environment dependent
    raise SystemExit("Install pyserial first: python -m pip install pyserial") from exc


MAGIC = b"\xA5\x5A"
VERSION = 1
sequence = 0


def crc16(data: bytes) -> int:
    crc = 0xFFFF
    for value in data:
        crc ^= value << 8
        for _ in range(8):
            crc = ((crc << 1) ^ 0x1021) & 0xFFFF if crc & 0x8000 else (crc << 1) & 0xFFFF
    return crc


def frame(command: int, payload: bytes = b"") -> bytes:
    global sequence
    if len(payload) > 63:
        raise ValueError("payload exceeds protocol maximum")
    sequence = (sequence + 1) & 0xFFFF
    body = struct.pack("<BHB", VERSION, sequence, command) + bytes([len(payload)]) + payload
    return MAGIC + body + struct.pack("<H", crc16(body))


def read_lines(port: serial.Serial, duration: float) -> list[str]:
    lines: list[str] = []
    deadline = time.monotonic() + duration
    while time.monotonic() < deadline:
        raw = port.readline()
        if raw:
            line = raw.decode("utf-8", errors="replace").strip()
            if line:
                print(line)
                lines.append(line)
    return lines


def require(lines: list[str], prefix: str) -> None:
    if not any(line.startswith(prefix) for line in lines):
        raise RuntimeError(f"expected response starting with {prefix!r}")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("port", help="CDC port, for example COM7 or /dev/ttyACM0")
    parser.add_argument("--interactive", action="store_true")
    args = parser.parse_args()

    with serial.Serial(args.port, 115200, timeout=0.10, write_timeout=0.5) as port:
        port.reset_input_buffer()
        port.write(frame(0xF0))
        lines = read_lines(port, 1.5)
        require(lines, "PONG:RAINBOW-RECOIL:4")
        require(lines, "DEVICE:RP2350-USB-C:MOUSE-PROXY")

        port.write(frame(0xFC))
        require(read_lines(port, 0.6), "STATUS:HASH=")

        # A bad CRC must be rejected, and a valid frame immediately after it
        # proves that the stream parser recovered its synchronization.
        damaged = bytearray(frame(0xF0))
        damaged[-1] ^= 0x80
        port.write(damaged)
        port.write(frame(0xF0))
        recovered = read_lines(port, 1.0)
        require(recovered, "ERROR:FRAME:CRC")
        require(recovered, "PONG:RAINBOW-RECOIL:4")

        # If a frame loses its final CRC byte, the next frame's first A5 byte
        # is consumed while detecting the CRC error. The parser must preserve
        # that byte as the next synchronization candidate.
        truncated_crc = frame(0xF0)[:-1]
        port.write(truncated_crc + frame(0xF0))
        overlapped = read_lines(port, 1.0)
        require(overlapped, "ERROR:FRAME:CRC")
        require(overlapped, "PONG:RAINBOW-RECOIL:4")

        # Exercise partial-frame timeout and recovery.
        port.write(MAGIC + bytes([VERSION]))
        time.sleep(0.35)
        port.write(frame(0xF0))
        timed_out = read_lines(port, 1.0)
        require(timed_out, "ERROR:FRAME:TIMEOUT")
        require(timed_out, "PONG:RAINBOW-RECOIL:4")

        if args.interactive:
            print("Hold physical M1+M2, then release both within 8 seconds...")
            observed: list[str] = []
            deadline = time.monotonic() + 8.0
            while time.monotonic() < deadline:
                port.write(frame(0xF8, b"\x01"))
                observed.extend(read_lines(port, 0.18))
            port.write(frame(0xF8, b"\x00"))
            require(observed, "START:")
            require(observed, "STOP:PHYSICAL_RELEASE")

    print("RP2350 HIL checks passed")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (RuntimeError, serial.SerialException) as exc:
        print(f"HIL FAILED: {exc}", file=sys.stderr)
        raise SystemExit(1)
