#!/usr/bin/env python3
"""Docker smoke test: install a TgJelly zip into a real Jellyfin and check that it loads (spec §14.3).

Usage:
  python3 tests/integration/smoke.py --image jellyfin/jellyfin:12.2 --zip artifacts/tgjelly_2.0.0.0.zip

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
PASSWORD = "smoke-pass-1"


def plugin_folder_name(meta: dict) -> str:
    """Jellyfin's own install layout: plugins/<Name>_<version>/."""
    return f"{meta['name']}_{meta['version']}"


def parse_host_port(docker_port_output: str) -> int:
    """Parse `docker port <c> 8096/tcp` output such as '127.0.0.1:49153' (first line wins)."""
    first = docker_port_output.strip().splitlines()[0]
    return int(first.rsplit(":", 1)[1])


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


def run(image: str, zip_path: Path) -> None:
    with zipfile.ZipFile(zip_path) as archive:
        meta = json.loads(archive.read("meta.json"))
        with tempfile.TemporaryDirectory() as tmp:
            plugin_dir = Path(tmp) / "plugins" / plugin_folder_name(meta)
            plugin_dir.mkdir(parents=True)
            archive.extractall(plugin_dir)

            name = f"tgjelly-smoke-{uuid.uuid4().hex[:8]}"
            try:
                docker("create", "--name", name, "-p", "127.0.0.1::8096", image)  # also pulls the image
                # Copying the "plugins" folder creates /config/plugins/<Name>_<version>/ (data dir /config, see Dockerfile).
                docker("cp", str(Path(tmp) / "plugins"), f"{name}:/config/")
                docker("start", name)
                base = f"http://127.0.0.1:{parse_host_port(docker('port', name, '8096/tcp'))}"
                checks(base, meta)
            except BaseException:
                print("---- container logs (tail) ----")
                print(docker("logs", "--tail", "200", name, check=False))
                raise
            finally:
                docker("rm", "-f", "-v", name, check=False)


def checks(base: str, meta: dict) -> None:
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

    def status_ok():
        code, _, body = http("GET", f"{base}/TgJelly/Status", token=token)
        data = json.loads(body) if code == 200 else {}
        return data.get("Compatible") is True and data.get("InstanceOwner") is True

    wait_until(status_ok, "GET /TgJelly/Status: compatible and owning its data folder", 60)


def main(argv) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--image", required=True, help="e.g. jellyfin/jellyfin:12.2")
    parser.add_argument("--zip", required=True, type=Path, help="plugin zip built by build/package.py")
    args = parser.parse_args(argv)
    run(args.image, args.zip)
    print("smoke test passed")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
