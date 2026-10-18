import json
import os
import sys
import time
import unittest
import urllib.error
import urllib.request

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import botapi_stub  # noqa: E402

TOKEN = "123456789:AAEhBP0av28KrBd9xL4Zq3Wm7Tn5Ys1Cv6QpZ"


class StubTests(unittest.TestCase):
    def setUp(self):
        self.stub = botapi_stub.StubBotApi()
        self.port = self.stub.start(host="127.0.0.1", port=0)
        self.addCleanup(self.stub.stop)

    def call(self, method, body=None):
        req = urllib.request.Request(f"http://127.0.0.1:{self.port}/bot{TOKEN}/{method}",
                                     data=json.dumps(body or {}).encode(), headers={"Content-Type": "application/json"})
        try:
            with urllib.request.urlopen(req, timeout=5) as r:
                return r.status, json.loads(r.read())
        except urllib.error.HTTPError as e:
            return e.code, json.loads(e.read())

    def test_get_me_uses_the_token_prefix_as_the_bot_id(self):
        status, body = self.call("getMe")
        self.assertEqual((200, 123456789, "smoke_bot"), (status, body["result"]["id"], body["result"]["username"]))

    def test_unknown_methods_are_404_with_a_description(self):
        status, body = self.call("sendDice")
        self.assertEqual((404, False, "Not Found"), (status, body["ok"], body["description"]))

    def test_calls_are_recorded_with_their_bodies(self):
        self.call("sendMessage", {"chat_id": -1001234567890, "text": "TgJelly test"})
        self.assertIn({"method": "sendMessage", "body": {"chat_id": -1001234567890, "text": "TgJelly test"}}, self.stub.calls())

    def test_get_updates_returns_empty_after_about_a_second(self):
        start = time.monotonic()
        self.assertEqual([], self.call("getUpdates")[1]["result"])
        self.assertGreaterEqual(time.monotonic() - start, 0.9)

    def test_groups_are_supergroups_with_the_bot_as_admin(self):
        chat = self.call("getChat", {"chat_id": -1001234567890})[1]["result"]
        member = self.call("getChatMember", {"chat_id": -1001234567890, "user_id": 123456789})[1]["result"]
        self.assertEqual(("supergroup", "administrator", True), (chat["type"], member["status"], member["can_post_messages"]))

    def test_send_message_returns_increasing_ids(self):
        first = self.call("sendMessage", {"chat_id": 5, "text": "a"})[1]["result"]["message_id"]
        second = self.call("sendMessage", {"chat_id": 5, "text": "b"})[1]["result"]["message_id"]
        self.assertEqual(first + 1, second)


if __name__ == "__main__":
    unittest.main()
