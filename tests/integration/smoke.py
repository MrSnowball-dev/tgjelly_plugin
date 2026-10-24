#!/usr/bin/env python3
"""Docker smoke test: install a TgJelly zip into a real Jellyfin and check that it loads (spec §14.3).

Usage:
  python3 tests/integration/smoke.py --image jellyfin/jellyfin:12.2 --track 2 --artifacts artifacts [--stub] [--base-url /jf]
  python3 tests/integration/smoke.py --image jellyfin/jellyfin:12.2 --zip artifacts/tgjelly_1.0.0.0.zip --expect-incompatible

--track N --artifacts DIR picks the one tgjelly_N.*.zip in DIR, whatever release build/package.py packaged.

--stub runs a stand-in Bot API (botapi_stub.py) and checks end to end: the token, destinations and test messages, a new
movie's announcement, and a single server.started message after a restart.
--expect-incompatible checks that a 10.11 build loaded by a 12 server reports itself incompatible (spec F8).

Needs docker. Exit code 0 means every check passed. Runs in CI; there is no local Docker requirement.
"""

import argparse
import json
import subprocess
import urllib.parse
import sys
import tempfile
import time
import urllib.error
import urllib.request
import uuid
import zipfile
from pathlib import Path

CLIENT = 'MediaBrowser Client="tgjelly-smoke", Device="ci", DeviceId="tgjelly-smoke", Version="1.0"'
USER = "smoke"
SMOKE_TOKEN = "123456789:AAEhBP0av28KrBd9xL4Zq3Wm7Tn5Ys1Cv6QpZ"
SMOKE_CHAT = -1001234567890
SMOKE_CHAT_2 = -1009876543210
SMOKE_CHAT_3 = -1005555555555
SMOKE_ADMIN = 777000111
MOVIE_DIR = "/media/movies/Smoke Movie (2024)"
FFMPEG = "/usr/lib/jellyfin-ffmpeg/ffmpeg"
FAST_BATCHING = {"QuietWindowSeconds": 5, "ReleaseQuietSeconds": 5, "MaxHoldMinutes": 1, "ReadyTimeoutMinutes": 1,
                 "DigestThreshold": 10, "LateEditHours": 6}
PASSWORD = "smoke-pass-1"


# Every dashboard asset GetPages() registers besides the page itself, with the content type a browser needs: a module
# script with any other type is refused, and so is a stylesheet (design §8). test_smoke.py pins this to Plugin.cs.
WEB_ASSETS = (
    ("tgjelly.js", "javascript"),
    ("tgjelly-ui.js", "javascript"),
    ("tgjelly-autosave.js", "javascript"),
    ("tgjelly-messages.js", "javascript"),
    ("tgjelly-preview.js", "javascript"),
    ("tgjelly-log.js", "javascript"),
    ("tgjelly.css", "css"),
)


def asset_served(status: int, content_type: str, kind: str) -> bool:
    return status == 200 and kind in (content_type or "").lower()


def plugin_folder_name(meta: dict) -> str:
    """Jellyfin's own install layout: plugins/<Name>_<version>/."""
    return f"{meta['name']}_{meta['version']}"


def parse_host_port(docker_port_output: str) -> int:
    """Parse `docker port <c> 8096/tcp` output such as '127.0.0.1:49153' (first line wins)."""
    first = docker_port_output.strip().splitlines()[0]
    return int(first.rsplit(":", 1)[1])


def with_base(base_url: str, path: str) -> str:
    """Prefix a request path with Jellyfin's BaseUrl ('' or e.g. '/jf')."""
    return base_url.rstrip("/") + path


def network_xml(base_url: str) -> str:
    """The network.xml that gives Jellyfin a BaseUrl; it is read from $JELLYFIN_CONFIG_DIR (/config/config in the image)."""
    return f'<?xml version="1.0" encoding="utf-8"?><NetworkConfiguration><BaseUrl>{base_url}</BaseUrl></NetworkConfiguration>'


def stub_docker_args(port: int) -> list:
    """Lets the container reach the stub on the host, and points TgJelly at it (spec §9.1, §14.3)."""
    return ["--add-host=host.docker.internal:host-gateway", "-e", f"TGJELLY_BOT_API_BASE_URL=http://host.docker.internal:{port}"]


def sample_movie_command() -> list:
    """A 2-second test-pattern movie, made with the ffmpeg that ships in Jellyfin's image."""
    return [FFMPEG, "-loglevel", "error", "-f", "lavfi", "-i", "testsrc=duration=2:size=320x240:rate=5", "-c:v", "mpeg4",
            f"{MOVIE_DIR}/Smoke Movie (2024).mkv"]


def docker(*args: str, check: bool = True) -> str:
    """Run docker; on failure print its stdout and stderr (a bare CalledProcessError would hide them)."""
    result = subprocess.run(["docker", *args], text=True, capture_output=True)
    if check and result.returncode != 0:
        print(f"$ docker {' '.join(args)}\n{result.stdout}{result.stderr}", file=sys.stderr)
        result.check_returncode()
    return result.stdout.strip()


def http(method: str, url: str, body=None, token: str = None, timeout: float = 20):
    """Returns (status, headers, text). Uses the header-based auth that Jellyfin 12 requires (F29)."""
    data = json.dumps(body).encode("utf-8") if body is not None else None
    request = urllib.request.Request(url, data=data, method=method)
    request.add_header("Authorization", CLIENT + (f', Token="{token}"' if token else ""))
    if data is not None:
        request.add_header("Content-Type", "application/json")
    try:
        with urllib.request.urlopen(request, timeout=timeout) as response:
            return response.status, response.headers, response.read().decode("utf-8", "replace")
    except urllib.error.HTTPError as error:
        return error.code, error.headers, error.read().decode("utf-8", "replace")


def check(condition: bool, message: str) -> None:
    if not condition:
        raise AssertionError(message)
    print(f"ok - {message}")


def wait_until(predicate, what: str, timeout: float) -> None:
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        try:
            if predicate():
                print(f"ok - {what}")
                return
        except (OSError, urllib.error.URLError):
            pass
        time.sleep(2)
    raise AssertionError(f"timed out after {timeout:.0f}s waiting for: {what}")


def run(image: str, zip_path: Path, use_stub: bool = False, expect_incompatible: bool = False,
        base_url: str = "") -> None:
    with zipfile.ZipFile(zip_path) as archive:
        meta = json.loads(archive.read("meta.json"))
        with tempfile.TemporaryDirectory() as tmp:
            plugin_dir = Path(tmp) / "plugins" / plugin_folder_name(meta)
            plugin_dir.mkdir(parents=True)
            archive.extractall(plugin_dir)
            if base_url:
                (Path(tmp) / "config").mkdir()
                (Path(tmp) / "config" / "network.xml").write_text(network_xml(base_url), encoding="utf-8")

            name = f"tgjelly-smoke-{uuid.uuid4().hex[:8]}"
            _CONTAINER["name"] = name
            stub = None
            extra = []
            if use_stub:
                import botapi_stub
                stub = botapi_stub.StubBotApi()
                extra = stub_docker_args(stub.start(host="0.0.0.0", port=0))
            try:
                docker("create", "--name", name, "-p", "127.0.0.1::8096", *extra, image)  # also pulls the image
                # Copying the "plugins" folder creates /config/plugins/<Name>_<version>/ (data dir /config, see Dockerfile).
                docker("cp", str(Path(tmp) / "plugins"), f"{name}:/config/")
                if base_url:
                    # The image's config dir is /config/config (JELLYFIN_CONFIG_DIR), so this lands at /config/config/network.xml.
                    docker("cp", str(Path(tmp) / "config"), f"{name}:/config/")
                docker("start", name)
                base = f"http://127.0.0.1:{parse_host_port(docker('port', name, '8096/tcp'))}" + with_base(base_url, "")
                token = checks(base, meta, expect_compatible=not expect_incompatible)
                if stub is not None:
                    telegram_checks(base, token, stub)
                    media_and_restart_checks(base, token, stub, name)
                    onboarding_checks(base, token, stub)
            except BaseException:
                print("---- container logs (tail) ----")
                print(docker("logs", "--tail", "200", name, check=False))
                raise
            finally:
                docker("rm", "-f", "-v", name, check=False)
                if stub is not None:
                    stub.stop()


def checks(base: str, meta: dict, expect_compatible: bool = True) -> str:
    wait_until(lambda: http("GET", f"{base}/health")[2].strip() == "Healthy", "server is healthy", 240)

    # Startup wizard: open to anonymous callers until it completes (same in 10.11 and 12).
    check(http("GET", f"{base}/Startup/Configuration")[0] == 200, "wizard: read configuration")
    status = http("POST", f"{base}/Startup/Configuration",
                  {"UICulture": "en-US", "MetadataCountryCode": "US", "PreferredMetadataLanguage": "en"})[0]
    check(status in (200, 204), "wizard: save configuration")
    check(http("GET", f"{base}/Startup/User")[0] == 200, "wizard: read first user")
    check(http("POST", f"{base}/Startup/User", {"Name": USER, "Password": PASSWORD})[0] in (200, 204), "wizard: set user")
    check(http("POST", f"{base}/Startup/Complete")[0] in (200, 204), "wizard: complete")

    status, _, text = http("POST", f"{base}/Users/AuthenticateByName", {"Username": USER, "Pw": PASSWORD})
    check(status == 200, "login")
    token = json.loads(text)["AccessToken"]

    status, _, text = http("GET", f"{base}/Plugins", token=token)
    check(status == 200, "list plugins")
    plugin = next((p for p in json.loads(text) if p.get("Name") == meta["name"]), None)
    check(plugin is not None, "TgJelly is installed")
    check(plugin.get("Status") == "Active", f"TgJelly is Active (got {plugin.get('Status')})")
    check(plugin.get("Version") == meta["version"], f"TgJelly version is {meta['version']}")

    status, _, html = http("GET", f"{base}/web/ConfigurationPage?name=tgjelly")
    check(status == 200 and 'data-role="page"' in html, "dashboard page is served")
    for name, kind in WEB_ASSETS:
        status, headers, _ = http("GET", f"{base}/web/ConfigurationPage?name={name}")
        content_type = headers.get("Content-Type") or ""
        print(f"info - {name} Content-Type: {content_type}")
        check(asset_served(status, content_type, kind), f"{name} is served with a {kind} content type")

    status, _, text = http("GET", f"{base}/TgJelly/Strings?lang=en", token=token)
    check(status == 200 and "web.connect.title" in json.loads(text), f"GET /TgJelly/Strings has web.connect.title (got {status})")

    if not expect_compatible:
        code, _, body = http("GET", f"{base}/TgJelly/Status", token=token)
        check(code == 200, "GET /TgJelly/Status answers")
        data = json.loads(body)
        check(data.get("Compatible") is False, "a 10.11 build on a 12 server reports itself incompatible (F8)")
        check(data.get("ConnectionState") == "Incompatible", "and keeps the bot off")
        return token

    def status_ok():
        code, _, body = http("GET", f"{base}/TgJelly/Status", token=token)
        data = json.loads(body) if code == 200 else {}
        return data.get("Compatible") is True and data.get("InstanceOwner") is True

    wait_until(status_ok, "GET /TgJelly/Status: compatible and owning its data folder", 60)
    return token


def telegram_checks(base: str, token: str, stub) -> None:
    """Connect a bot, add a NewMedia chat by ID, receive the automatic test message (plan 2's deliverable)."""
    status, _, text = http("POST", f"{base}/TgJelly/Token", {"Token": SMOKE_TOKEN, "SetProfile": True}, token=token, timeout=60)
    check(status == 200 and json.loads(text).get("Outcome") == "Connected", f"POST /TgJelly/Token connects (got {status} {text[:200]})")

    def connected():
        code, _, body = http("GET", f"{base}/TgJelly/Status", token=token)
        return code == 200 and json.loads(body).get("ConnectionState") == "Connected"

    wait_until(connected, "the bot is connected and polling", 60)

    settings = get_settings(base, token)
    update = {"ExpectedRevision": settings["Revision"], "PublicUrl": settings["PublicUrl"], "Message": settings["Message"],
              "Batching": FAST_BATCHING, "UpdateCheck": settings["UpdateCheck"]}
    status, _, text = http("PUT", f"{base}/TgJelly/Settings", update, token=token)
    check(status == 200, f"PUT /TgJelly/Settings shortens the batching windows (got {status} {text[:300]})")

    added = add_destination(base, token, SMOKE_CHAT)
    check(added["Destination"]["ChatId"] == SMOKE_CHAT, "the destination has the chat's ID")
    keep_only_media(base, token, added["Destination"]["Id"])
    check(any("TgJelly test" in m.get("text", "") for m in stub.messages(SMOKE_CHAT)), "the chat received the automatic test message")
    wait_until(lambda: any(c["method"] == "setMyCommands" for c in stub.calls()), "the bot profile was set up", 30)


def get_settings(base: str, token: str) -> dict:
    status, _, text = http("GET", f"{base}/TgJelly/Settings", token=token)
    check(status == 200, "GET /TgJelly/Settings")
    return json.loads(text)


def add_destination(base: str, token: str, chat_id: int) -> dict:
    """Adds a chat; a new destination starts with every event on."""
    request = {"ExpectedRevision": get_settings(base, token)["Revision"], "ChatId": chat_id}
    status, _, text = http("POST", f"{base}/TgJelly/Destinations", request, token=token, timeout=60)
    added = json.loads(text) if status == 200 else {}
    check(added.get("Outcome") == "Added" and added.get("TestSent") is True,
          f"POST /TgJelly/Destinations adds {chat_id} (got {status} {text[:300]})")
    return added


def keep_only_media(base: str, token: str, destination_id: str) -> None:
    """Turns a destination's security and server events off, so it gets media events only."""
    status, _, text = http("GET", f"{base}/TgJelly/Destinations", token=token)
    check(status == 200, "GET /TgJelly/Destinations")
    media = [k for k in json.loads(text)["Catalog"]["EventKeys"] if k.startswith("media.")]
    request = {"ExpectedRevision": get_settings(base, token)["Revision"], "Events": media}
    status, _, text = http("PUT", f"{base}/TgJelly/Destinations/{destination_id}", request, token=token)
    check(status == 200, f"PUT /TgJelly/Destinations/{{id}} keeps media events only (got {status} {text[:300]})")


def add_sample_movie(container: str) -> None:
    docker("exec", container, "mkdir", "-p", MOVIE_DIR)
    docker("exec", container, *sample_movie_command())
    print("ok - a sample movie is in /media/movies")


def create_library(base: str, token: str, container: str) -> None:
    add_sample_movie(container)
    query = urllib.parse.urlencode({"name": "Movies", "collectionType": "movies", "paths": "/media/movies", "refreshLibrary": "true"})
    status, _, text = http("POST", f"{base}/Library/VirtualFolders?{query}", {"LibraryOptions": {"EnableRealtimeMonitor": False}}, token=token, timeout=60)
    check(status in (200, 204), f"POST /Library/VirtualFolders creates the Movies library (got {status} {text[:300]})")
    status, _, text = http("POST", f"{base}/Library/Refresh", token=token, timeout=60)
    check(status in (200, 204), f"POST /Library/Refresh starts a scan (got {status} {text[:300]})")


def find_track_zip(folder: Path, track: int) -> Path:
    """The one zip of a track in folder (tgjelly_<track>.X.Y.0.zip from build/package.py), for any release X.Y."""
    matches = sorted(folder.glob(f"tgjelly_{track}.*.zip"))
    if len(matches) != 1:
        raise SystemExit(f"expected exactly one tgjelly_{track}.*.zip in {folder}, found {[m.name for m in matches]}")
    return matches[0]


def announced_with_template(messages) -> bool:
    """True when a message shows the smoke template rendered with the movie's title."""
    expected = "TGJ-TPL Smoke Movie"
    return any(m.get("text", "").startswith(expected) or m.get("caption", "").startswith(expected) for m in messages)


def media_and_restart_checks(base: str, token: str, stub, container: str) -> None:
    """A new movie reaches the NewMedia chat; after a restart, server.started reaches only the AdminAlerts chat, once."""
    # A custom template proves Scriban.dll loads in the plugin's load context on this server.
    status, _, text = http("GET", f"{base}/TgJelly/Templates", token=token)
    check(status == 200, "GET /TgJelly/Templates")
    status, _, text = http("PUT", f"{base}/TgJelly/Templates/media.movie",
                           {"ExpectedRevision": json.loads(text)["Revision"], "Enabled": True, "Text": "TGJ-TPL {{ title }}"}, token=token)
    check(status == 200, f"PUT /TgJelly/Templates/media.movie (got {status} {text[:300]})")
    create_library(base, token, container)

    wait_until(lambda: announced_with_template(stub.messages(SMOKE_CHAT)),
               "the new movie was announced with the custom template (TGJ-TPL Smoke Movie)", 180)

    add_destination(base, token, SMOKE_CHAT_2)
    status, _, text = http("POST", f"{base}/System/Restart", token=token, timeout=60)
    check(status in (200, 204), f"POST /System/Restart (got {status} {text[:200]})")
    restarted = time.monotonic()
    wait_until(lambda: http("GET", f"{base}/health")[2].strip() == "Healthy", "server is healthy after the restart", 240)

    def connected():
        code, _, body = http("GET", f"{base}/TgJelly/Status", token=token)
        return code == 200 and json.loads(body).get("ConnectionState") == "Connected"

    wait_until(connected, "the bot reconnects after the restart", 120)

    def started_messages(chat_id):
        return [m for m in stub.messages(chat_id) if "Jellyfin is up" in m.get("text", "")]

    wait_until(lambda: started_messages(SMOKE_CHAT_2), "server.started reached the all-events chat",
               max(1.0, 120 - (time.monotonic() - restarted)))
    time.sleep(15)
    check(len(started_messages(SMOKE_CHAT_2)) == 1, f"exactly one server.started message (got {len(started_messages(SMOKE_CHAT_2))})")
    check(not started_messages(SMOKE_CHAT), "the media-only chat got no server.started message")
    check(all(SMOKE_TOKEN not in line for line in docker_logs_lines()), "the bot token never appears in Jellyfin's log (both runs)")


def user_update(**fields) -> dict:
    """A private-chat message update from the smoke admin; fields are merged into the message."""
    user = {"id": SMOKE_ADMIN, "is_bot": False, "first_name": "Smoke"}
    return {"message": {"message_id": int(time.time() * 1000) % 1000000, "date": int(time.time()),
                        "chat": {"id": SMOKE_ADMIN, "type": "private", "first_name": "Smoke"}, "from": user, **fields}}


def url_button(markup, url_part: str):
    """The first inline button whose url contains url_part, or None."""
    rows = (markup or {}).get("inline_keyboard") or []
    return next((b for row in rows for b in row if url_part in b.get("url", "")), None)


def chat_member_update(chat_id: int, chat_type: str, title: str, old_status: str, new_status: str,
                       rights: dict = None) -> dict:
    """A my_chat_member update: the smoke admin changes the bot's status in a chat."""
    admin = {"id": SMOKE_ADMIN, "is_bot": False, "first_name": "Smoke"}
    bot = {"id": int(SMOKE_TOKEN.split(":")[0]), "is_bot": True, "first_name": "TgJelly", "username": "tgjelly_smoke_bot"}
    return {"my_chat_member": {
        "chat": {"id": chat_id, "type": chat_type, "title": title}, "from": admin, "date": int(time.time()),
        "old_chat_member": {"user": bot, "status": old_status},
        "new_chat_member": {"user": bot, "status": new_status, **(rights or {})}}}


def onboarding_checks(base: str, token: str, stub) -> None:
    """The bot's inline onboarding: /start setup, add the bot to a channel (my_chat_member), and the destination exists with every event on."""
    def connected():
        code, _, body = http("GET", f"{base}/TgJelly/Status", token=token)
        return code == 200 and json.loads(body).get("ConnectionState") == "Connected"

    wait_until(connected, "the bot is connected before onboarding", 60)

    stub.push_update(user_update(text="/start setup", entities=[{"offset": 0, "length": 6, "type": "bot_command"}]))

    def channel_prompt():
        return url_button(stub.last_reply_markup("sendMessage", SMOKE_ADMIN), "startchannel")

    wait_until(channel_prompt, "/start setup shows the inline channel prompt", 30)
    check(url_button(stub.last_reply_markup("sendMessage", SMOKE_ADMIN), "startgroup=add") is not None,
          "the prompt also has a startgroup button")
    prompt_id = stub.sent_message_id(SMOKE_ADMIN, "Where should notifications go?")
    check(prompt_id is not None, "the prompt is a message with the expected text")

    rights = {"can_be_edited": False, "can_manage_chat": True, "can_change_info": False, "can_post_messages": True,
              "can_edit_messages": True, "can_delete_messages": False, "can_invite_users": False,
              "can_restrict_members": False, "can_promote_members": False, "can_manage_video_chats": False,
              "is_anonymous": False, "can_post_stories": False, "can_edit_stories": False, "can_delete_stories": False}
    stub.push_update(chat_member_update(SMOKE_CHAT_3, "channel", "Smoke channel", "left", "administrator", rights))

    def destination_added():
        code, _, body = http("GET", f"{base}/TgJelly/Destinations", token=token)
        if code != 200:
            return False
        items = json.loads(body)
        items = items.get("Items", []) if isinstance(items, dict) else items
        return any(d.get("ChatId") == SMOKE_CHAT_3 and any(e.startswith("server.") for e in d.get("Events", [])) for d in items)

    wait_until(destination_added, "the picked channel is a destination with every event on", 30)
    wait_until(lambda: any("TgJelly test" in m.get("text", "") for m in stub.messages(SMOKE_CHAT_3)),
               "the channel received the test message", 30)
    wait_until(lambda: any(c["method"] == "editMessageText" and c["body"].get("chat_id") == SMOKE_ADMIN
                           and "✅ Done" in c["body"].get("text", "") for c in stub.calls()),
               "the wizard was edited to Done", 30)


_CONTAINER = {"name": None}


def docker_logs_lines() -> list:
    name = _CONTAINER["name"]
    return docker("logs", name, check=False).splitlines() if name else []


def parse_args(argv) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--image", required=True, help="e.g. jellyfin/jellyfin:12.2")
    which = parser.add_mutually_exclusive_group(required=True)
    which.add_argument("--zip", type=Path, help="plugin zip built by build/package.py")
    which.add_argument("--track", type=int, choices=(1, 2), help="test this track's zip from --artifacts")
    parser.add_argument("--artifacts", type=Path, default=Path("artifacts"), help="folder with the zips (for --track)")
    parser.add_argument("--stub", action="store_true", help="run the Bot API stub and the Telegram checks")
    parser.add_argument("--base-url", default="", help="run Jellyfin under this BaseUrl, e.g. /jf")
    parser.add_argument("--expect-incompatible", action="store_true", help="expect CompatGuard to report a mismatch")
    return parser.parse_args(argv)


def resolve_zip(args: argparse.Namespace) -> Path:
    return args.zip if args.zip is not None else find_track_zip(args.artifacts, args.track)


def main(argv) -> int:
    args = parse_args(argv)
    zip_path = resolve_zip(args)
    print(f"testing {zip_path}")
    run(args.image, zip_path, use_stub=args.stub, expect_incompatible=args.expect_incompatible, base_url=args.base_url)
    print("smoke test passed")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
