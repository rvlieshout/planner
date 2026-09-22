"""Exercise the shipped Caddyfile with a real Caddy process and a local header-echo API.

Set CADDY_BIN to an installed Caddy 2 binary, then run:
    python -m unittest discover -s tests/deploy -v
"""
import contextlib
import http.client
import http.server
import json
import os
from pathlib import Path
import shutil
import socket
import subprocess
import tempfile
import threading
import time
import unittest

ROOT = Path(__file__).resolve().parents[2]
CADDY = os.environ.get("CADDY_BIN") or shutil.which("caddy")


class Echo(http.server.BaseHTTPRequestHandler):
    def do_POST(self):
        payload = json.dumps(dict(self.headers)).encode()
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(payload)))
        self.end_headers()
        self.wfile.write(payload)

    def log_message(self, *_args):
        pass


@unittest.skipUnless(CADDY, "Set CADDY_BIN to run the real proxy integration checks")
class ProxyHeaders(unittest.TestCase):
    @contextlib.contextmanager
    def proxy(self, trusted_peer):
        config = (ROOT / "deploy/Caddyfile").read_text(encoding="utf-8")
        # Loopback stands in for Coolify's Docker address only in this test. The untrusted case
        # runs the production trust list unchanged, which deliberately excludes loopback.
        if trusted_peer:
            config = config.replace("trusted_proxies static ", "trusted_proxies static 127.0.0.1/32 ")
        config = config.replace("{", "{\n    admin off\n    persist_config off", 1)
        with http.server.ThreadingHTTPServer(("127.0.0.1", 0), Echo) as backend:
            thread = threading.Thread(target=backend.serve_forever, daemon=True)
            thread.start()
            try:
                with socket.socket() as listener:
                    listener.bind(("127.0.0.1", 0))
                    port = listener.getsockname()[1]
                config = config.replace(":80 {", f":{port} {{\n    bind 127.0.0.1")
                config = config.replace("api:8080", f"127.0.0.1:{backend.server_port}")
                with tempfile.TemporaryDirectory(prefix="planner-caddy-") as folder:
                    path = Path(folder) / "Caddyfile"
                    path.write_text(config, encoding="utf-8")
                    with (Path(folder) / "caddy.log").open("w+") as logs:
                        process = subprocess.Popen(
                            [CADDY, "run", "--config", str(path), "--adapter", "caddyfile"],
                            cwd=folder, stdout=logs, stderr=logs,
                            creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0)
                        try:
                            deadline = time.monotonic() + 10
                            while True:
                                if process.poll() is not None or time.monotonic() > deadline:
                                    logs.seek(0)
                                    self.fail("Caddy did not start: " + logs.read())
                                try:
                                    with socket.create_connection(("127.0.0.1", port), timeout=.2):
                                        break
                                except OSError:
                                    time.sleep(.05)
                            yield port
                        finally:
                            process.terminate()
                            process.wait(timeout=10)
            finally:
                backend.shutdown()
                thread.join(timeout=5)

    def request(self, port, forwarded=True):
        headers = {"Host": "planner.test", "Origin": "https://planner.test"}
        if forwarded:
            headers.update({"X-Forwarded-Proto": "https", "X-Forwarded-For": "203.0.113.7"})
        with contextlib.closing(http.client.HTTPConnection("127.0.0.1", port, timeout=5)) as connection:
            connection.request("POST", "/api/v1/me/passkeys/options", body="{}", headers=headers)
            response = connection.getresponse()
            self.assertEqual(response.status, 200)
            return {k.lower(): v for k, v in json.loads(response.read()).items()}

    def test_trusted_tls_proxy_preserves_public_origin(self):
        with self.proxy(trusted_peer=True) as port:
            headers = self.request(port)
            self.assertEqual(headers["x-forwarded-proto"], "https")
            self.assertEqual(headers["host"], "planner.test")
            self.assertEqual(headers["origin"], "https://planner.test")
            self.assertEqual(headers["x-forwarded-for"], "203.0.113.7, 127.0.0.1")

    def test_untrusted_peer_cannot_forge_https_or_client_ip(self):
        with self.proxy(trusted_peer=False) as port:
            headers = self.request(port)
            self.assertEqual(headers["x-forwarded-proto"], "http")
            self.assertEqual(headers["x-forwarded-for"], "127.0.0.1")

    def test_direct_http_stays_http(self):
        with self.proxy(trusted_peer=True) as port:
            self.assertEqual(self.request(port, forwarded=False)["x-forwarded-proto"], "http")


if __name__ == "__main__":
    unittest.main()
