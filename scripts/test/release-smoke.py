"""Run the Production Compose overlay as a real deployment, not just a render.

Exercises: fail-closed secret rendering, an isolated Compose project, external
Trip EF migrations on an empty database, Production environment isolation (no
Payment simulator routes), real Trip messaging through RabbitMQ, a completed
ride and a cancellation/void compensation, independent authority reads, and
teardown that preserves unrelated Docker resources.
"""
import json
import os
import re
import secrets
import shutil
import subprocess
import sys
import time
import urllib.error
import urllib.request
import uuid
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
ENV_PATH = ROOT / ".env"
REPORT = ROOT / "plans/reports/release-smoke.json"
LOG = ROOT / "plans/reports/release-smoke.log"
COMPOSE_FILES = ["-f", "compose.yaml", "-f", "deploy/compose/compose.release.yaml"]
DB_SECRETS = ["POSTGRES_PASSWORD", "IDENTITY_DB_PASSWORD", "DRIVER_DB_PASSWORD",
              "TRIP_DB_PASSWORD", "MATCHING_DB_PASSWORD", "PAYMENT_DB_PASSWORD",
              "NOTIFICATION_DB_PASSWORD", "RABBITMQ_PASSWORD"]
AUTH_SECRETS = ["AUTH_PUBLIC_KEY_BASE64", "AUTH_PRIVATE_KEY_BASE64",
                "AUTH_MATCHING_SECRET", "AUTH_TRIP_SECRET"]
REQUIRED = DB_SECRETS + AUTH_SECRETS
SERVICES = ["gateway", "identity", "driver", "trip", "matching", "payment", "notification"]
HTTP_PORTS = {"gateway": 8080, "identity": 8101, "driver": 8102, "trip": 8103,
              "matching": 8104, "payment": 8105}
project = os.environ.get("COMPOSE_PROJECT_NAME") or f"ride-release-smoke-{os.getpid()}"
if not re.fullmatch(r"ride-release-[a-z0-9-]+", project):
    raise SystemExit("COMPOSE_PROJECT_NAME must start with 'ride-release-'.")
base_env = dict(os.environ, COMPOSE_PROJECT_NAME=project)
report = {"project": project, "status": "FAILED", "checks": [], "limitations": []}


def log(message):
    LOG.parent.mkdir(parents=True, exist_ok=True)
    with LOG.open("a", encoding="utf-8") as handle:
        handle.write(message + "\n")
    print(message)


def compose(*args, check=True, env=None, extra=()):
    command = ["docker", "compose", *extra, *COMPOSE_FILES, *args]
    return subprocess.run(command, cwd=ROOT, env=env or base_env,
                          check=check, text=True, capture_output=True)


def check(identifier, condition, detail=""):
    assert condition, f"{identifier} violated{': ' + detail if detail else ''}"
    report["checks"].append({"id": identifier, "status": "PASS"})


def read_env():
    values = {}
    for line in ENV_PATH.read_text(encoding="utf-8").splitlines():
        if "=" in line and not line.lstrip().startswith("#"):
            key, value = line.split("=", 1)
            values[key] = value
    return values


def write_disposable_env():
    created = not ENV_PATH.exists()
    if created:
        ENV_PATH.write_text((ROOT / ".env.example").read_text(encoding="utf-8"),
                            encoding="utf-8")
    values = read_env()
    disposable = any(values.get(key, "").endswith("_dev_pw") or
                     values.get(key) == "ride_local_cookie_change_me"
                     for key in DB_SECRETS + ["RABBITMQ_ERLANG_COOKIE"])
    if disposable and not created and os.environ.get("RELEASE_SMOKE_REPLACE_DEV_ENV") != "1":
        raise SystemExit(
            "Refusing to run the release smoke with development placeholders; "
            "set RELEASE_SMOKE_REPLACE_DEV_ENV=1 to replace this ignored .env.")
    if created or disposable:
        replacements = {key: secrets.token_urlsafe(24)
                        for key in DB_SECRETS + ["RABBITMQ_ERLANG_COOKIE"]}
        lines = []
        for line in ENV_PATH.read_text(encoding="utf-8").splitlines():
            key = line.split("=", 1)[0] if "=" in line else ""
            if key in replacements:
                line = key + "=" + replacements[key]
            lines.append(line)
        ENV_PATH.write_text("\n".join(lines) + "\n", encoding="utf-8")
        report["disposableSecrets"] = True
    subprocess.run([sys.executable, str(ROOT / "scripts/security/init-local-auth.py")],
                   cwd=ROOT, check=True, capture_output=True, text=True)
    values = read_env()
    missing = [key for key in REQUIRED if not values.get(key)]
    assert not missing, "missing release secrets: " + ", ".join(missing)
    weak = [key for key in DB_SECRETS + ["RABBITMQ_ERLANG_COOKIE"]
            if values[key].endswith("_dev_pw") or
            values[key] == "ride_local_cookie_change_me"]
    assert not weak, "release smoke still uses development placeholders: " + ", ".join(weak)
    return values


def render_with(env, extra, expected_failure=None):
    result = compose("config", "--quiet", check=False, env=env, extra=extra)
    if expected_failure:
        assert result.returncode != 0, f"release overlay rendered without {expected_failure}"
        output = result.stderr + result.stdout
        assert expected_failure in output, output
        report["checks"].append({"id": "RELEASE-SECRETS-" + expected_failure, "status": "PASS"})
    else:
        assert result.returncode == 0, result.stderr
        report["checks"].append({"id": "RELEASE-RENDER", "status": "PASS"})


def wait_http(port, path="/health/ready", predicate=lambda data: True, seconds=180):
    deadline = time.monotonic() + seconds
    last = None
    while time.monotonic() < deadline:
        try:
            with urllib.request.urlopen(f"http://127.0.0.1:{port}{path}", timeout=3) as response:
                data = json.load(response)
                if predicate(data):
                    return data
        except (OSError, ValueError) as exc:
            last = exc
        time.sleep(1)
    raise AssertionError(f"Timed out: http://127.0.0.1:{port}{path} ({last})")


def http(method, path, body=None, token=None, headers=None, port=8080):
    merged = dict(headers or {})
    if token:
        merged["Authorization"] = "Bearer " + token
    if body is not None:
        merged["Content-Type"] = "application/json"
    request = urllib.request.Request(f"http://127.0.0.1:{port}{path}",
                                     json.dumps(body).encode() if body is not None else None,
                                     merged, method=method)
    with urllib.request.urlopen(request, timeout=15) as response:
        raw = response.read()
        return json.loads(raw) if raw else None


def http_status(method, path, port=8080):
    request = urllib.request.Request(f"http://127.0.0.1:{port}{path}", method=method)
    try:
        with urllib.request.urlopen(request, timeout=10) as response:
            return response.status
    except urllib.error.HTTPError as error:
        return error.code


def rows(database, query):
    result = subprocess.run(
        ["docker", "compose", *COMPOSE_FILES, "exec", "-T", "postgres", "psql",
         "-U", "postgres", "-d", database, "-v", "ON_ERROR_STOP=1", "-XAtc",
         "BEGIN READ ONLY; SELECT coalesce(json_agg(x),'[]') FROM (" + query + ") x; COMMIT"],
        cwd=ROOT, env=base_env, capture_output=True, text=True, check=True)
    return json.loads(result.stdout.removeprefix("BEGIN\n").removesuffix("COMMIT\n").strip())


def wait(label, action, predicate, seconds=120):
    deadline = time.monotonic() + seconds
    value = None
    while time.monotonic() < deadline:
        value = action()
        if predicate(value):
            return value
        time.sleep(.5)
    raise AssertionError("Timed out waiting for " + label + ": " + json.dumps(value))


def account(role):
    username = "release-" + uuid.uuid4().hex
    password = uuid.uuid4().hex + "!"
    identity = http("POST", "/auth/register",
                    {"username": username, "password": password, "role": role})
    token = http("POST", "/auth/login",
                 {"username": username, "password": password})["access_token"]
    return identity["userId"], token


def container_state(service):
    container_id = compose("ps", "-q", service).stdout.strip()
    if not container_id:
        return None
    result = subprocess.run(["docker", "inspect", container_id], check=True,
                            text=True, capture_output=True)
    return json.loads(result.stdout)[0]["State"]


def migrate(values):
    def connection(host):
        return ("Host=" + host + ";Port=5432;Database=trip_db;Username=trip_app;Password="
                + values["TRIP_DB_PASSWORD"])
    ef = ["ef", "database", "update",
          "--project", "src/Services/Trip/RideHailing.TripService",
          "--startup-project", "src/Services/Trip/RideHailing.TripService",
          "-c", "TripDbContext", "--configuration", "Release"]
    if shutil.which("dotnet"):
        environment = dict(base_env, ConnectionStrings__Postgres=connection("127.0.0.1"),
                           Auth__PublicKeyBase64=values["AUTH_PUBLIC_KEY_BASE64"])
        subprocess.run(["dotnet", "tool", "restore"], cwd=ROOT, check=True,
                       text=True, capture_output=True)
        result = subprocess.run(["dotnet", *ef], cwd=ROOT, env=environment,
                                text=True, capture_output=True)
    else:
        mount = str(ROOT).replace("\\", "/")
        environment = dict(base_env, ConnectionStrings__Postgres=connection("postgres"),
                           Auth__PublicKeyBase64=values["AUTH_PUBLIC_KEY_BASE64"])
        command = ("dotnet tool restore && dotnet " + " ".join(ef))
        result = subprocess.run(
            ["docker", "run", "--rm", "--network", project + "_ride-net",
             "-v", mount + ":/work", "-v", "ride-release-nuget:/tmp/nuget",
             "-w", "/work", "-e", "NUGET_PACKAGES=/tmp/nuget",
             "-e", "ConnectionStrings__Postgres", "-e", "Auth__PublicKeyBase64",
             "mcr.microsoft.com/dotnet/sdk:10.0", "bash", "-c", command],
            cwd=ROOT, env=environment, text=True, capture_output=True)
    if result.returncode != 0:
        raise RuntimeError("Trip migration failed:\n" + (result.stdout + result.stderr)[-6000:])
    applied = rows("trip_db", "SELECT count(*)::int AS count FROM \"__EFMigrationsHistory\"")
    assert applied[0]["count"] >= 7, applied
    report["migration"] = {"api": "dotnet ef database update", "applied": applied[0]["count"]}


def harness(command, trip, driver, reservation):
    assembly = (ROOT / "scripts/test/w10-g3-harness/bin/Release/net10.0/"
                "RideHailing.W10Harness.dll")
    assert assembly.exists(), "build the W10 harness in Release before the release smoke"
    environment = dict(base_env, TRIP_DB=(
        "Host=postgres;Database=trip_db;Username=trip_app;Password="
        + read_env()["TRIP_DB_PASSWORD"]))
    result = subprocess.run(
        ["docker", "run", "--rm", "--network", project + "_ride-net",
         "-v", str(ROOT).replace("\\", "/") + ":/work:ro", "-w", "/work",
         "-e", "TRIP_DB", "mcr.microsoft.com/dotnet/sdk:10.0", "dotnet",
         "scripts/test/w10-g3-harness/bin/Release/net10.0/RideHailing.W10Harness.dll",
         command, trip, driver, reservation],
        cwd=ROOT, env=environment, capture_output=True, text=True)
    assert result.returncode == 0, result.stdout + result.stderr
    value = json.loads(result.stdout.strip().splitlines()[-1])
    assert value["ok"], value
    return value


def redis_projection(driver_id):
    result = compose("exec", "-T", "redis", "redis-cli", "HGETALL",
                     "driver:projection:" + driver_id.replace("-", ""))
    return result.stdout


def topology():
    values = read_env()
    import base64
    credential = base64.b64encode(
        (values.get("RABBITMQ_USER", "ride_app") + ":" + values["RABBITMQ_PASSWORD"]).encode()).decode()

    def api(path):
        request = urllib.request.Request("http://127.0.0.1:15672/api/" + path,
                                         headers={"Authorization": "Basic " + credential})
        with urllib.request.urlopen(request, timeout=10) as response:
            return json.load(response)

    exchanges = {item["name"] for item in api("exchanges/%2F")}
    assert {"ride.commands", "ride.events", "ride.deadletter", "ride.quarantine"} <= exchanges
    queues = {item["name"] for item in api("queues/%2F")}
    assert {"matching.ride-requested.q", "trip.payment-outcome.q", "trip.driver-state.q",
            "payment.commands.q", "driver.commands.q"} <= queues
    report["topology"] = {"exchanges": len(exchanges), "queues": len(queues)}


def main():
    LOG.parent.mkdir(parents=True, exist_ok=True)
    LOG.write_text("", encoding="utf-8")
    values = write_disposable_env()
    empty = ROOT / "plans/reports/release-empty.env"
    empty.parent.mkdir(parents=True, exist_ok=True)
    empty.write_text("", encoding="utf-8")
    clean = {key: value for key, value in os.environ.items()
             if key not in REQUIRED and key != "COMPOSE_PROJECT_NAME"}
    render_with(clean, ["--env-file", str(empty)],
                "must be set for the release overlay")
    db_only = dict(clean, **{key: values[key] for key in DB_SECRETS})
    render_with(db_only, ["--env-file", str(empty)], "AUTH_")
    render_with(dict(os.environ, COMPOSE_PROJECT_NAME=project), ["--env-file", str(ENV_PATH)])
    try:
        compose("up", "-d", "--build", "postgres", "redis", "rabbitmq")
        for service in ["postgres", "redis", "rabbitmq"]:
            wait(service + " healthy",
                 lambda service=service: container_state(service),
                 lambda state: state and state.get("Health", {}).get("Status") == "healthy")
        migrate(values)
        compose("up", "-d", "--build", *SERVICES)
        for service, port in HTTP_PORTS.items():
            wait_http(port)
            info = wait_http(port, "/info")
            assert info.get("environment") == "Production", (service, info)
        for service in ["trip", "payment"]:
            configured = compose("exec", "-T", service, "printenv",
                                 "TripMessaging__Enabled" if service == "trip"
                                 else "ASPNETCORE_ENVIRONMENT").stdout.strip()
            assert configured == ("true" if service == "trip" else "Production"), configured
        assert compose("exec", "-T", "trip", "printenv",
                       "TripDatabase__AutoMigrate").stdout.strip() == "false"
        check("PRODUCTION-ENVIRONMENT", True)
        check("MESSAGING-ENABLED", True)
        for path in ["/simulator/operations",
                     "/simulator/operations/" + str(uuid.uuid4())]:
            assert http_status("POST" if path == "/simulator/operations" else "GET",
                               path, 8105) == 404, path
        check("NO-PRODUCTION-SIMULATOR", True)
        topology()
        stack_checks = container_state("gateway")
        check("RELEASE-SERVICES-READY", stack_checks is not None)

        user, driver_token = account("Driver")
        driver = http("POST", "/drivers", {"userId": user}, driver_token)
        driver_id = driver["driverId"]
        online = http("POST", "/drivers/" + driver_id + "/online",
                      {"expectedVersion": driver["version"]}, driver_token)
        http("POST", "/drivers/" + driver_id + "/location", {
            "deviceSessionId": uuid.uuid4().hex, "sequenceNo": 1,
            "capturedAt": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
            "latitude": 10.7769, "longitude": 106.7009}, driver_token)
        try:
            wait("Matching location projection", lambda: redis_projection(driver_id),
                 lambda value: "AVAILABLE" in value and "latitude" in value)
            check("GATEWAY-AUTH", True)
            _, rider_token = account("Rider")
            assert http_status("GET", "/trips/" + str(uuid.uuid4())) == 401
            check("UNAUTHENTICATED-REJECTED", True)
            journeys = {}

            def start_trip(label):
                _, token = account("Rider")
                created = http("POST", "/trips", {
                    "pickup": {"latitude": 10.7769, "longitude": 106.7009},
                    "dropoff": {"latitude": 10.78, "longitude": 106.71}},
                    token, {"Idempotency-Key": uuid.uuid4().hex})
                trip_id = created["tripId"]
                state = lambda: rows("trip_db", "SELECT * FROM trips WHERE trip_id='" + trip_id + "'")
                assigned = wait(label + " durable reservation", state,
                                lambda value: value and value[0]["state"] == "DRIVER_RESERVED")[0]
                reservation = assigned["reservation_id"]
                driver_truth = rows("driver_db", "SELECT * FROM drivers WHERE driver_id='" + driver_id + "'")[0]
                round_truth = wait(label + " matching round",
                                   lambda: rows("matching_db", "SELECT * FROM matching_rounds WHERE trip_id='" + trip_id + "'"),
                                   lambda value: value and value[0]["status"] == "RESERVED")[0]
                attempt = rows("matching_db", "SELECT * FROM matching_attempts WHERE trip_id='" + trip_id + "' AND state='RESERVED'")
                assert driver_truth["status"] == "RESERVED"
                assert driver_truth["reserved_trip_id"] == trip_id
                assert driver_truth["reservation_id"] == reservation
                assert round_truth["selected_driver_id"] == driver_id
                assert round_truth["selected_reservation_id"] == reservation
                assert len(attempt) == 1 and attempt[0]["reservation_id"] == reservation
                intent = rows("trip_db", "SELECT message_type,status FROM outbox_messages WHERE aggregate_id='" + trip_id + "' AND message_type='RideRequestedV1'")
                assert len(intent) == 1 and intent[0]["status"] == "PUBLISHED"
                return token, trip_id, reservation, {"trip": assigned, "driver": driver_truth,
                                                     "round": round_truth, "attempt": attempt[0]}

            token, trip_id, reservation, proof = start_trip("completed")
            harness("accept", trip_id, driver_id, reservation)
            wait("authorization", lambda: rows("trip_db", "SELECT * FROM trips WHERE trip_id='" + trip_id + "'"),
                 lambda value: value[0]["state"] == "READY_TO_START")
            harness("start", trip_id, driver_id, reservation)
            wait("driver start", lambda: rows("driver_db", "SELECT * FROM drivers WHERE driver_id='" + driver_id + "'"),
                 lambda value: value[0]["status"] == "ON_TRIP")
            harness("complete", trip_id, driver_id, reservation)
            wait("completed trip", lambda: rows("trip_db", "SELECT * FROM trips WHERE trip_id='" + trip_id + "'"),
                 lambda value: value[0]["state"] == "COMPLETED")
            wait("completed saga", lambda: rows("trip_db", "SELECT * FROM ride_sagas WHERE trip_id='" + trip_id + "'"),
                 lambda value: value and value[0]["status"] == "COMPLETED")
            payment = wait("completed payment",
                           lambda: rows("payment_db", "SELECT * FROM payment_operations WHERE trip_id='" + trip_id + "'"),
                           lambda value: len(value) == 2 and all(item["state"] == "SUCCEEDED" for item in value))
            assert {item["operation_type"] for item in payment} == {"AUTHORIZE", "CAPTURE"}
            driver_after = wait("completed driver release",
                                lambda: rows("driver_db", "SELECT * FROM drivers WHERE driver_id='" + driver_id + "'"),
                                lambda value: value[0]["status"] == "AVAILABLE" and value[0]["reservation_id"] is None)[0]
            journeys["completed"] = {"trip": trip_id, "reservation": reservation,
                                     "state": "COMPLETED", "payment": len(payment), "proof": proof}
            check("COMPLETED-JOURNEY", True)

            token, trip_id, reservation, proof = start_trip("cancelled")
            # AUD-05 frozen policy: cancel after the authorization succeeded.
            # READY_TO_START cancellation must release the exact reservation,
            # void the hold with the stable void key, and compensate the Saga.
            harness("accept", trip_id, driver_id, reservation)
            wait("authorization before cancellation",
                 lambda: rows("trip_db", "SELECT * FROM trips WHERE trip_id='" + trip_id + "'"),
                 lambda value: value[0]["state"] == "READY_TO_START")
            http("POST", "/trips/" + trip_id + "/cancel", {}, token)
            wait("cancelled saga", lambda: rows("trip_db", "SELECT * FROM ride_sagas WHERE trip_id='" + trip_id + "'"),
                 lambda value: value and value[0]["status"] == "COMPENSATED")
            cancelled = rows("trip_db", "SELECT * FROM trips WHERE trip_id='" + trip_id + "'")[0]
            assert cancelled["state"] == "CANCELLED"
            compensation = wait("cancellation payment",
                                lambda: rows("payment_db", "SELECT * FROM payment_operations WHERE trip_id='" + trip_id + "'"),
                                lambda value: len(value) == 2 and all(item["state"] == "SUCCEEDED" for item in value))
            assert {item["operation_type"] for item in compensation} == {"AUTHORIZE", "VOID"}
            wait("cancelled driver release",
                 lambda: rows("driver_db", "SELECT * FROM drivers WHERE driver_id='" + driver_id + "'"),
                 lambda value: value[0]["status"] == "AVAILABLE" and value[0]["reservation_id"] is None and value[0]["active_trip_id"] is None)
            # A RESERVED round is the historical terminal record; the exact
            # reservation release is proven by the Driver authority above,
            # and the round must never fall back into PROCESSING.
            wait("matching round terminal",
                 lambda: rows("matching_db", "SELECT * FROM matching_rounds WHERE trip_id='" + trip_id + "'"),
                 lambda value: value[0]["status"] in ("RESERVED", "CANCELLED") and
                 value[0]["selected_reservation_id"] == reservation)
            journeys["cancelled"] = {"trip": trip_id, "reservation": reservation,
                                     "state": "CANCELLED", "compensation": "VOID",
                                     "payment": len(compensation)}
            check("CANCELLED-VOID-JOURNEY", True)

            for database in ["trip_db", "driver_db", "matching_db", "payment_db"]:
                wait("outbox drain " + database,
                     lambda database=database: rows(database, "SELECT message_id FROM outbox_messages WHERE status <> 'PUBLISHED'"),
                     lambda value: not value)
            wait("cleanup completed", lambda: rows("trip_db", "SELECT key FROM trip_cleanup_intents WHERE completed_at IS NULL"),
                 lambda value: not value)
            wait("no processing matching rounds",
                 lambda: rows("matching_db", "SELECT trip_id FROM matching_rounds WHERE status='PROCESSING'"),
                 lambda value: not value)
            authorities = {
                "trips": rows("trip_db", "SELECT * FROM trips"),
                "drivers": rows("driver_db", "SELECT * FROM drivers"),
                "rounds": rows("matching_db", "SELECT * FROM matching_rounds"),
                "payments": rows("payment_db", "SELECT * FROM payment_operations"),
                "effects": rows("payment_db", "SELECT * FROM provider_effects"),
                "sagas": rows("trip_db", "SELECT * FROM ride_sagas"),
            }
            assert {item["state"] for item in authorities["trips"]} == {"COMPLETED", "CANCELLED"}
            assert all(item["status"] == "AVAILABLE" and item["reservation_id"] is None and
                       item["reserved_trip_id"] is None and item["active_trip_id"] is None
                       for item in authorities["drivers"])
            assert all(item["status"] in ("RESERVED", "CANCELLED") for item in authorities["rounds"])
            assert len({item["operation_key"] for item in authorities["payments"]}) == len(authorities["payments"])
            assert all(sum(effect["operation_key"] == operation["operation_key"]
                           for effect in authorities["effects"]) ==
                       int(operation["provider_effect_occurred"])
                       for operation in authorities["payments"])
            assert {item["status"] for item in authorities["sagas"]} == {"COMPLETED", "COMPENSATED"}
            report["journeys"] = journeys
            report["authority"] = {"trips": len(authorities["trips"]),
                                   "drivers": len(authorities["drivers"]),
                                   "rounds": len(authorities["rounds"]),
                                   "payments": len(authorities["payments"]),
                                   "effects": len(authorities["effects"])}
            check("AUTHORITY-AUDIT", True)
        finally:
            pass
        report["status"] = "PASS"
    finally:
        if os.environ.get("RELEASE_SMOKE_KEEP") == "1":
            log("Keeping release project " + project + " for inspection.")
        else:
            sentinel = "ride-release-sentinel-" + uuid.uuid4().hex[:12]
            subprocess.run(["docker", "run", "-d", "--rm", "--name", sentinel,
                            "alpine:3.21", "sleep", "300"], check=True,
                           text=True, capture_output=True)
            try:
                compose("down", "-v", "--remove-orphans", check=False)
                running = subprocess.run(["docker", "inspect", "-f", "{{.State.Running}}", sentinel],
                                         check=True, text=True, capture_output=True).stdout.strip()
                assert running == "true", "teardown removed an unrelated container"
                remaining = subprocess.run(
                    ["docker", "ps", "-aq", "--filter",
                     "label=com.docker.compose.project=" + project],
                    check=True, text=True, capture_output=True).stdout.strip()
                assert not remaining, "project containers remain: " + remaining
                report["teardown"] = {"sentinelPreserved": True, "projectContainers": 0}
                check("TEARDOWN-ISOLATED", True)
            finally:
                subprocess.run(["docker", "rm", "-f", sentinel], check=False,
                               text=True, capture_output=True)
    REPORT.parent.mkdir(parents=True, exist_ok=True)
    report["source"] = {
        "commit": subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip(),
        "tree": subprocess.check_output(["git", "rev-parse", "HEAD^{tree}"], cwd=ROOT, text=True).strip(),
    }
    report["limitations"].append(
        "Payment decline is exercised by the real Payment integration suite in the "
        "release-smoke CI job; the Production overlay intentionally exposes no "
        "simulator control route to force a remote decline.")
    REPORT.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print("PASS: Production release overlay runtime smoke; report " + str(REPORT))


if __name__ == "__main__":
    try:
        main()
    except Exception as exc:
        report["status"] = "FAILED"
        report["error"] = f"{type(exc).__name__}: {exc}"
        REPORT.parent.mkdir(parents=True, exist_ok=True)
        REPORT.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
        raise
