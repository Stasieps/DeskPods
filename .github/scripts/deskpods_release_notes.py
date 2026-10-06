#!/usr/bin/env python3
"""Write (or print) the release notes for the current VERSION.

    python .github/scripts/deskpods_release_notes.py --out release-notes.md

The notes are Ukrainian, and the Windows runner's console is cp1252, so
printing them there raises UnicodeEncodeError. That is what stopped the 0.8.42
release after the installer was already built, so the text is written straight
to a UTF-8 file and stdout is only a fallback.

The notes are the "## X.Y.Z" section of CHANGELOG.md, so the release page and
the changelog can never tell two different stories. A missing section is
not fatal: a short fallback is printed instead.
"""
from __future__ import annotations

import argparse
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]


def notes() -> str:
    version = (ROOT / "VERSION").read_text(encoding="utf-8").strip()
    readme = (ROOT / "CHANGELOG.md").read_text(encoding="utf-8")
    pattern = re.compile(
        rf"^## {re.escape(version)}\b[^\n]*$(.*?)(?=^## |\Z)",
        re.MULTILINE | re.DOTALL,
    )
    match = pattern.search(readme)
    body = match.group(1).strip() if match else f"DeskPods {version}."
    return "\n".join([
        f"## Що нового у {version}",
        "",
        body,
        "",
        "## Встановлення",
        "",
        f"1. Завантаж **DeskPods_Setup_v{version}.exe** нижче.",
        "2. Запусти його — ставиться поверх попередньої версії, без прав адміністратора.",
        "3. Налаштування лишаються на місці.",
        "",
        "Windows може показати «Windows protected your PC» — натисни **More info → Run anyway**.",
        "",
        "Portable ZIP лежить поруч, якщо не хочеш нічого встановлювати.",
        "",
    ])


def main() -> int:
    parser = argparse.ArgumentParser(description="Release notes for the current VERSION")
    parser.add_argument("--out", help="file to write the notes into, always UTF-8")
    args = parser.parse_args()
    text = notes()
    if args.out:
        Path(args.out).write_text(text, encoding="utf-8")
        print(f"Release notes written to {args.out}")
        return 0
    sys.stdout.reconfigure(encoding="utf-8")
    sys.stdout.write(text)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
