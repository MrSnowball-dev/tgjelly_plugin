"""Tests for build/package.py. Run: python3 -m unittest discover -s build -p 'test_*.py' -v"""

import json
import tempfile
import unittest
import zipfile
from pathlib import Path

import package


class VersionTests(unittest.TestCase):
    def test_whitelist_includes_telegram_bot_and_scriban(self):
        self.assertEqual(package.ASSEMBLIES, ["Jellyfin.Plugin.TgJelly.dll", "Telegram.Bot.dll", "Scriban.dll"])

    def test_maps_release_to_four_parts(self):
        self.assertEqual(package.version_for("1.0"), "1.0.0.0")
        self.assertEqual(package.version_for("1.2"), "1.2.0.0")
        self.assertEqual(package.version_for("1.2.3"), "1.2.3.0")

    def test_rejects_malformed_release(self):
        for bad in ["1", "1.2.3.4", "v1.2", "1.x", ""]:
            with self.assertRaises(ValueError, msg=bad):
                package.version_for(bad)

    def test_one_build_for_10_11_and_later(self):
        self.assertEqual(package.TARGET_ABI, "10.11.0.0")


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

    def test_verify_zip_accepts_the_three_dlls_plus_meta(self):
        self.assertIn("Scriban.dll", package.ASSEMBLIES)
        with tempfile.TemporaryDirectory() as tmp:
            zip_path = Path(tmp) / "ok.zip"
            with zipfile.ZipFile(zip_path, "w") as archive:
                for name in ["Jellyfin.Plugin.TgJelly.dll", "Telegram.Bot.dll", "Scriban.dll", "meta.json"]:
                    archive.writestr(name, "x")
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

    def _released(self, version, tag):
        entry = self._version(version, "10.11.0.0")
        entry["sourceUrl"] = f"{package.REPO_URL}/releases/download/{tag}/tgjelly_{version}.zip"
        return entry

    def test_pre_release_entries_leave_the_manifest(self):
        # The 0.x builds were 1.0.N.0 and 2.0.N.0: left in, 1.0.9.0 would outrank 1.0.0.0 on 10.11.
        old = package.update_manifest(
            [], [self._released("2.0.9.0", "v0.9"), self._released("1.0.9.0", "v0.9"), self._released("1.0.1.0", "v0.1")]
        )
        updated = package.update_manifest(old, [self._released("1.0.0.0", "v1.0")])
        self.assertEqual([v["version"] for v in updated[0]["versions"]], ["1.0.0.0"])

    def test_a_later_release_keeps_the_earlier_ones(self):
        old = package.update_manifest([], [self._released("1.0.0.0", "v1.0")])
        updated = package.update_manifest(old, [self._released("1.1.0.0", "v1.1")])
        self.assertEqual([v["version"] for v in updated[0]["versions"]], ["1.1.0.0", "1.0.0.0"])


if __name__ == "__main__":
    unittest.main()
