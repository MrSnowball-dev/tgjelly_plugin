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

    def test_chunked_bodies_are_read(self):
        import http.client
        conn = http.client.HTTPConnection("127.0.0.1", self.port, timeout=5)
        conn.request("POST", f"/bot{TOKEN}/getChat", body=iter([b'{"chat_id":', b'-1001234567890}']),
                     headers={"Content-Type": "application/json; charset=utf-8"}, encode_chunked=True)
        result = json.loads(conn.getresponse().read())["result"]
        conn.close()
        self.assertEqual(-1001234567890, result["id"])

    def test_answers_http_1_1_and_keeps_the_connection_open(self):
        # .NET pools connections; HTTP/1.0 replies that close them made concurrent calls fail in CI.
        import http.client
        conn = http.client.HTTPConnection("127.0.0.1", self.port, timeout=5)
        for _ in range(2):
            conn.request("POST", f"/bot{TOKEN}/getMe", body=b"{}", headers={"Content-Type": "application/json"})
            response = conn.getresponse()
            response.read()
            self.assertEqual(11, response.version)
            self.assertNotEqual("close", (response.getheader("Connection") or "").lower())
        conn.close()

    def test_records_multipart_send_photo(self):
        boundary = "tgjelly-boundary"
        body = (
            f"--{boundary}\r\nContent-Disposition: form-data; name=chat_id\r\n\r\n-1001234567890\r\n"
            f"--{boundary}\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Disposition: form-data; name=caption\r\n\r\n<b>Smoke Movie</b>\r\n"
            f"--{boundary}\r\nContent-Disposition: form-data; name=photo; filename=poster.jpg\r\nContent-Type: application/octet-stream\r\n\r\n"
        ).encode() + b"\xff\xd8\xff\x00binary" + f"\r\n--{boundary}--\r\n".encode()
        req = urllib.request.Request(f"http://127.0.0.1:{self.port}/bot{TOKEN}/sendPhoto", data=body,
                                     headers={"Content-Type": f'multipart/form-data; boundary="{boundary}"'})
        with urllib.request.urlopen(req, timeout=5) as r:
            reply = json.loads(r.read())

        call = next(c for c in self.stub.calls() if c["method"] == "sendPhoto")
        self.assertEqual(-1001234567890, call["body"]["chat_id"])
        self.assertEqual("<b>Smoke Movie</b>", call["body"]["caption"])
        self.assertNotIn("photo", call["body"])
        self.assertTrue(reply["ok"])
        self.assertEqual([{"file_id": "f1", "file_unique_id": "u1", "width": 1, "height": 1}], reply["result"]["photo"])
        self.assertEqual(("<b>Smoke Movie</b>", -1001234567890), (reply["result"]["caption"], reply["result"]["chat"]["id"]))

    def test_messages_filters_by_chat(self):
        self.call("sendMessage", {"chat_id": -1001, "text": "one"})
        self.call("sendMessage", {"chat_id": -1002, "text": "two"})
        self.call("getChat", {"chat_id": -1001})
        self.call("sendPhoto", {"chat_id": -1001, "caption": "three"})

        self.assertEqual([{"chat_id": -1001, "text": "one"}, {"chat_id": -1001, "caption": "three"}], self.stub.messages(-1001))
        self.assertEqual("two", self.call("sendMessage", {"chat_id": -1002, "text": "two"})[1]["result"]["text"])

    def test_delete_message_is_true(self):
        status, body = self.call("deleteMessage", {"chat_id": 5, "message_id": 1})
        self.assertEqual((200, True), (status, body["result"]))

    def test_create_forum_topic_returns_a_thread(self):
        first = self.call("createForumTopic", {"chat_id": -1001, "name": "Jellyfin"})[1]["result"]
        second = self.call("createForumTopic", {"chat_id": -1001, "name": "Other"})[1]["result"]
        self.assertEqual(("Jellyfin", 7322096), (first["name"], first["icon_color"]))
        self.assertEqual(first["message_thread_id"] + 1, second["message_thread_id"])

    def test_pushed_updates_are_returned_once_offset_confirms(self):
        self.stub.push_update({"message": {"text": "a"}})
        self.stub.push_update({"message": {"text": "b"}})
        first = self.call("getUpdates")[1]["result"]
        self.assertEqual(["a", "b"], [u["message"]["text"] for u in first])
        self.assertEqual(first[0]["update_id"] + 1, first[1]["update_id"])
        again = self.call("getUpdates", {"offset": first[1]["update_id"]})[1]["result"]
        self.assertEqual(["b"], [u["message"]["text"] for u in again])
        start = time.monotonic()
        self.assertEqual([], self.call("getUpdates", {"offset": first[1]["update_id"] + 1})[1]["result"])
        self.assertGreaterEqual(time.monotonic() - start, 0.9)

    def test_last_reply_markup_parses_json(self):
        self.assertIsNone(self.stub.last_reply_markup("sendMessage", 5))
        self.call("sendMessage", {"chat_id": 5, "text": "a", "reply_markup": {"keyboard": [[{"text": "x"}]]}})
        self.call("sendMessage", {"chat_id": 5, "text": "b", "reply_markup": json.dumps({"keyboard": [[{"text": "y"}]]})})
        self.assertEqual({"keyboard": [[{"text": "y"}]]}, self.stub.last_reply_markup("sendMessage", 5))

    def test_channel_ids_are_channels(self):
        chat = self.call("getChat", {"chat_id": -1005555555555})[1]["result"]
        member = self.call("getChatMember", {"chat_id": -1005555555555, "user_id": 123456789})[1]["result"]
        self.assertEqual(("channel", "administrator", True), (chat["type"], member["status"], member["can_post_messages"]))


if __name__ == "__main__":
    unittest.main()
