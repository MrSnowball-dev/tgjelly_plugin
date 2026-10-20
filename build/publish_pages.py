#!/usr/bin/env python3
"""Prepare the gh-pages checkout of the plugin repository (spec §13.4).

Usage:
  python3 build/publish_pages.py --pages-dir pages --manifest artifacts/manifest.json --logo build/logo.png
"""

import argparse
import json
import shutil
import sys
from pathlib import Path

import package


def prepare(pages_dir: Path, manifest: Path, logo: Path | None) -> list[str]:
    """Copy the manifest (always) and the logo (only when the folder has none); return the changed files."""
    entries = json.loads(manifest.read_text(encoding="utf-8"))
    if not any(isinstance(e, dict) and e.get("guid") == package.GUID for e in entries):
        raise ValueError(f"{manifest} has no TgJelly entry with guid {package.GUID}")

    pages_dir.mkdir(parents=True, exist_ok=True)
    changed = []
    shutil.copyfile(manifest, pages_dir / "manifest.json")
    changed.append("manifest.json")
    if logo is not None and logo.is_file() and not (pages_dir / "logo.png").exists():
        shutil.copyfile(logo, pages_dir / "logo.png")
        changed.append("logo.png")
    return changed


def main(argv: list) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--pages-dir", required=True, type=Path)
    parser.add_argument("--manifest", required=True, type=Path)
    parser.add_argument("--logo", type=Path)
    args = parser.parse_args(argv)
    try:
        changed = prepare(args.pages_dir, args.manifest, args.logo)
    except (ValueError, OSError) as error:
        print(f"error: {error}", file=sys.stderr)
        return 1
    print("updated: " + ", ".join(changed))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
