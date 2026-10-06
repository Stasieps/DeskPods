#!/usr/bin/env python3
"""Single source of truth for the DeskPods version number.

VERSION holds the only version number in the repository. This script copies it
into every place that must agree with it, and can verify that they do.

    python .github/scripts/deskpods_version.py --check   # CI gate, no writes
    python .github/scripts/deskpods_version.py --apply   # rewrite the anchors
    python .github/scripts/deskpods_version.py --print   # echo the version

Every rewrite is anchored on an exact pattern. An anchor that no longer matches
is a hard error, so a renamed anchor fails the build instead of silently
leaving an old number behind. Line endings of the edited files are preserved.
"""
from __future__ import annotations

import argparse
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
VERSION_FILE = ROOT / "VERSION"
SEMVER = re.compile(r"^\d+\.\d+\.\d+$")
VER = r"\d+\.\d+\.\d+"

# (path, pattern with a {v} placeholder, number of matches expected)
ANCHORS: list[tuple[str, str, int]] = [
    ("src/PodsView/PodsView.csproj", "<Version>{v}</Version>", 1),
    ("installer/DeskPods.iss", '#define MyAppVersion "{v}"', 1),
    ("START.cmd", "title DeskPods {v}", 1),
    ("START.cmd", 'set "EXPECTED={v}"', 1),
    ("START.cmd", "echo   DeskPods {v}", 1),
]


def read_version() -> str:
    version = VERSION_FILE.read_text(encoding="utf-8").strip()
    if not SEMVER.match(version):
        sys.exit(f"VERSION must be X.Y.Z, got {version!r}")
    return version


def read_keep_newlines(path: Path) -> str:
    with path.open("r", encoding="utf-8", newline="") as handle:
        return handle.read()


def write_keep_newlines(path: Path, text: str) -> None:
    with path.open("w", encoding="utf-8", newline="") as handle:
        handle.write(text)


def process(apply: bool) -> int:
    version = read_version()
    problems: list[str] = []
    changed: list[str] = []

    for rel, template, expected in ANCHORS:
        path = ROOT / rel
        if not path.exists():
            problems.append(f"{rel}: file is missing")
            continue
        text = read_keep_newlines(path)
        loose = re.escape(template).replace(r"\{v\}", VER)
        wanted = template.format(v=version)
        found = re.findall(loose, text)
        if len(found) != expected:
            problems.append(
                f"{rel}: anchor {template!r} matched {len(found)} time(s), expected {expected}"
            )
            continue
        if all(item == wanted for item in found):
            continue
        if not apply:
            problems.append(f"{rel}: holds {found[0]!r}, VERSION says {version}")
            continue
        write_keep_newlines(path, re.sub(loose, wanted.replace("\\", "\\\\"), text))
        changed.append(rel)

    if problems:
        print("Version check failed:")
        for problem in problems:
            print(f"  - {problem}")
        return 1

    if apply and changed:
        print(f"Version {version} written into: {', '.join(sorted(set(changed)))}")
    else:
        print(f"Version {version} is consistent everywhere.")
    return 0


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--check", action="store_true", help="fail if anything disagrees with VERSION")
    parser.add_argument("--apply", action="store_true", help="rewrite every anchor from VERSION")
    parser.add_argument("--print", dest="show", action="store_true", help="print the version and exit")
    args = parser.parse_args()
    if args.show:
        print(read_version())
        return 0
    if args.check == args.apply:
        parser.error("choose exactly one of --check or --apply")
    return process(apply=args.apply)


if __name__ == "__main__":
    raise SystemExit(main())
