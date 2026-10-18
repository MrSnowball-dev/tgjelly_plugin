"""Tests for build/package.py. Run: python3 -m unittest discover -s build -p 'test_*.py' -v"""

import json
import tempfile
import unittest
import zipfile
from pathlib import Path

import package


class TrackVersionTests(unittest.TestCase):
    def test_whitelist_includes_telegram_bot(self):
        self.assertEqual(package.ASSEMBLIES, ["Jellyfin.Plugin.TgJelly.dll", "Telegram.Bot.dll"])

    def test_maps_release_to_both_tracks(self):
        self.assertEqual(package.track_version(1, "0.3"), "1.0.3.0")
        self.assertEqual(package.track_version(2, "1.12"), "2.1.12.0")

    def test_every_12_build_outranks_every_1011_build(self):
        newest_1011 = tuple(int(p) for p in package.track_version(1, "99.99").split("."))
        oldest_12 = tuple(int(p) for p in package.track_version(2, "0.0").split("."))
        self.assertLess(newest_1011, oldest_12)

    def test_rejects_malformed_release(self):
        for bad in ["1", "1.2.3", "v1.2", "1.x", ""]:
            with self.assertRaises(ValueError, msg=bad):
                package.track_version(1, bad)


class MetaTests(unittest.TestCase):
    def test_meta_matches_spec(self):
        meta = package.make_meta("2.0.1.0", "12.0.0.0", "notes", "2026-10-17T00:00:00Z")
        self.assertEqual(meta["guid"], "e547b901-0dc4-42ae-8999-3871dd141270")
        self.assertEqual(meta["name"], "TgJelly")
        self.assertEqual(meta["category"], "Administration")
        self.assertEqual(meta["targetAbi"], "12.0.0.0")
        self.assertEqual(meta["imagePath"], "")
        self.assertEqual(meta["assemblies"], package.ASSEMBLIES)


class ZipTests(unittest.TestCase):
    def _publish_dir(self, root: Path, extra=()):
        publish = root / "publish"
        publish.mkdir()
        for name in package.ASSEMBLIES + list(extra):
            (publish / name).write_bytes(b"dll")
        return publish

    def test_stage_and_zip_contain_exactly_the_whitelist(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            publish = self._publish_dir(root, extra=["System.Text.Json.dll", "Jellyfin.Plugin.TgJelly.pdb"])
            stage = root / "stage"
            package.stage(publish, stage, package.make_meta("1.0.0.0", "10.11.0.0", "", "t"))
            zip_path = root / "out.zip"
            package.write_zip(stage, zip_path)
            with zipfile.ZipFile(zip_path) as archive:
                self.assertEqual(set(archive.namelist()), package.expected_zip_names())
                meta = json.loads(archive.read("meta.json"))
            self.assertEqual(meta["version"], "1.0.0.0")

    def test_verify_zip_rejects_extra_files(self):
        with tempfile.TemporaryDirectory() as tmp:
            zip_path = Path(tmp) / "bad.zip"
            with zipfile.ZipFile(zip_path, "w") as archive:
                for name in package.expected_zip_names() | {"Microsoft.Extensions.Options.dll"}:
                    archive.writestr(name, "x")
            with self.assertRaises(ValueError):
                package.verify_zip(zip_path)

    def test_stage_fails_when_a_dll_is_missing(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            (root / "publish").mkdir()
            with self.assertRaises(FileNotFoundError):
                package.stage(root / "publish", root / "stage", package.make_meta("1.0.0.0", "10.11.0.0", "", "t"))


class ManifestTests(unittest.TestCase):
    def _version(self, version, abi):
        return {"version": version, "changelog": "", "targetAbi": abi, "sourceUrl": "u", "checksum": "c", "timestamp": "t"}

    def test_creates_entry_with_image_url(self):
        manifest = package.update_manifest([], [self._version("1.0.1.0", "10.11.0.0")])
        entry = manifest[0]
        self.assertEqual(entry["guid"], package.GUID)
        self.assertEqual(entry["imageUrl"], package.IMAGE_URL)
        self.assertEqual(entry["category"], "Administration")

    def test_keeps_older_versions_replaces_same_version_newest_first(self):
        old = package.update_manifest(
            [{"guid": "other-plugin", "versions": []}],
            [self._version("1.0.1.0", "10.11.0.0"), self._version("2.0.1.0", "12.0.0.0")],
        )
        updated = package.update_manifest(
            old,
            [self._version("1.0.2.0", "10.11.0.0"), self._version("2.0.2.0", "12.0.0.0"), self._version("2.0.1.0", "12.0.0.0")],
        )
        entry = next(p for p in updated if p["guid"] == package.GUID)
        self.assertEqual([v["version"] for v in entry["versions"]], ["2.0.2.0", "2.0.1.0", "1.0.2.0", "1.0.1.0"])
        self.assertEqual(len(updated), 2)


if __name__ == "__main__":
    unittest.main()
