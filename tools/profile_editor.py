#!/usr/bin/env python3
"""Edit the custom weapon profiles consumed by the Windows application."""

from __future__ import annotations

import argparse
import json
import math
import os
import shutil
from pathlib import Path
from typing import Any


def default_profiles_file() -> Path:
    local_app_data = os.environ.get("LOCALAPPDATA")
    base = Path(local_app_data) if local_app_data else Path.home() / "AppData" / "Local"
    return base / "RainbowRecoil" / "settings" / "weapon-profiles.json"


def load_profiles(path: Path) -> list[dict[str, Any]]:
    if not path.exists():
        return []

    with path.open(encoding="utf-8") as handle:
        data = json.load(handle)

    # Accept the obsolete {"profiles": [...]} shape for migration, while always
    # writing the list shape expected by WeaponProfile.LoadFromFile.
    profiles = data.get("profiles") if isinstance(data, dict) else data
    if not isinstance(profiles, list) or not all(isinstance(item, dict) for item in profiles):
        raise ValueError("The profile file must contain a JSON array of objects.")
    return profiles


def save_profiles(path: Path, profiles: list[dict[str, Any]]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary_path = path.with_suffix(path.suffix + ".tmp")
    try:
        if path.exists():
            # Validate before replacing the rolling backup so a corrupt primary
            # can never overwrite the last known-good copy.
            load_profiles(path)
            shutil.copy2(path, path.with_suffix(path.suffix + ".bak"))
        with temporary_path.open("w", encoding="utf-8", newline="\n") as handle:
            json.dump(profiles, handle, indent=2, ensure_ascii=False, allow_nan=False)
            handle.write("\n")
        temporary_path.replace(path)
    except (OSError, ValueError):
        temporary_path.unlink(missing_ok=True)
        raise


def print_profiles(profiles: list[dict[str, Any]]) -> None:
    print("=" * 78)
    print("  Rainbow Recoil profile editor")
    print(f"  Profiles: {len(profiles)}")
    print("=" * 78)
    for index, profile in enumerate(profiles):
        name = str(profile.get("name", "<unnamed>"))
        vertical = float(profile.get("verticalCompensation", 0.0))
        horizontal = float(profile.get("horizontalCompensation", 0.0))
        burst = int(profile.get("burstProgression", 0))
        print(
            f"  [{index:2d}] {name:<28} "
            f"V:{vertical:>7.3f} H:{horizontal:>+7.3f} B:{burst:>3d}"
        )


def prompt_float(label: str, current: float, minimum: float, maximum: float) -> float:
    raw_value = input(f"    {label} [{current:g}]: ").strip()
    value = current if not raw_value else float(raw_value)
    if not math.isfinite(value):
        raise ValueError(f"{label} must be a finite number.")
    if not minimum <= value <= maximum:
        raise ValueError(f"{label} must be between {minimum:g} and {maximum:g}.")
    return value


def edit_profile(profile: dict[str, Any]) -> None:
    print(f"  Editing {profile.get('name', '<unnamed>')}")
    profile["verticalCompensation"] = prompt_float(
        "Vertical compensation", float(profile.get("verticalCompensation", 0.0)), 0.0, 20.0
    )
    profile["horizontalCompensation"] = prompt_float(
        "Horizontal compensation", float(profile.get("horizontalCompensation", 0.0)), -20.0, 20.0
    )
    profile["burstProgression"] = round(
        prompt_float(
            "Burst progression (0-100)",
            float(profile.get("burstProgression", 0)),
            0.0,
            100.0,
        )
    )


def interactive_editor(path: Path, profiles: list[dict[str, Any]]) -> int:
    print_profiles(profiles)
    dirty = False

    while True:
        command = input("\nAction [a=add, e=edit, l=list, s=save, q=quit]: ").strip().lower()
        try:
            if command == "q":
                if dirty:
                    print("Unsaved changes were not written.")
                return 0
            if command == "l":
                print_profiles(profiles)
            elif command == "s":
                save_profiles(path, profiles)
                dirty = False
                print(f"Saved {len(profiles)} profile(s) to {path}")
            elif command == "e":
                index = int(input(f"  Profile index [0-{len(profiles) - 1}]: "))
                if not 0 <= index < len(profiles):
                    raise ValueError("Profile index is out of range.")
                edit_profile(profiles[index])
                dirty = True
            elif command == "a":
                name = input("  New profile name: ").strip()
                if not name:
                    raise ValueError("A profile name is required.")
                profiles.append(
                    {
                        "name": name,
                        "verticalCompensation": 1.0,
                        "horizontalCompensation": 0.0,
                        "burstProgression": 0,
                    }
                )
                dirty = True
            else:
                print("Unknown command. Use a, e, l, s, or q.")
        except (ValueError, TypeError) as error:
            print(f"[ERROR] {error}")


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--file",
        type=Path,
        default=default_profiles_file(),
        help="Profile JSON file (defaults to the Windows app's LocalAppData file)",
    )
    parser.add_argument("--list", action="store_true", help="List profiles and exit")
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    try:
        profiles = load_profiles(args.file)
        if args.list:
            print_profiles(profiles)
            return 0
        return interactive_editor(args.file, profiles)
    except (OSError, json.JSONDecodeError, ValueError) as error:
        print(f"[ERROR] {error}")
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
