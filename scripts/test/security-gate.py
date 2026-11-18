"""Real Identity/Gateway/Driver/Trip security checks on a disposable Compose stack.

No credentials or tokens are written to evidence. Run after init-local-auth.py
and Compose startup. COMPOSE_PROJECT_NAME must identify a dedicated test stack.
"""
import base64
import copy
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request
import uuid

root = Path(__file__).resolve().parents[2]
(root / "plans/reports").mkdir(parents=True, exist_ok=True)
project = os.environ.get("COMPOSE_PROJECT_NAME", "")
if not project.startswith(("ride-test-", "ride-ci-")):
    raise SystemExit("A dedicated ride-test-* or ride-ci-* Compose project is required.")
settings = dict(line.split("=", 1) for line in (root / ".env").read_text().splitlines() if "=" in line and not line.startswith("#"))
checks = []


def request(port, method, path, body=None, token=None, headers=None):
    headers = dict(headers or {})
    if token:
        headers["Authorization"] = "Bearer " + token
    if body is not None:
        headers["Content-Type"] = "application/json"
    payload = json.dumps(body).encode() if body is not None else None
    req = urllib.request.Request(f"http://127.0.0.1:{port}" + path, payload, headers, method=method)
    try:
        response = urllib.request.urlopen(req, timeout=10)
    except urllib.error.HTTPError as error:
        response = error
    with response:
        raw = response.read()
        try:
            value = json.loads(raw)
        except ValueError:
            value = None
        return response.code, value


def expect(name, status, *args, **kwargs):
    actual, value = request(*args, **kwargs)
    assert actual == status, f"{name}: expected {status}, received {actual}"
    checks.append({"case": name, "status": actual})
    return value


def sql(database, query):
    result = subprocess.run(["docker", "compose", "exec", "-T", "postgres", "psql", "-U", "postgres", "-d", database,
                             "-v", "ON_ERROR_STOP=1", "-XAtc", "BEGIN READ ONLY; " + query + "; COMMIT"],
                            cwd=root, capture_output=True, text=True, check=True)
    return result.stdout.removeprefix("BEGIN\n").removesuffix("COMMIT\n").strip()


def account(role):
    username = "gate-" + uuid.uuid4().hex
    password = uuid.uuid4().hex + "!"
    account = expect("register-" + role, 201, 8080, "POST", "/auth/register", {"username": username, "password": password, "role": role})
    grant = expect("login-" + role, 200, 8080, "POST", "/auth/login", {"username": username, "password": password})
    assert grant["userId"] == account["userId"] and grant["expires_in"] <= 900
    expect("wrong-password", 401, 8080, "POST", "/auth/login", {"username": username, "password": password + "wrong"})
    stored = sql("identity_db", "SELECT password_hash FROM identity_users WHERE user_id='" + account["userId"] + "'")
    assert stored and stored != password and password not in stored
    return account["userId"], grant["access_token"]


for port in [8080, 8101, 8102, 8103, 8104, 8105, 8106]:
    for attempt in range(90):
        try:
            if request(port, "GET", "/health/ready")[0] == 200:
                break
        except OSError:
            pass
        time.sleep(1)
    else:
        raise AssertionError(f"Service {port} not ready")

owner_id, owner = account("Driver")
foreign_id, foreign = account("Driver")
rider_id, rider = account("Rider")
_, other_rider = account("Rider")
driver = expect("own-driver-create", 201, 8080, "POST", "/drivers", {"userId": owner_id}, owner)
driver_id = driver["driverId"]
expect("own-driver-read", 200, 8080, "GET", "/drivers/" + driver_id, token=owner)

# Inbox boundary: no client-supplied identity is accepted, and both the
# direct service and the Gateway enforce the authenticated principal.
expect("notification-gateway-missing-token", 401, 8080, "GET", "/notifications/me")
expect("notification-direct-missing-token", 401, 8106, "GET", "/notifications/me")
expect("notification-own-inbox", 200, 8080, "GET", "/notifications/me", token=rider)
expect("notification-other-inbox", 200, 8080, "GET", "/notifications/me", token=other_rider)
expect("notification-forged-user-id", 401, 8106, "GET", "/notifications/me?userId=" + rider_id)


def snapshot():
    # Relay status can progress independently. Compare immutable intent identities
    # and payloads, plus the entire authoritative Driver row.
    return sql("driver_db", "SELECT json_build_object('driver',(SELECT row_to_json(d) FROM drivers d WHERE driver_id='" + driver_id + "'), 'intents',(SELECT coalesce(json_agg(x ORDER BY message_id),'[]') FROM (SELECT message_id,message_type,payload_json FROM outbox_messages WHERE aggregate_id IN ('" + driver_id + "','" + driver_id.replace("-", "") + "')) x))")


def deny(name, status, port, method, path, body=None, token=None, headers=None):
    before = snapshot()
    expect(name, status, port, method, path, body, token, headers)
    assert snapshot() == before, name + ": rejected request changed authoritative state or Outbox intents"


path = "/drivers/" + driver_id + "/online"
body = {"expectedVersion": driver["version"]}
deny("missing-token", 401, 8102, "POST", path, body)
for action, payload in [("offline", body), ("reserve", {"expectedVersion": driver["version"], "tripId": str(uuid.uuid4()), "reservationId": str(uuid.uuid4()), "matchingRound": 1}),
                        ("release", {"tripId": str(uuid.uuid4()), "reservationId": str(uuid.uuid4())}),
                        ("location", {"deviceSessionId": "security", "sequenceNo": 1, "capturedAt": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()), "latitude": 10.7, "longitude": 106.7})]:
    deny("unauthenticated-" + action, 401, 8102, "POST", "/drivers/" + driver_id + "/" + action, payload)
deny("unsigned-spoofed-headers", 401, 8102, "POST", path, body, headers={"X-Driver-Id": driver_id, "X-Role": "Driver"})
deny("malformed-token", 401, 8102, "POST", path, body, "unsigned-spoofed-token")
parts = owner.split(".")
signature = bytearray(base64.urlsafe_b64decode(parts[2] + "=="))
signature[0] ^= 1
invalid = ".".join(parts[:2] + [base64.urlsafe_b64encode(signature).decode().rstrip("=")])
deny("invalid-signature", 401, 8102, "POST", path, body, invalid)

openssl = shutil.which("openssl") or "C:/Program Files/Git/usr/bin/openssl.exe"
claims = json.loads(base64.urlsafe_b64decode(parts[1] + "=="))
with tempfile.TemporaryDirectory(prefix="ride-security-negative-") as directory:
    key = Path(directory) / "key.pem"
    key.write_bytes(base64.b64decode(settings["AUTH_PRIVATE_KEY_BASE64"]))
    for label, updates in [("expired-token", {"nbf": int(time.time()) - 3600, "exp": int(time.time()) - 1800}),
                           ("wrong-issuer", {"iss": "untrusted"}), ("wrong-audience", {"aud": "untrusted"})]:
        changed = copy.deepcopy(claims)
        changed.update(updates)
        encoded = base64.urlsafe_b64encode(json.dumps(changed, separators=(",", ":")).encode()).decode().rstrip("=")
        signed = parts[0] + "." + encoded
        signature = subprocess.run([openssl, "dgst", "-sha256", "-sign", str(key)], input=signed.encode(), capture_output=True, check=True).stdout
        negative = signed + "." + base64.urlsafe_b64encode(signature).decode().rstrip("=")
        deny(label, 401, 8102, "POST", path, body, negative)
        if label == "expired-token":
            expired = negative

deny("wrong-role", 403, 8080, "POST", path, body, rider)
deny("foreign-driver", 403, 8080, "POST", path, body, foreign)
deny("spoofed-body-owner", 403, 8080, "POST", "/drivers", {"userId": foreign_id}, owner)
deny("foreign-read", 403, 8102, "GET", "/drivers/" + driver_id, token=foreign)
for action in ["reserve", "release"]:
    deny("rider-internal-" + action, 403, 8102, "POST", "/drivers/" + driver_id + "/" + action,
         {"tripId": str(uuid.uuid4()), "reservationId": str(uuid.uuid4()), "expectedVersion": 1, "matchingRound": 1}, rider)
    expect("gateway-hides-" + action, 404, 8080, "POST", "/drivers/" + driver_id + "/" + action, {}, owner)
    deny("owner-cannot-use-service-" + action.upper(), 403, 8102, "POST", "/drivers/" + driver_id + "/" + action.upper(), {}, owner)
expect("gateway-hides-service-grants", 404, 8080, "POST", "/connect/token", {})
expect("matching-requires-service", 401, 8104, "POST", "/internal/matching/rounds", {})
expect("matching-denies-rider", 403, 8104, "POST", "/internal/matching/rounds", {}, rider)
expect("cancellation-status-requires-service", 401, 8104, "GET", "/internal/matching/rounds/" + str(uuid.uuid4()) + "/1/cancellation")
expect("cancellation-status-denies-rider", 403, 8104, "GET", "/internal/matching/rounds/" + str(uuid.uuid4()) + "/1/cancellation", token=rider)
expect("bad-service-secret", 401, 8101, "POST", "/connect/token", {"clientId": "matching", "clientSecret": "wrong"})
service = {}
for client in ["matching", "trip"]:
    grant = expect("service-grant-" + client, 200, 8101, "POST", "/connect/token", {"clientId": client, "clientSecret": settings["AUTH_" + client.upper() + "_SECRET"]})
    assert grant["expires_in"] <= 120
    service[client] = grant["access_token"]
deny("trip-cannot-reserve", 403, 8102, "POST", "/drivers/" + driver_id + "/reserve", {}, service["trip"])
deny("matching-cannot-release", 403, 8102, "POST", "/drivers/" + driver_id + "/release", {}, service["matching"])
expect("matching-authorized-read", 200, 8102, "GET", "/drivers/" + driver_id, token=service["matching"])

online = expect("owner-online-positive-control", 200, 8080, "POST", path, body, owner)
expect("owner-offline-positive-control", 200, 8080, "POST", "/drivers/" + driver_id + "/offline", {"expectedVersion": online["version"]}, owner)
trip_body = {"pickup": {"latitude": 10.7769, "longitude": 106.7009}, "dropoff": {"latitude": 10.78, "longitude": 106.71}}
idem = "security-" + uuid.uuid4().hex
trip = expect("rider-create-through-gateway", 201, 8080, "POST", "/trips", trip_body, rider, {"Idempotency-Key": idem})
trip_id = trip["tripId"]
expect("rider-own-trip", 200, 8080, "GET", "/trips/" + trip_id, token=rider)
expect("foreign-trip-not-disclosed", 404, 8080, "GET", "/trips/" + trip_id, token=other_rider)
expect("driver-cannot-create-trip", 403, 8080, "POST", "/trips", trip_body, owner, {"Idempotency-Key": uuid.uuid4().hex})
changed = copy.deepcopy(trip_body)
changed["dropoff"]["latitude"] += .01
expect("idempotency-payload-conflict", 409, 8080, "POST", "/trips", changed, rider, {"Idempotency-Key": idem})
changed["pickup"]["latitude"] = 91
expect("invalid-coordinates", 400, 8080, "POST", "/trips", changed, rider, {"Idempotency-Key": uuid.uuid4().hex})
expect("oversized-request", 413, 8080, "POST", "/trips", {"padding": "x" * 80000}, rider)
if "--identity-tests" in sys.argv:
    fixture_path = root / "TestResults/security-fixture.json"
    fixture_path.parent.mkdir(parents=True, exist_ok=True)
    snapshot_sql = "SELECT row_to_json(d)::text FROM drivers d WHERE driver_id='" + driver_id + "'"
    fixture = {"BaseUrl": "http://127.0.0.1:8102", "ControlPath": "/drivers/" + driver_id,
               "ControlTokenEnvironment": "GATE_OWNER", "DatabaseEnvironment": "GATE_DATABASE",
               "SnapshotSql": snapshot_sql, "Cases": {}}
    for name, token_name in [("missing-token", None), ("invalid-signature", "GATE_INVALID"),
                              ("expired-token", "GATE_EXPIRED"), ("malformed-token", "GATE_MALFORMED"),
                              ("wrong-role", "GATE_RIDER"), ("foreign-object", "GATE_FOREIGN")]:
        fixture["Cases"][name] = {"Method": "POST", "Path": path, "Body": json.dumps(body), "TokenEnvironment": token_name}
    fixture["Cases"]["spoofed-actor"] = {"Method": "POST", "Path": "/drivers", "Body": json.dumps({"userId": foreign_id}), "TokenEnvironment": "GATE_OWNER"}
    environment = dict(os.environ, GATE_OWNER=owner, GATE_INVALID=invalid, GATE_EXPIRED=expired,
                       GATE_MALFORMED="unsigned-spoofed-token", GATE_RIDER=rider, GATE_FOREIGN=foreign)
    connection = "Host=127.0.0.1;Port=5432;Database=driver_db;Username=driver_app;Password=" + settings["DRIVER_DB_PASSWORD"]
    identity_result_dir = "TestResults/identity-gate/" + uuid.uuid4().hex
    command = ["dotnet", "test", "tests/Integration/RideHailing.IdentityService.IntegrationTests", "-c", "Release",
               "--logger", "trx", "--results-directory", identity_result_dir]
    if os.name == "nt":
        fixture["BaseUrl"] = "http://host.docker.internal:8102"
        connection = connection.replace("127.0.0.1", "host.docker.internal")
        environment["RIDE_AUTH_FIXTURE"] = "/work/TestResults/security-fixture.json"
        environment["GATE_DATABASE"] = connection
        command = ["docker", "run", "--rm", "--name", "ride-identity-gate", "-v", str(root).replace("\\", "/") + ":/work", "-w", "/work"] + sum((["-e", key] for key in environment if key.startswith("GATE_") or key == "RIDE_AUTH_FIXTURE"), []) + ["mcr.microsoft.com/dotnet/sdk:10.0@sha256:de4fe90a98845d75910b9769662465c84de1a934665c431279d1382b2bddc02b"] + command
    else:
        environment["RIDE_AUTH_FIXTURE"] = str(fixture_path)
        environment["GATE_DATABASE"] = connection
    fixture_path.write_text(json.dumps(fixture, indent=2))
    completed = subprocess.run(command, cwd=root, env=environment, capture_output=True, text=True)
    (root / "plans/reports/identity-gate.log").write_text(completed.stdout + completed.stderr)
    assert completed.returncode == 0, "Real Identity negative test execution failed; inspect identity-gate.log"
    verified = subprocess.run(
        [sys.executable, str(root / "scripts/test/assert-trx.py"),
         "--results-dir", str(root / identity_result_dir),
         "--min-passed", "7", "--min-files", "1", "--max-skipped", "0",
         "--require-suite", "ridehailing.identityservice.integrationtests:7"],
        cwd=root, capture_output=True, text=True,
    )
    with (root / "plans/reports/identity-gate.log").open("a", encoding="utf-8") as log:
        log.write("\nIdentity TRX verification:\n" + verified.stdout + verified.stderr)
    assert verified.returncode == 0, (
        "Identity test process returned success but its seven actual cases "
        "were not verified in fresh TRX; inspect identity-gate.log"
    )
output = root / "plans/reports/security-gate.json"
output.parent.mkdir(parents=True, exist_ok=True)
source = {key: subprocess.check_output(["git", "rev-parse", expression], cwd=root, text=True).strip()
          for key, expression in [("commit", "HEAD"), ("tree", "HEAD^{tree}")]}
source["status"] = subprocess.check_output(["git", "status", "--porcelain"], cwd=root, text=True).strip()
output.write_text(json.dumps({"project": project, "source": source, "checks": checks, "credentialsRecorded": False}, indent=2) + "\n")
print(f"PASS: {len(checks)} real Identity/Gateway/service checks; rejected Driver requests preserved authority and immutable Outbox intents.")
