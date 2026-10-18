"""Tests for the smoke-test helpers. Run: python3 -m unittest discover -s tests/integration -p 'test_*.py' -v"""

import unittest

import smoke


class HelperTests(unittest.TestCase):
    def test_plugin_folder_matches_jellyfin_layout(self):
        self.assertEqual(smoke.plugin_folder_name({"name": "TgJelly", "version": "2.0.0.0"}), "TgJelly_2.0.0.0")

    def test_parse_host_port_takes_first_binding(self):
        self.assertEqual(smoke.parse_host_port("127.0.0.1:49153\n"), 49153)
        self.assertEqual(smoke.parse_host_port("127.0.0.1:40001\n[::1]:40002"), 40001)

    def test_stub_docker_args(self):
        self.assertEqual(
            smoke.stub_docker_args(8081),
            ["--add-host=host.docker.internal:host-gateway", "-e", "TGJELLY_BOT_API_BASE_URL=http://host.docker.internal:8081"])


if __name__ == "__main__":
    unittest.main()
