#!/usr/bin/env python3
"""A tiny stand-in for the Telegram Bot API, for the Docker smoke test (spec §14.3).

Jellyfin runs with TGJELLY_BOT_API_BASE_URL pointing here; every request is recorded so the smoke test can check
what TgJelly sent. Standard library only.

  python3 tests/integration/botapi_stub.py --port 8081
"""

import argparse
import itertools
import json
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

TRUE_METHODS = {
    "deleteWebhook", "setMyCommands", "deleteMyCommands", "setMyDescription", "setMyShortDescription",
    "setMyDefaultAdministratorRights", "sendChatAction", "leaveChat", "answerCallbackQuery",
}


class StubBotApi:
    def __init__(self):
        self._calls = []
        self._lock = threading.Lock()
        self._ids = itertools.count(1)
        self._server = None

    def start(self, host="0.0.0.0", port=0):
        """Starts serving on a daemon thread; returns the bound port."""
        stub = self

        class Handler(BaseHTTPRequestHandler):
            def do_POST(self):  # noqa: N802 (http.server naming)
                stub._handle(self)

            do_GET = do_POST

            def log_message(self, *args):  # keep CI logs readable
                pass

        self._server = ThreadingHTTPServer((host, port), Handler)
        self._server.daemon_threads = True
        threading.Thread(target=self._server.serve_forever, daemon=True).start()
        return self._server.server_address[1]

    def stop(self):
        if self._server:
            self._server.shutdown()
            self._server.server_close()

    def calls(self):
        with self._lock:
            return list(self._calls)

    def _handle(self, request):
        parts = request.path.split("?")[0].strip("/").split("/")
        if len(parts) != 2 or not parts[0].startswith("bot"):
            return self._reply(request, 404, {"ok": False, "error_code": 404, "description": "Not Found"})
        token, method = parts[0][3:], parts[1]
        raw = self._read_body(request)
        body = {}
        if raw and "json" in (request.headers.get("Content-Type") or ""):
            try:
                body = json.loads(raw)
            except ValueError:
                body = {}
        with self._lock:
            self._calls.append({"method": method, "body": body})

        bot_id = int(token.split(":")[0]) if token.split(":")[0].isdigit() else 0
        result = self._result(method, body, bot_id)
        if result is None:
            return self._reply(request, 404, {"ok": False, "error_code": 404, "description": "Not Found"})
        self._reply(request, 200, {"ok": True, "result": result})

    def _result(self, method, body, bot_id):
        chat_id = body.get("chat_id", 0)
        if method == "getMe":
            return {"id": bot_id, "is_bot": True, "first_name": "Smoke", "username": "smoke_bot"}
        if method == "getWebhookInfo":
            return {"url": "", "has_custom_certificate": False, "pending_update_count": 0}
        if method == "getUpdates":
            time.sleep(1)  # spec §14.3: [] after 1 s, so TgJelly's poll loop doesn't spin
            return []
        if method == "getChat":
            if isinstance(chat_id, int) and chat_id > 0:
                return {"id": chat_id, "type": "private", "first_name": "Smoke user", "accent_color_id": 0, "max_reaction_count": 11}
            return {"id": chat_id, "type": "supergroup", "title": "Smoke group", "accent_color_id": 0, "max_reaction_count": 11}
        if method == "getChatMember":
            return {"status": "administrator", "user": {"id": bot_id, "is_bot": True, "first_name": "Smoke"},
                    "can_be_edited": False, "can_manage_chat": True, "can_change_info": False, "can_post_messages": True,
                    "can_edit_messages": True, "can_delete_messages": False, "can_invite_users": False,
                    "can_restrict_members": False, "can_promote_members": False, "can_manage_video_chats": False,
                    "is_anonymous": False, "can_post_stories": False, "can_edit_stories": False, "can_delete_stories": False}
        if method in ("sendMessage", "sendPhoto", "editMessageText", "editMessageCaption"):
            chat_type = "private" if isinstance(chat_id, int) and chat_id > 0 else "supergroup"
            return {"message_id": next(self._ids), "date": int(time.time()), "chat": {"id": chat_id, "type": chat_type},
                    "text": body.get("text", "")}
        if method in TRUE_METHODS:
            return True
        return None

    @staticmethod
    def _read_body(request):
        # Telegram.Bot sends JSON with chunked transfer encoding (no Content-Length).
        if request.headers.get("Transfer-Encoding", "").lower() == "chunked":
            chunks = []
            while True:
                size = int(request.rfile.readline().split(b";")[0].strip() or b"0", 16)
                if size == 0:
                    request.rfile.readline()
                    break
                chunks.append(request.rfile.read(size))
                request.rfile.readline()
            return b"".join(chunks)
        length = int(request.headers.get("Content-Length") or 0)
        return request.rfile.read(length) if length else b""

    @staticmethod
    def _reply(request, status, payload):
        data = json.dumps(payload).encode("utf-8")
        try:
            request.send_response(status)
            request.send_header("Content-Type", "application/json")
            request.send_header("Content-Length", str(len(data)))
            request.end_headers()
            request.wfile.write(data)
        except (BrokenPipeError, ConnectionResetError):
            pass  # the client gave up, e.g. a long poll cancelled by a session restart


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--port", type=int, default=8081)
    args = parser.parse_args()
    stub = StubBotApi()
    print(f"Bot API stub listening on port {stub.start(port=args.port)}", flush=True)
    try:
        while True:
            time.sleep(3600)
    except KeyboardInterrupt:
        stub.stop()


if __name__ == "__main__":
    main()
