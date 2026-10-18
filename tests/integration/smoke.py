#!/usr/bin/env python3
"""Docker smoke test: install a TgJelly zip into a real Jellyfin and check that it loads (spec §14.3).

Usage:
  python3 tests/integration/smoke.py --image jellyfin/jellyfin:12.2 --zip artifacts/tgjelly_2.0.0.0.zip [--stub]
  python3 tests/integration/smoke.py --image jellyfin/jellyfin:12.2 --zip artifacts/tgjelly_1.0.0.0.zip --expect-incompatible

--stub runs a stand-in Bot API (botapi_stub.py) and checks the token, destination and test-message flow end to end.
--expect-incompatible checks that a 10.11 build loaded by a 12 server reports itself incompatible (spec F8).

Needs docker. Exit code 0 means every check passed. Runs in CI; there is no local Docker requirement.
"""

import argparse
import json
import subprocess
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
PASSWORD = "smoke-pass-1"


def plugin_folder_name(meta: dict) -> str:
    """Jellyfin's own install layout: plugins/<Name>_<version>/."""
    return f"{meta['name']}_{meta['version']}"


def parse_host_port(docker_port_output: str) -> int:
    """Parse `docker port <c> 8096/tcp` output such as '127.0.0.1:49153' (first line wins)."""
    first = docker_port_output.strip().splitlines()[0]
    return int(first.rsplit(":", 1)[1])


def stub_docker_args(port: int) -> list:
    """Lets the container reach the stub on the host, and points TgJelly at it (spec §9.1, §14.3)."""
    return ["--add-host=host.docker.internal:host-gateway", "-e", f"TGJELLY_BOT_API_BASE_URL=http://host.docker.internal:{port}"]


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


def run(image: str, zip_path: Path, use_stub: bool = False, expect_incompatible: bool = False) -> None:
    with zipfile.ZipFile(zip_path) as archive:
        meta = json.loads(archive.read("meta.json"))
        with tempfile.TemporaryDirectory() as tmp:
            plugin_dir = Path(tmp) / "plugins" / plugin_folder_name(meta)
            plugin_dir.mkdir(parents=True)
            archive.extractall(plugin_dir)

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
                docker("start", name)
                base = f"http://127.0.0.1:{parse_host_port(docker('port', name, '8096/tcp'))}"
                token = checks(base, meta, expect_compatible=not expect_incompatible)
                if stub is not None:
                    telegram_checks(base, token, stub)
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
    status, headers, _ = http("GET", f"{base}/web/ConfigurationPage?name=tgjelly.js")
    check(status == 200 and "javascript" in (headers.get("Content-Type") or ""), "page controller is served as JavaScript")

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
    """Connect a bot, add a chat by ID, receive the automatic test message (plan 2's deliverable)."""
    status, _, text = http("POST", f"{base}/TgJelly/Token", {"Token": SMOKE_TOKEN, "SetProfile": True}, token=token, timeout=60)
    check(status == 200 and json.loads(text).get("Outcome") == "Connected", f"POST /TgJelly/Token connects (got {status} {text[:200]})")

    def connected():
        code, _, body = http("GET", f"{base}/TgJelly/Status", token=token)
        return code == 200 and json.loads(body).get("ConnectionState") == "Connected"

    wait_until(connected, "the bot is connected and polling", 60)

    status, _, text = http("GET", f"{base}/TgJelly/Settings", token=token)
    check(status == 200, "GET /TgJelly/Settings")
    revision = json.loads(text)["Revision"]
    request = {"ExpectedRevision": revision, "ChatId": SMOKE_CHAT, "Preset": "AdminAlerts"}
    status, _, text = http("POST", f"{base}/TgJelly/Destinations", request, token=token, timeout=60)
    added = json.loads(text) if status == 200 else {}
    check(added.get("Outcome") == "Added" and added.get("TestSent") is True, f"POST /TgJelly/Destinations adds the chat (got {status} {text[:300]})")
    check(added["Destination"]["ChatId"] == SMOKE_CHAT, "the destination has the chat's ID")

    sent = [c for c in stub.calls() if c["method"] == "sendMessage" and c["body"].get("chat_id") == SMOKE_CHAT]
    check(any("TgJelly test" in c["body"].get("text", "") for c in sent), "the chat received the automatic test message")
    wait_until(lambda: any(c["method"] == "setMyCommands" for c in stub.calls()), "the bot profile was set up", 30)
    check(all(SMOKE_TOKEN not in line for line in docker_logs_lines()), "the bot token never appears in Jellyfin's log")


_CONTAINER = {"name": None}


def docker_logs_lines() -> list:
    name = _CONTAINER["name"]
    return docker("logs", name, check=False).splitlines() if name else []


def main(argv) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--image", required=True, help="e.g. jellyfin/jellyfin:12.2")
    parser.add_argument("--zip", required=True, type=Path, help="plugin zip built by build/package.py")
    parser.add_argument("--stub", action="store_true", help="run the Bot API stub and the Telegram checks")
    parser.add_argument("--expect-incompatible", action="store_true", help="expect CompatGuard to report a mismatch")
    args = parser.parse_args(argv)
    run(args.image, args.zip, use_stub=args.stub, expect_incompatible=args.expect_incompatible)
    print("smoke test passed")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
