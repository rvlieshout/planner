"""End-to-end check of the MCP server as deployed: through TLS, both proxies and the production images.

Does what a remote MCP client (claude.ai, for one) does: gets challenged, discovers the authorization
server, registers itself, signs a user in through the consent flow, and calls tools, reading and
writing. It also checks what only a production image can show: responses surviving compression,
attachments landing on their volume, and time zones resolving on Linux.

Standard library only. Run against the local replica (see README.md):

    python tests/deploy/production/smoke_mcp.py https://localhost:8443 owner@planner.check planner-check-owner

Everything it creates is deleted again at the end, and the user's time zone is put back. It does leave
one registered OAuth client ("Production smoke check") and the activity-log entries of what it did.
Exits non-zero on the first broken step.
"""
import base64
import gzip
import hashlib
import http.cookiejar
import json
import os
import ssl
import sys
import urllib.error
import urllib.parse
import urllib.request

BASE = sys.argv[1].rstrip("/") if len(sys.argv) > 1 else "https://localhost:8443"
EMAIL = sys.argv[2] if len(sys.argv) > 2 else "owner@planner.check"
PASSWORD = sys.argv[3] if len(sys.argv) > 3 else "planner-check-owner"
RESOURCE = BASE + "/mcp"
REDIRECT = "https://claude.ai/api/mcp/auth_callback"

# The local replica's certificate comes from Caddy's internal CA. Against a real deployment, pass
# --verify to check the certificate as well.
TLS = ssl.create_default_context() if "--verify" in sys.argv else ssl._create_unverified_context()


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, *args, **kwargs):
        return None


jar = http.cookiejar.CookieJar()
opener = urllib.request.build_opener(
    urllib.request.HTTPSHandler(context=TLS), urllib.request.HTTPCookieProcessor(jar), NoRedirect)


def call(method, path, data=None, headers=None, form=False):
    headers = dict(headers or {})
    body = None
    if data is not None:
        if form:
            body = urllib.parse.urlencode(data).encode()
            headers["content-type"] = "application/x-www-form-urlencoded"
        else:
            body = json.dumps(data).encode()
            headers["content-type"] = "application/json"
    url = path if path.startswith("http") else BASE + path
    request = urllib.request.Request(url, data=body, headers=headers, method=method)
    try:
        with opener.open(request, timeout=30) as response:
            status, raw, head = response.status, response.read(), response.headers
    except urllib.error.HTTPError as error:
        status, raw, head = error.code, error.read(), error.headers
    head = {k.lower(): v for k, v in head.items()}
    if head.get("content-encoding") == "gzip":
        raw = gzip.decompress(raw)
    return status, head, raw.decode("utf-8", "replace")


def step(ok, message):
    print(("PASS: " if ok else "FAIL: ") + message, flush=True)
    if not ok:
        sys.exit(1)


def rpc(token, method, params, rpc_id=[0]):
    rpc_id[0] += 1
    status, head, body = call("POST", "/mcp", {"jsonrpc": "2.0", "id": rpc_id[0], "method": method, "params": params}, {
        "accept": "application/json, text/event-stream",
        # Real clients accept compressed responses, and Caddy compresses what it can.
        "accept-encoding": "gzip",
        "mcp-protocol-version": "2025-06-18",
        "authorization": "Bearer " + token})
    if status != 200:
        return status, head, body
    if head.get("content-type", "").startswith("text/event-stream"):
        body = "".join(line[5:].strip() for line in body.splitlines() if line.startswith("data:"))
    return status, head, json.loads(body)


def tool(token, name, arguments):
    status, _, reply = rpc(token, "tools/call", {"name": name, "arguments": arguments})
    step(status == 200 and "result" in reply, f"tools/call {name} answers ({status})")
    result = reply["result"]
    text = "".join(c.get("text", "") for c in result.get("content", []))
    step(not result.get("isError"), f"{name} succeeds" + (f": {text[:160]}" if result.get("isError") else ""))
    return json.loads(text)


# ------------------------------------------------------------------ discovery, as a client meets it
status, head, _ = call("POST", "/mcp", {"jsonrpc": "2.0", "id": 0, "method": "initialize", "params": {}},
                       {"accept": "application/json, text/event-stream"})
challenge = head.get("www-authenticate", "")
step(status == 401 and f'resource_metadata="{BASE}/.well-known/oauth-protected-resource/mcp"' in challenge,
     f"an anonymous call is challenged with the public resource metadata URL ({challenge})")

_, _, body = call("GET", "/.well-known/oauth-protected-resource/mcp")
metadata = json.loads(body)
step(metadata["resource"] == RESOURCE and metadata["authorization_servers"] == [BASE + "/"],
     f"resource metadata names {RESOURCE} and Planner as its authorization server")

_, _, body = call("GET", "/.well-known/oauth-authorization-server")
server = json.loads(body)
step(server["issuer"] == BASE + "/" and server["registration_endpoint"] == BASE + "/connect/register"
     and server["code_challenge_methods_supported"] == ["S256"],
     "authorization server metadata: public issuer, registration endpoint, S256 only")

# ------------------------------------------------------------------ registration and sign-in
status, _, body = call("POST", "/connect/register", {
    "client_name": "Production smoke check", "redirect_uris": [REDIRECT],
    "grant_types": ["authorization_code", "refresh_token"], "response_types": ["code"],
    "token_endpoint_auth_method": "none"})
step(status == 201, f"the client registers itself ({status})")
client_id = json.loads(body)["client_id"]

status, _, body = call("POST", "/connect/token", {"grant_type": "password", "client_id": "planner-web",
                                                  "username": EMAIL, "password": PASSWORD,
                                                  "scope": "openid offline_access planner.api"}, form=True)
step(status == 200, f"the user is signed in to the web client ({status})")
web = json.loads(body)["access_token"]

verifier = base64.urlsafe_b64encode(os.urandom(32)).rstrip(b"=").decode()
challenge = base64.urlsafe_b64encode(hashlib.sha256(verifier.encode()).digest()).rstrip(b"=").decode()
authorize = "/connect/authorize?" + urllib.parse.urlencode({
    "client_id": client_id, "response_type": "code", "redirect_uri": REDIRECT, "scope": "planner.mcp offline_access",
    "code_challenge": challenge, "code_challenge_method": "S256", "resource": RESOURCE, "state": "smoke"})
status, head, _ = call("GET", authorize)
step(status == 302 and head.get("location", "").startswith("/app/authorize?return="),
     "authorize sends the browser to the consent page")

_, _, page = call("GET", head["location"])
step("<html" in page.lower(), "the consent page is served by the web container")

status, _, _ = call("POST", "/connect/authorize/consent", {"clientId": client_id, "allow": True},
                    {"authorization": "Bearer " + web})
secure = any(c.name == "planner.authorize" and c.secure for c in jar)
step(status == 204 and secure, "consent is recorded in a Secure cookie (the public request was HTTPS)")

status, head, _ = call("GET", authorize)
location = head.get("location", "")
step(status == 302 and location.startswith(REDIRECT + "?code="), "authorize returns a code to the client")
code = urllib.parse.parse_qs(urllib.parse.urlparse(location).query)["code"][0]

status, _, body = call("POST", "/connect/token", {"grant_type": "authorization_code", "client_id": client_id, "code": code,
                                                  "redirect_uri": REDIRECT, "code_verifier": verifier,
                                                  "resource": RESOURCE}, form=True)
step(status == 200, f"the code is exchanged for tokens ({status})")
tokens = json.loads(body)
token = tokens["access_token"]
claims = json.loads(base64.urlsafe_b64decode(token.split(".")[1] + "=="))
step(claims["iss"] == BASE + "/" and RESOURCE in json.dumps(claims["aud"]), "the token names the public issuer and the MCP resource")

status, _, _ = call("GET", "/api/v1/teams", headers={"authorization": "Bearer " + token})
step(status == 403, "the MCP token is refused by the REST API")

# ------------------------------------------------------------------ the MCP session
status, head, reply = rpc(token, "initialize", {"protocolVersion": "2025-06-18", "capabilities": {},
                                                "clientInfo": {"name": "smoke", "version": "1"}})
step(status == 200 and reply["result"]["serverInfo"]["name"] == "planner",
     f"initialize succeeds through both proxies ({head.get('content-type')}, encoding {head.get('content-encoding', 'none')})")

_, _, reply = rpc(token, "tools/list", {})
names = {t["name"] for t in reply["result"]["tools"]}
step(len(names) == 18 and {"team_digest", "update_issue", "attach_text"} <= names, f"all {len(names)} tools are listed")

teams = tool(token, "list_teams", {})
team = teams[0]["key"]

# Time zones: the one thing a Windows dev machine cannot show. The user's own setting is put back
# afterwards, since this may be a real account.
original_zone = json.loads(call("GET", "/api/v1/me", headers={"authorization": "Bearer " + web})[2])["timeZone"]
call("PATCH", "/api/v1/me", {"timeZone": "Europe/Amsterdam"}, {"authorization": "Bearer " + web})
try:
    digest = tool(token, "team_digest", {"team": team, "days": 7})
    step(digest["period"]["timeZone"] == "Europe/Amsterdam" and digest["period"]["to"][-6:] in ("+01:00", "+02:00"),
         f"times come back in the user's time zone ({digest['period']['timeZone']}, {digest['period']['to'][-6:]})")
finally:
    call("PATCH", "/api/v1/me", {"timeZone": original_zone}, {"authorization": "Bearer " + web})

# ------------------------------------------------------------------ writes, then cleanup
created = tool(token, "create_issue", {"team": team, "title": "Production smoke check", "allowDuplicate": True})
key = created["issue"]["key"]
step(created["url"] == f"{BASE}/app/issues/{key}", f"the new issue links to the public address ({created['url']})")
try:
    edited = tool(token, "update_issue", {"key": key, "appendToDescription": "Checked by the smoke test."})
    moved = tool(token, "move_issue", {"key": key, "state": "started", "position": "top"})
    step(moved["issue"]["stateType"] == "started", "the issue is edited and moved")

    attached = tool(token, "attach_text", {"issue": key, "fileName": "smoke.md", "content": "# Smoke\n\nÖK 🚀\n"})
    read = tool(token, "read_attachment", {"issue": key, "attachment": "smoke.md"})
    step(read.get("text") == "# Smoke\n\nÖK 🚀\n", "a text file is written to the attachment volume and read back intact")

    document = tool(token, "create_document", {"team": team, "title": "Production smoke check", "content": "Draft"})
    got = tool(token, "get_document", {"document": document["id"]})
    updated = tool(token, "update_document", {"document": document["id"], "content": "Final", "version": got["version"]})
    step(tool(token, "get_document", {"document": document["id"]})["content"] == "Final", "a document is created and rewritten")
finally:
    issue_id = json.loads(call("GET", f"/api/v1/issues/by-key/{key}", headers={"authorization": "Bearer " + web})[2])["id"]
    call("DELETE", f"/api/v1/issues/{issue_id}", headers={"authorization": "Bearer " + web})
    listed = json.loads(call("GET", "/api/v1/documents?search=Production%20smoke%20check",
                             headers={"authorization": "Bearer " + web})[2])
    for item in listed["items"]:
        call("DELETE", f"/api/v1/documents/{item['id']}", headers={"authorization": "Bearer " + web})
    print("cleaned up the smoke issue and document")

print("\nThe MCP server works end to end at " + BASE)
