#!/usr/bin/env python3
"""Build the TgJelly plugin zips and the plugin repository manifest (spec §13.3-§13.4).

One release tag vX.Y produces two builds from the same commit:
  track 1: net9.0,  targetAbi 10.11.0.0, version 1.X.Y.0  (Jellyfin 10.11)
  track 2: net10.0, targetAbi 12.0.0.0,  version 2.X.Y.0  (Jellyfin 12)
Every 12 build therefore outranks every 10.11 build, which Jellyfin needs because it only checks
targetAbi <= server version (spec F7).

Usage:
  python3 build/package.py --release 0.1 --changelog "First release" \
      [--output artifacts] [--manifest-in old-manifest.json] [--source-url-base URL]
"""

import argparse
import hashlib
import json
import shutil
import subprocess
import sys
import zipfile
from datetime import datetime, timezone
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
PROJECT = ROOT / "src" / "Jellyfin.Plugin.TgJelly" / "Jellyfin.Plugin.TgJelly.csproj"

GUID = "e547b901-0dc4-42ae-8999-3871dd141270"
NAME = "TgJelly"
OWNER = "MrSnowball-dev"
CATEGORY = "Administration"
OVERVIEW = "Telegram notifications for Jellyfin"
DESCRIPTION = (
    "A Telegram bot inside your Jellyfin server: new media, security and server notifications "
    "for the chats, groups and channels you choose, set up from the dashboard or the bot."
)
IMAGE_URL = "https://mrsnowball-dev.github.io/tgjelly_plugin/logo.png"
REPO_URL = "https://github.com/MrSnowball-dev/tgjelly_plugin"

# Exactly these DLLs ship, and meta.json lists them, so no stray framework DLL is ever loaded (spec §13.4, F5).
# Plan 5 adds Scriban.dll.
ASSEMBLIES = ["Jellyfin.Plugin.TgJelly.dll", "Telegram.Bot.dll"]

TRACKS = [
    {"track": 1, "tfm": "net9.0", "target_abi": "10.11.0.0"},
    {"track": 2, "tfm": "net10.0", "target_abi": "12.0.0.0"},
]


def track_version(track: int, release: str) -> str:
    """Map release "X.Y" to the four-part version of a track, e.g. (2, "0.3") -> "2.0.3.0"."""
    parts = release.split(".")
    if len(parts) != 2 or not all(p.isdigit() for p in parts):
        raise ValueError(f"release must look like X.Y, got {release!r}")
    return f"{track}.{int(parts[0])}.{int(parts[1])}.0"


def make_meta(version: str, target_abi: str, changelog: str, timestamp: str) -> dict:
    """The meta.json packaged in the zip. imagePath stays empty so the repository's imageUrl is used (F5)."""
    return {
        "guid": GUID,
        "name": NAME,
        "overview": OVERVIEW,
        "description": DESCRIPTION,
        "owner": OWNER,
        "category": CATEGORY,
        "version": version,
        "targetAbi": target_abi,
        "changelog": changelog,
        "timestamp": timestamp,
        "imagePath": "",
        "assemblies": list(ASSEMBLIES),
    }


def expected_zip_names() -> set:
    return set(ASSEMBLIES) | {"meta.json"}


def stage(publish_dir: Path, stage_dir: Path, meta: dict) -> None:
    """Copy exactly the whitelisted DLLs plus meta.json into stage_dir."""
    if stage_dir.exists():
        shutil.rmtree(stage_dir)
    stage_dir.mkdir(parents=True)
    for name in ASSEMBLIES:
        source = publish_dir / name
        if not source.is_file():
            raise FileNotFoundError(f"{name} is missing from {publish_dir}")
        shutil.copy2(source, stage_dir / name)
    (stage_dir / "meta.json").write_text(json.dumps(meta, indent=2) + "\n", encoding="utf-8")


def write_zip(stage_dir: Path, zip_path: Path) -> None:
    zip_path.parent.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(zip_path, "w", compression=zipfile.ZIP_DEFLATED) as archive:
        for path in sorted(stage_dir.iterdir()):
            archive.write(path, arcname=path.name)
    verify_zip(zip_path)


def verify_zip(zip_path: Path) -> None:
    """Fail unless the zip holds exactly the whitelisted DLLs and meta.json, at its root."""
    with zipfile.ZipFile(zip_path) as archive:
        names = set(archive.namelist())
    expected = expected_zip_names()
    if names != expected:
        extra = sorted(names - expected)
        missing = sorted(expected - names)
        raise ValueError(f"{zip_path.name}: unexpected contents; extra={extra} missing={missing}")


def md5(path: Path) -> str:
    digest = hashlib.md5()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(65536), b""):
            digest.update(chunk)
    return digest.hexdigest()


def _version_key(entry: dict) -> tuple:
    return tuple(int(p) for p in entry["version"].split("."))


def update_manifest(manifest: list, new_versions: list) -> list:
    """Add version entries to TgJelly's PackageInfo, keeping older ones; newest first (spec §13.4)."""
    package = next((p for p in manifest if p.get("guid") == GUID), None)
    if package is None:
        package = {"guid": GUID, "versions": []}
        manifest.append(package)
    package.update(
        {
            "name": NAME,
            "description": DESCRIPTION,
            "overview": OVERVIEW,
            "owner": OWNER,
            "category": CATEGORY,
            "imageUrl": IMAGE_URL,
        }
    )
    replaced = {v["version"] for v in new_versions}
    kept = [v for v in package.get("versions", []) if v["version"] not in replaced]
    package["versions"] = sorted(kept + list(new_versions), key=_version_key, reverse=True)
    return manifest


def publish(tfm: str, version: str, out_dir: Path, dotnet: str) -> None:
    subprocess.run(
        [
            dotnet, "publish", str(PROJECT),
            "-c", "Release",
            "-f", tfm,
            "-o", str(out_dir),
            f"-p:Version={version}",
            f"-p:AssemblyVersion={version}",
            f"-p:FileVersion={version}",
        ],
        check=True,
    )


def main(argv: list) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--release", required=True, help="release number X.Y (from tag vX.Y)")
    parser.add_argument("--changelog", default="", help="changelog text for this release")
    parser.add_argument("--output", default=str(ROOT / "artifacts"), help="output folder")
    parser.add_argument("--manifest-in", help="existing manifest.json to extend")
    parser.add_argument("--source-url-base", help="base URL of the zips (default: the GitHub release of tag vX.Y)")
    parser.add_argument("--dotnet", default="dotnet", help="dotnet executable")
    args = parser.parse_args(argv)

    output = Path(args.output).resolve()
    base_url = args.source_url_base or f"{REPO_URL}/releases/download/v{args.release}"
    timestamp = datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")

    manifest = []
    if args.manifest_in:
        manifest = json.loads(Path(args.manifest_in).read_text(encoding="utf-8"))

    new_versions = []
    for track in TRACKS:
        version = track_version(track["track"], args.release)
        publish_dir = output / "publish" / track["tfm"]
        stage_dir = output / "stage" / track["tfm"]
        zip_path = output / f"tgjelly_{version}.zip"

        publish(track["tfm"], version, publish_dir, args.dotnet)
        stage(publish_dir, stage_dir, make_meta(version, track["target_abi"], args.changelog, timestamp))
        write_zip(stage_dir, zip_path)
        new_versions.append(
            {
                "version": version,
                "changelog": args.changelog,
                "targetAbi": track["target_abi"],
                "sourceUrl": f"{base_url}/{zip_path.name}",
                "checksum": md5(zip_path),
                "timestamp": timestamp,
            }
        )
        print(f"built {zip_path.name} (targetAbi {track['target_abi']})")

    manifest_path = output / "manifest.json"
    manifest_path.write_text(json.dumps(update_manifest(manifest, new_versions), indent=2) + "\n", encoding="utf-8")
    print(f"wrote {manifest_path}")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
