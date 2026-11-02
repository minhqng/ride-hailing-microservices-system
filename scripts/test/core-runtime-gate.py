"""Exercise authenticated ride intake, durable Matching and real business authorities.

Requires the W10 controlled lifecycle actor built in Release. It invokes the
production accept/start/complete handlers; these operations have no public HTTP
transport. It never fabricates authority events or writes business rows directly.
"""
import json
import math
from decimal import Decimal, ROUND_HALF_EVEN
import os
from pathlib import Path
import subprocess
import time
import urllib.error
import urllib.request
import urllib.parse
import uuid

root = Path(__file__).resolve().parents[2]
project = os.environ.get("COMPOSE_PROJECT_NAME", "")
if not project.startswith(("team-w12-", "ride-ci-")):
    raise SystemExit("A dedicated test Compose project is required.")
settings = dict(line.split("=", 1) for line in (root / ".env").read_text().splitlines() if "=" in line and not line.startswith("#"))


def http(method, path, body=None, token=None, headers=None):
    headers = dict(headers or {})
    if token:
        headers["Authorization"] = "Bearer " + token
    if body is not None:
        headers["Content-Type"] = "application/json"
    request = urllib.request.Request("http://127.0.0.1:8080" + path, json.dumps(body).encode() if body is not None else None, headers, method=method)
    with urllib.request.urlopen(request, timeout=10) as response:
        return json.load(response)


def rows(database, query):
    result = subprocess.run(["docker", "compose", "exec", "-T", "postgres", "psql", "-U", "postgres", "-d", database,
                             "-v", "ON_ERROR_STOP=1", "-XAtc", "BEGIN READ ONLY; SELECT coalesce(json_agg(x),'[]') FROM (" + query + ") x; COMMIT"],
                            cwd=root, capture_output=True, text=True, check=True)
    return json.loads(result.stdout.removeprefix("BEGIN\n").removesuffix("COMMIT\n").strip())


def wait(label, action, predicate, seconds=90):
    deadline = time.monotonic() + seconds
    while time.monotonic() < deadline:
        value = action()
        if predicate(value):
            return value
        time.sleep(.5)
    raise AssertionError("Timed out waiting for " + label + ": " + json.dumps(value))


def account(role):
    username = "core-" + uuid.uuid4().hex
    password = uuid.uuid4().hex + "!"
    identity = http("POST", "/auth/register", {"username": username, "password": password, "role": role})
    token = http("POST", "/auth/login", {"username": username, "password": password})["access_token"]
    return identity["userId"], token


def actor(command, trip, driver, reservation):
    # Only Trip's own connection is supplied to this operator-controlled actor.
    environment = dict(os.environ, TRIP_DB="Host=postgres;Database=trip_db;Username=trip_app;Password=" + settings["TRIP_DB_PASSWORD"], TRACEPARENT=trace_parent)
    args = ["docker", "run", "--rm", "--name", "team-w12-lifecycle-actor", "--network", project + "_ride-net", "-v", str(root).replace("\\", "/") + ":/work:ro", "-w", "/work", "-e", "TRIP_DB", "-e", "TRACEPARENT",
            "mcr.microsoft.com/dotnet/sdk:10.0", "dotnet", "scripts/test/w10-g3-harness/bin/Release/net10.0/RideHailing.W10Harness.dll", command, trip, driver, reservation]
    result = subprocess.run(args, cwd=root, env=environment, capture_output=True, text=True, check=True)
    value = json.loads(result.stdout.strip().splitlines()[-1])
    assert value["ok"], "Production lifecycle handler rejected " + command
    return value


user, driver_token = account("Driver")
driver = http("POST", "/drivers", {"userId": user}, driver_token)
driver_id = driver["driverId"]
online = http("POST", "/drivers/" + driver_id + "/online", {"expectedVersion": driver["version"]}, driver_token)
session = uuid.uuid4().hex
http("POST", "/drivers/" + driver_id + "/location", {"deviceSessionId": session, "sequenceNo": 1,
     "capturedAt": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()), "latitude": 10.7769, "longitude": 106.7009}, driver_token)
def projection():
    result = subprocess.run(["docker", "compose", "exec", "-T", "redis", "redis-cli", "HGETALL", "driver:projection:" + driver_id.replace("-", "")], cwd=root, capture_output=True, text=True, check=True)
    return result.stdout


wait("Matching location projection", projection, lambda value: "AVAILABLE" in value and "latitude" in value)
scenarios = []
assignment_proofs = []
for scenario in ["completed", "cancelled-during-authorization", "terminal-authorization-decline"]:
    _, rider_token = account("Rider")
    trace_id = uuid.uuid4().hex
    trace_parent = "00-" + trace_id + "-" + uuid.uuid4().hex[:16] + "-01"
    trip = http("POST", "/trips", {"pickup": {"latitude": 10.7769, "longitude": 106.7009}, "dropoff": {"latitude": 10.78, "longitude": 106.71}}, rider_token, {"Idempotency-Key": uuid.uuid4().hex, "traceparent": trace_parent})
    trip_id = trip["tripId"]
    state = lambda: rows("trip_db", "SELECT * FROM trips WHERE trip_id='" + trip_id + "'")
    assigned = wait("durable Matching reservation", state, lambda value: value and value[0]["state"] == "DRIVER_RESERVED")[0]
    assert assigned["assigned_driver_id"] == driver_id
    reservation = assigned["reservation_id"]
    driver_truth = rows("driver_db", "SELECT * FROM drivers WHERE driver_id='" + driver_id + "'")[0]
    round_truth = wait("Matching terminal reservation", lambda: rows("matching_db", "SELECT * FROM matching_rounds WHERE trip_id='" + trip_id + "'"), lambda value: value and value[0]["status"] == "RESERVED")[0]
    attempts = rows("matching_db", "SELECT * FROM matching_attempts WHERE trip_id='" + trip_id + "' AND state='RESERVED'")
    assert driver_truth["status"] == "RESERVED" and driver_truth["reserved_trip_id"] == trip_id and driver_truth["reservation_id"] == reservation
    assert round_truth["status"] == "RESERVED" and round_truth["selected_driver_id"] == driver_id and round_truth["selected_reservation_id"] == reservation
    assert len(attempts) == 1 and attempts[0]["driver_id"] == driver_id and attempts[0]["reservation_id"] == reservation
    assignment_proofs.append({"trip": assigned, "driver": driver_truth, "round": round_truth, "winningAttempt": attempts[0]})
    if scenario == "completed":
        actor("accept", trip_id, driver_id, reservation)
        wait("real Payment authorization", state, lambda value: value[0]["state"] == "READY_TO_START")
        actor("start", trip_id, driver_id, reservation)
        wait("real Driver start effect", lambda: rows("driver_db", "SELECT * FROM drivers WHERE driver_id='" + driver_id + "'"), lambda value: value[0]["status"] == "ON_TRIP")
        actor("complete", trip_id, driver_id, reservation)
        saga_terminal = "COMPLETED"
        operations = {"AUTHORIZE", "CAPTURE"}
    elif scenario == "cancelled-during-authorization":
        # Bound the cancellation window by stopping this dedicated Payment
        # process before acceptance. No timing assumption decides whether the
        # authorization has already happened. Real queued work resumes after
        # cancellation and must drive the exact real-provider void.
        subprocess.run(["docker", "compose", "stop", "payment"], cwd=root, capture_output=True, check=True)
        try:
            actor("accept", trip_id, driver_id, reservation)
            wait("pending authorization", state, lambda value: value[0]["state"] == "DRIVER_ACCEPTED")
            http("POST", "/trips/" + trip_id + "/cancel", {}, rider_token)
        finally:
            subprocess.run(["docker", "compose", "start", "payment"], cwd=root, capture_output=True, check=True)
        saga_terminal = "COMPENSATED"
        operations = {"AUTHORIZE", "VOID"}
    else:
        # Configure a real simulator result through its Development control
        # API before the production authorization command arrives. No outcome
        # event or authority database row is fabricated by this harness.
        assert assigned["fare_currency"] == "VND"
        amount = int(Decimal(str(assigned["fare_amount"])).quantize(Decimal("1"), rounding=ROUND_HALF_EVEN))
        request = urllib.request.Request("http://127.0.0.1:8105/simulator/operations", method="POST",
            headers={"Content-Type": "application/json", "traceparent": trace_parent},
            data=json.dumps({"operationKey": trip_id + ":authorize:v1", "tripId": trip_id, "type": "Authorize",
                             "amountMinor": amount, "currency": "VND", "mode": "Decline"}).encode())
        with urllib.request.urlopen(request, timeout=10) as response:
            declined = json.load(response)
        assert declined["state"] == "Declined" and not declined["providerEffectOccurred"]
        actor("accept", trip_id, driver_id, reservation)
        saga_terminal = "COMPENSATED"
        operations = {"AUTHORIZE"}
    wait("terminal Saga", lambda: rows("trip_db", "SELECT * FROM ride_sagas WHERE trip_id='" + trip_id + "'"), lambda value: value and value[0]["status"] == saga_terminal)
    wait("Driver recovery", lambda: rows("driver_db", "SELECT * FROM drivers WHERE driver_id='" + driver_id + "'"), lambda value: value[0]["status"] == "AVAILABLE" and value[0]["reservation_id"] is None and value[0]["active_trip_id"] is None)
    payment = rows("payment_db", "SELECT * FROM payment_operations WHERE trip_id='" + trip_id + "'")
    assert len(payment) == len(operations) and {item["operation_type"] for item in payment} == operations
    if scenario == "terminal-authorization-decline":
        assert payment[0]["state"] == "DECLINED" and not payment[0]["provider_effect_occurred"]
        assert state()[0]["state"] == "CANCELLED"
        saga = rows("trip_db", "SELECT * FROM ride_sagas WHERE trip_id='" + trip_id + "'")[0]
        assert saga["last_error_code"] == "DECLINED"
        release = rows("trip_db", "SELECT message_id,payload_json FROM outbox_messages WHERE aggregate_id='" + trip_id + "' AND message_type='ReleaseDriverReservationV1'")
        assert len(release) == 1
        payload = release[0]["payload_json"]
        assert payload["driverId"] == driver_id and payload["reservationId"] == reservation
    else:
        assert all(item["state"] == "SUCCEEDED" and item["provider_effect_occurred"] for item in payment)
    scenarios.append({"scenario": scenario, "tripId": trip_id, "driverId": driver_id, "reservationId": reservation, "terminalSaga": saga_terminal, "traceId": trace_id})

# Independent final authority reads; no Trip-local proxies and no writes.
drivers = rows("driver_db", "SELECT * FROM drivers")
trips = rows("trip_db", "SELECT * FROM trips")
rounds = rows("matching_db", "SELECT * FROM matching_rounds")
payments = rows("payment_db", "SELECT * FROM payment_operations")
effects = rows("payment_db", "SELECT * FROM provider_effects")
sagas = rows("trip_db", "SELECT * FROM ride_sagas")
checks = []


def audit(identifier, condition, authorities):
    assert condition, identifier + " violated"
    checks.append({"id": identifier, "status": "PASS", "authorities": authorities})


active_states = {"DRIVER_RESERVED", "DRIVER_ACCEPTED", "READY_TO_START", "IN_PROGRESS"}
audit("AUD-01", all(sum(trip["assigned_driver_id"] == driver["driver_id"] for trip in trips if trip["state"] in active_states) <= 1 and
                     sum(driver[key] is not None for key in ["reserved_trip_id", "active_trip_id"]) <= 1 for driver in drivers), ["driver_db", "trip_db"])
audit("AUD-02", all((item["status"] in ("AVAILABLE", "OFFLINE") and all(item[key] is None for key in ["reservation_id", "reserved_trip_id", "active_trip_id"])) or
                     (item["status"] == "RESERVED" and item["reservation_id"] is not None and item["reserved_trip_id"] is not None and item["active_trip_id"] is None) or
                     (item["status"] == "ON_TRIP" and item["active_trip_id"] is not None and item["reserved_trip_id"] is None and item["reservation_id"] is None) for item in drivers), ["driver_db"])
audit("AUD-03", len({item["trip_id"] for item in trips}) == len(trips) and all((item["assigned_driver_id"] is None) == (item["reservation_id"] is None) for item in trips), ["trip_db"])
audit("AUD-04", len(assignment_proofs) == 3 and all(any(driver["driver_id"] == trip["assigned_driver_id"] and
      ((trip["state"] == "IN_PROGRESS" and driver["active_trip_id"] == trip["trip_id"]) or
       (driver["reservation_id"] == trip["reservation_id"] and driver["reserved_trip_id"] == trip["trip_id"]))
      for driver in drivers) for trip in trips if trip["state"] in active_states), ["trip_db", "driver_db", "matching_db"])
audit("AUD-05", len({item["operation_key"] for item in payments}) == len(payments) and len({item["operation_key"] for item in effects}) == len(effects) and all(sum(effect["operation_key"] == payment["operation_key"] for effect in effects) == int(payment["provider_effect_occurred"]) for payment in payments), ["payment_db", "provider_effects"])
terminal_messages = rows("matching_db", "SELECT aggregate_id,message_type,payload_json FROM outbox_messages WHERE message_type IN ('DriverReservedV1','NoDriverFoundV1')")
audit("AUD-06", len({(item["trip_id"], item["matching_round"]) for item in rounds}) == len(rounds) and all(not any(message["aggregate_id"] in (item["trip_id"], item["trip_id"].replace('-', '')) and message["message_type"] == ("NoDriverFoundV1" if item["status"] == "RESERVED" else "DriverReservedV1") for message in terminal_messages) for item in rounds if item["status"] in ("RESERVED", "NO_DRIVER")), ["matching_db"])
audit("AUD-07", all(not any(trip["trip_id"] in (driver["reserved_trip_id"], driver["active_trip_id"]) for driver in drivers) for trip in trips if trip["state"] in ("CANCELLED", "MATCH_FAILED", "COMPLETED")), ["trip_db", "driver_db"])
for database in ["trip_db", "driver_db", "matching_db", "payment_db"]:
    wait("Outbox drain " + database, lambda: rows(database, "SELECT message_id,status FROM outbox_messages WHERE status <> 'PUBLISHED'"), lambda value: not value)
wait("authoritative cleanup confirmation", lambda: rows("trip_db", "SELECT key FROM trip_cleanup_intents WHERE completed_at IS NULL"), lambda value: not value)
wait("Matching work resolution", lambda: rows("matching_db", "SELECT trip_id FROM matching_rounds WHERE status='PROCESSING'"), lambda value: not value)
audit("AUD-08", all(item["status"] in ("COMPLETED", "COMPENSATED") for item in sagas), ["trip_db", "driver_db", "matching_db", "payment_db"])

def exported_trace(trace_id):
    with urllib.request.urlopen("http://127.0.0.1:16686/api/traces/" + trace_id, timeout=5) as response:
        data = json.load(response).get("data", [])
    services = {item["serviceName"] for trace in data for item in trace.get("processes", {}).values()}
    return services


for scenario in scenarios:
    expected_services = {"gateway-service", "trip-service", "matching-service", "driver-service", "payment-service"}
    observed = wait("cross-service exported trace", lambda: exported_trace(scenario["traceId"]), lambda value: expected_services <= value)
    scenario["traceServices"] = sorted(observed)

metrics = []
for metric in ["trip_created_trips_total", "saga_compensation_sagas_total", "driver_reserve_success_total",
               "matching_round_total", "outbox_publish_messages_total", "payment_operation_operations_total",
               "rabbitmq_detailed_queue_messages_ready"]:
    def samples():
        with urllib.request.urlopen("http://127.0.0.1:9090/api/v1/query?" + urllib.parse.urlencode({"query": metric}), timeout=5) as response:
            payload = json.load(response)
        assert payload["status"] == "success"
        values = payload.get("data", {}).get("result", [])
        assert all(math.isfinite(float(item["value"][1])) for item in values), "Non-finite metric: " + metric
        return values
    values = wait("required metric " + metric, samples, bool, seconds=180)
    metrics.append({"name": metric, "series": len(values)})

def git(*arguments):
    return subprocess.check_output(["git", *arguments], cwd=root, text=True).strip()

source = {"commit": git("rev-parse", "HEAD"), "tree": git("rev-parse", "HEAD^{tree}"), "status": git("status", "--porcelain"),
          "runId": os.environ.get("GITHUB_RUN_ID"), "checkoutSha": os.environ.get("GITHUB_SHA")}
providers = {"postgres": rows("trip_db", "SELECT current_setting('server_version') AS version")[0]["version"]}
for service, command in [("rabbitmq", ["rabbitmq-diagnostics", "server_version"]), ("redis", ["redis-server", "--version"])]:
    providers[service] = subprocess.check_output(["docker", "compose", "exec", "-T", service, *command], cwd=root, text=True).strip()
output = root / "plans/reports/core-runtime-gate.json"
output.parent.mkdir(parents=True, exist_ok=True)
output.write_text(json.dumps({"project": project, "source": source, "providers": providers, "metrics": metrics, "scenarios": scenarios, "assignmentProofs": assignment_proofs, "checks": checks,
    "proofBoundary": "Real authenticated public ride intake; controlled production lifecycle handlers; live service authorities, providers and broker. No public accept/start/complete HTTP transport claim."}, indent=2) + "\n")
print("PASS: completed ride, exact authorization cancellation/void and terminal decline/release across real providers; AUD-01..08 inspected owning authorities.")
