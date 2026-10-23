"""Tests for the smoke-test helpers. Run: python3 -m unittest discover -s tests/integration -p 'test_*.py' -v"""

import re
import sys
import tempfile
import unittest
from pathlib import Path

import smoke

sys.path.insert(0, str(Path(__file__).resolve().parent.parent.parent / "build"))
import package  # noqa: E402  (build/package.py names the zips)


class HelperTests(unittest.TestCase):
    def test_chat_member_update_comes_from_the_admin(self):
        u = smoke.chat_member_update(smoke.SMOKE_CHAT_3, "channel", "Smoke channel", "left", "administrator", {"can_post_messages": True})
        m = u["my_chat_member"]
        self.assertEqual(m["from"]["id"], smoke.SMOKE_ADMIN)
        self.assertEqual((m["chat"]["id"], m["chat"]["type"]), (smoke.SMOKE_CHAT_3, "channel"))
        self.assertEqual((m["old_chat_member"]["status"], m["new_chat_member"]["status"]), ("left", "administrator"))
        self.assertEqual(m["new_chat_member"]["user"]["id"], int(smoke.SMOKE_TOKEN.split(":")[0]))
        self.assertTrue(m["new_chat_member"]["can_post_messages"])

    def test_url_button_finds_by_url(self):
        markup = {"inline_keyboard": [[{"text": "a", "callback_data": "1|oh"}], [{"text": "b", "url": "https://t.me/x?startchannel&admin=post_messages"}]]}
        self.assertEqual(smoke.url_button(markup, "startchannel")["text"], "b")
        self.assertIsNone(smoke.url_button(markup, "startgroup"))

    def test_plugin_folder_matches_jellyfin_layout(self):
        self.assertEqual(smoke.plugin_folder_name({"name": "TgJelly", "version": "2.0.0.0"}), "TgJelly_2.0.0.0")

    def test_parse_host_port_takes_first_binding(self):
        self.assertEqual(smoke.parse_host_port("127.0.0.1:49153\n"), 49153)
        self.assertEqual(smoke.parse_host_port("127.0.0.1:40001\n[::1]:40002"), 40001)

    def test_stub_docker_args(self):
        self.assertEqual(
            smoke.stub_docker_args(8081),
            ["--add-host=host.docker.internal:host-gateway", "-e", "TGJELLY_BOT_API_BASE_URL=http://host.docker.internal:8081"])

    def test_sample_movie_command(self):
        command = smoke.sample_movie_command()
        self.assertEqual("/usr/lib/jellyfin-ffmpeg/ffmpeg", command[0])
        self.assertIn("lavfi", command[command.index("-f") + 1])
        self.assertEqual("/media/movies/Smoke Movie (2024)/Smoke Movie (2024).mkv", command[-1])

    def test_with_base_prefixes_the_path(self):
        self.assertEqual("/jf/System/Info", smoke.with_base("/jf", "/System/Info"))
        self.assertEqual("/x", smoke.with_base("", "/x"))
        self.assertEqual("/jf", smoke.with_base("/jf/", ""))

    def test_network_xml_sets_base_url(self):
        self.assertIn("<BaseUrl>/jf</BaseUrl>", smoke.network_xml("/jf"))


class WebAssetTests(unittest.TestCase):
    def test_every_registered_asset_is_checked(self):
        plugin = (Path(__file__).resolve().parent.parent.parent / "src" / "Jellyfin.Plugin.TgJelly" / "Plugin.cs").read_text()
        registered = re.findall(r'Name = "([^"]+\.(?:js|css))"', plugin)
        self.assertEqual(sorted(registered), sorted(name for name, _ in smoke.WEB_ASSETS))

    def test_scripts_need_javascript_and_the_stylesheet_css(self):
        self.assertEqual(dict(smoke.WEB_ASSETS), {"tgjelly.js": "javascript", "tgjelly-ui.js": "javascript", "tgjelly-autosave.js": "javascript", "tgjelly-messages.js": "javascript", "tgjelly-preview.js": "javascript", "tgjelly.css": "css"})

    def test_asset_served_checks_status_and_type(self):
        self.assertTrue(smoke.asset_served(200, "text/css; charset=utf-8", "css"))
        self.assertTrue(smoke.asset_served(200, "Application/JavaScript", "javascript"))
        self.assertFalse(smoke.asset_served(200, "application/octet-stream", "css"))
        self.assertFalse(smoke.asset_served(200, "", "javascript"))
        self.assertFalse(smoke.asset_served(404, "text/css", "css"))


class TrackZipTests(unittest.TestCase):
    """The smoke jobs name a track, not a file, so a release run (any X.Y) finds its zips (final review C1)."""

    def _artifacts(self, release):
        temp = tempfile.TemporaryDirectory()
        self.addCleanup(temp.cleanup)
        folder = Path(temp.name)
        for track in (1, 2):
            (folder / f"tgjelly_{package.track_version(track, release)}.zip").write_bytes(b"")
        (folder / "manifest.json").write_text("[]")
        return folder

    def test_finds_each_tracks_zip_for_a_release(self):
        folder = self._artifacts("0.3")
        self.assertEqual(folder / "tgjelly_1.0.3.0.zip", smoke.find_track_zip(folder, 1))
        self.assertEqual(folder / "tgjelly_2.0.3.0.zip", smoke.find_track_zip(folder, 2))

    def test_finds_the_push_build_too(self):
        folder = self._artifacts("0.0")
        self.assertEqual(folder / "tgjelly_1.0.0.0.zip", smoke.find_track_zip(folder, 1))

    def test_track_10_is_not_track_1(self):
        folder = self._artifacts("0.3")
        (folder / "tgjelly_10.0.3.0.zip").write_bytes(b"")
        self.assertEqual(folder / "tgjelly_1.0.3.0.zip", smoke.find_track_zip(folder, 1))

    def test_missing_or_ambiguous_fails(self):
        folder = self._artifacts("0.3")
        with self.assertRaises(SystemExit):
            smoke.find_track_zip(folder, 3)
        (folder / "tgjelly_1.0.4.0.zip").write_bytes(b"")
        with self.assertRaises(SystemExit):
            smoke.find_track_zip(folder, 1)

    def test_command_line_takes_a_track_and_the_artifacts_folder(self):
        folder = self._artifacts("0.3")
        args = smoke.parse_args(["--image", "jellyfin/jellyfin:12.2", "--track", "2", "--artifacts", str(folder), "--stub"])
        self.assertEqual(folder / "tgjelly_2.0.3.0.zip", smoke.resolve_zip(args))
        args = smoke.parse_args(["--image", "x", "--zip", "some.zip"])
        self.assertEqual(Path("some.zip"), smoke.resolve_zip(args))


class AnnouncementTests(unittest.TestCase):
    def test_the_template_announcement_needs_the_title(self):
        self.assertTrue(smoke.announced_with_template([{"text": "TGJ-TPL Smoke Movie"}]))
        self.assertTrue(smoke.announced_with_template([{"caption": "TGJ-TPL Smoke Movie"}]))
        self.assertFalse(smoke.announced_with_template([{"text": "TGJ-TPL "}]))
        self.assertFalse(smoke.announced_with_template([{"text": "Smoke Movie"}]))


if __name__ == "__main__":
    unittest.main()
