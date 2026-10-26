"""Tests for build/publish_pages.py. Run: python3 -m unittest discover -s build -p 'test_*.py' -v"""

import json
import tempfile
import unittest
from pathlib import Path

import package
import publish_pages


def write_manifest(path: Path, guid: str = package.GUID) -> None:
    path.write_text(json.dumps([{"guid": guid, "name": "TgJelly", "versions": []}]), encoding="utf-8")


class PublishPagesTests(unittest.TestCase):
    def setUp(self):
        self._tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self._tmp.cleanup)
        self.root = Path(self._tmp.name)
        self.pages = self.root / "pages"
        self.pages.mkdir()
        self.manifest = self.root / "manifest.json"
        self.logo = self.root / "logo.png"
        self.logo.write_bytes(b"png")

    def test_copies_manifest(self):
        write_manifest(self.manifest)
        changed = publish_pages.prepare(self.pages, self.manifest, None)
        self.assertEqual(changed, ["manifest.json"])
        self.assertEqual((self.pages / "manifest.json").read_text(encoding="utf-8"),
                         self.manifest.read_text(encoding="utf-8"))

    def test_logo_added_once(self):
        write_manifest(self.manifest)
        self.assertEqual(publish_pages.prepare(self.pages, self.manifest, self.logo), ["manifest.json", "logo.png"])
        self.logo.write_bytes(b"other")
        publish_pages.prepare(self.pages, self.manifest, self.logo)
        self.assertEqual((self.pages / "logo.png").read_bytes(), b"png")

    def test_missing_logo_is_skipped(self):
        write_manifest(self.manifest)
        self.assertEqual(publish_pages.prepare(self.pages, self.manifest, self.root / "none.png"), ["manifest.json"])

    def test_rejects_manifest_without_guid(self):
        write_manifest(self.manifest, guid="00000000-0000-0000-0000-000000000000")
        with self.assertRaises(ValueError):
            publish_pages.prepare(self.pages, self.manifest, self.logo)
        self.assertFalse((self.pages / "manifest.json").exists())

    def test_refuses_to_drop_existing_versions(self):
        old = [{"guid": package.GUID, "versions": [{"version": "1.0.1.0"}, {"version": "2.0.1.0"}]}]
        new = [{"guid": package.GUID, "versions": [{"version": "2.0.2.0"}]}]
        (self.pages / "manifest.json").write_text(json.dumps(old), encoding="utf-8")
        self.manifest.write_text(json.dumps(new), encoding="utf-8")
        with self.assertRaises(ValueError):
            publish_pages.prepare(self.pages, self.manifest, None)
        self.assertEqual(json.loads((self.pages / "manifest.json").read_text(encoding="utf-8")), old)

    def test_accepts_dropping_pre_release_versions(self):
        url = f"{package.REPO_URL}/releases/download"
        old = [{"guid": package.GUID, "versions": [{"version": "2.0.9.0", "sourceUrl": f"{url}/v0.9/tgjelly_2.0.9.0.zip"}]}]
        new = [{"guid": package.GUID, "versions": [{"version": "1.0.0.0", "sourceUrl": f"{url}/v1.0/tgjelly_1.0.0.0.zip"}]}]
        (self.pages / "manifest.json").write_text(json.dumps(old), encoding="utf-8")
        self.manifest.write_text(json.dumps(new), encoding="utf-8")
        self.assertEqual(publish_pages.prepare(self.pages, self.manifest, None), ["manifest.json"])

    def test_accepts_manifest_that_keeps_existing_versions(self):
        old = [{"guid": package.GUID, "versions": [{"version": "2.0.1.0"}]}]
        new = [{"guid": package.GUID, "versions": [{"version": "2.0.2.0"}, {"version": "2.0.1.0"}]}]
        (self.pages / "manifest.json").write_text(json.dumps(old), encoding="utf-8")
        self.manifest.write_text(json.dumps(new), encoding="utf-8")
        self.assertEqual(publish_pages.prepare(self.pages, self.manifest, None), ["manifest.json"])


if __name__ == "__main__":
    unittest.main()
