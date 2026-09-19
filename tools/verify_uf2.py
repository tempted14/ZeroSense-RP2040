#!/usr/bin/env python3
"""Validate UF2 framing and the RP2040/RP2350 family identifier."""

from __future__ import annotations

import argparse
import struct
from pathlib import Path


UF2_BLOCK_SIZE = 512
UF2_MAGIC_START0 = 0x0A324655
UF2_MAGIC_START1 = 0x9E5D5157
UF2_MAGIC_END = 0x0AB16F30
UF2_FLAG_FAMILY_ID = 0x00002000
FAMILY_IDS = {
    "rp2040": 0xE48BFF56,
    "rp2350": 0xE48BFF59,
}


def validate_uf2(path: Path, expected_family: int) -> tuple[int, int]:
    data = path.read_bytes()
    if not data or len(data) % UF2_BLOCK_SIZE:
        raise ValueError(f"{path}: size is not a non-zero multiple of 512 bytes")

    block_count = len(data) // UF2_BLOCK_SIZE
    previous_end = None
    for index in range(block_count):
        offset = index * UF2_BLOCK_SIZE
        (magic0, magic1, flags, target_address, payload_size,
         block_number, declared_blocks, family_id) = struct.unpack_from(
            "<8I", data, offset
        )
        end_magic = struct.unpack_from("<I", data, offset + 508)[0]
        if (magic0, magic1, end_magic) != (
            UF2_MAGIC_START0,
            UF2_MAGIC_START1,
            UF2_MAGIC_END,
        ):
            raise ValueError(f"{path}: invalid UF2 magic in block {index}")
        if not flags & UF2_FLAG_FAMILY_ID or family_id != expected_family:
            raise ValueError(
                f"{path}: block {index} family 0x{family_id:08X}, "
                f"expected 0x{expected_family:08X}"
            )
        if block_number != index or declared_blocks != block_count:
            raise ValueError(f"{path}: inconsistent block numbering at {index}")
        if payload_size == 0 or payload_size > 476:
            raise ValueError(f"{path}: invalid payload size in block {index}")
        if previous_end is not None and target_address < previous_end:
            raise ValueError(f"{path}: target addresses overlap or run backward")
        previous_end = target_address + payload_size

    return len(data), block_count


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("family", choices=sorted(FAMILY_IDS))
    parser.add_argument("path", type=Path)
    args = parser.parse_args()
    size, blocks = validate_uf2(args.path, FAMILY_IDS[args.family])
    print(
        f"PASS {args.path}: {size} bytes, {blocks} blocks, "
        f"{args.family} family 0x{FAMILY_IDS[args.family]:08X}"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
