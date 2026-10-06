"""W4 broker routing/redeclare and Production simulator isolation. Disposable Compose only."""
import base64
import json
import os
import subprocess
import time
import urllib.error
import urllib.parse
import urllib.request

credentials = base64.b64encode((os.getenv("RABBITMQ_USER", "ride_app") + ":" +
    os.getenv("RABBITMQ_PASSWORD", "ride_dev_pw")).encode()).decode()


def api(method, path, data=None):
    req = urllib.request.Request("http://localhost:15672/api/" + path, method=method,
        headers={"Authorization": "Basic " + credentials, "Content-Type": "application/json"},
        data=None if data is None else json.dumps(data).encode())
    with urllib.request.urlopen(req, timeout=10) as response:
        raw = response.read()
        return json.loads(raw) if raw else None


expected = {
    "matching.ride-requested.q": ("ride.events", ["trip.ride-requested.v1"]),
    "trip.driver-state.q": ("ride.events", ["driver.reserved.v1", "driver.reservation-released.v1", "driver.trip-started.v1", "driver.trip-completed.v1"]),
    "trip.matching-outcome.q": ("ride.events", ["matching.failed.v1"]),
    "trip.payment-outcome.q": ("ride.events", ["payment.authorized.v1", "payment.authorization-failed.v1", "payment.authorization-voided.v1", "payment.captured.v1", "payment.capture-pending.v1", "payment.requires-review.v1"]),
    "matching.driver-projection.q": ("ride.events", ["driver.available.v1", "driver.offline.v1", "driver.reserved.v1", "driver.reservation-released.v1", "driver.trip-started.v1", "driver.trip-completed.v1", "driver.location-accepted.v1"]),
    "payment.commands.q": ("ride.commands", ["payment.authorize.v1", "payment.capture.v1", "payment.void-authorization.v1"]),
    "driver.commands.q": ("ride.commands", ["driver.release-reservation.v1", "driver.mark-trip-started.v1", "driver.mark-trip-completed.v1"]),
    "matching.commands.q": ("ride.commands", ["matching.cancel-round.v1"]),
    "notification.business-events.q": ("ride.events", ["trip.#", "driver.#", "payment.#", "matching.#"]),
}


def snapshot():
    exchanges = {x["name"]: (x["type"], x["durable"], x["auto_delete"]) for x in api("GET", "exchanges/%2F") if x["name"].startswith("ride.")}
    assert exchanges == {"ride.commands": ("topic", True, False), "ride.events": ("topic", True, False), "ride.deadletter": ("topic", True, False)}
    queues = {q["name"]: (q["durable"], q["auto_delete"], q["exclusive"], q["arguments"]) for q in api("GET", "queues/%2F")}
    assert set(queues) == set(expected) | {q + ".dead" for q in expected}
    for declaration in queues.values():
        assert declaration == (True, False, False, {"x-queue-type": "quorum"})
    bindings = {(b["source"], b["destination"], b["routing_key"]) for b in api("GET", "bindings/%2F") if b["source"].startswith("ride.")}
    assert bindings == {(exchange, queue, key) for queue, (exchange, keys) in expected.items() for key in keys} | {("ride.deadletter", q + ".dead", q) for q in expected}
    return exchanges, queues, bindings


before = snapshot()
subprocess.run(["docker", "compose", "restart", "payment"], check=True)
for attempt in range(60):
    try:
        with urllib.request.urlopen("http://localhost:8105/health/ready", timeout=3) as response:
            if response.status == 200:
                break
    except OSError:
        pass
    time.sleep(1)
else:
    raise AssertionError("Payment failed to redeclare topology")
assert snapshot() == before

# Route every catalog contract to its independent consumer queues.
routes = {(exchange, key) for queue, (exchange, keys) in expected.items() if queue != "notification.business-events.q" for key in keys}
for exchange, key in sorted(routes):
    result = api("POST", f"exchanges/%2F/{exchange}/publish", dict(
        properties=dict(delivery_mode=2, message_id=key, content_type="application/json"),
        routing_key=key, payload=json.dumps({"contract": key}), payload_encoding="string"))
    assert result["routed"], key

for queue, (exchange, keys) in expected.items():
    wanted = {key for source, key in routes if source == exchange} if queue == "notification.business-events.q" else set(keys)
    received = set()
    for attempt in range(20):
        messages = api("POST", f"queues/%2F/{urllib.parse.quote(queue, safe='')}/get",
            dict(count=50, ackmode="ack_requeue_false", encoding="auto", truncate=10000))
        for message in messages:
            assert message["properties"]["delivery_mode"] == 2
            received.add(message["properties"]["message_id"])
        if received == wanted:
            break
        time.sleep(0.2)
    assert received == wanted, (queue, received, wanted)

# Separate Production instance shares only this disposable test project's infrastructure.
container = subprocess.run(["docker", "compose", "run", "-d", "--no-deps", "--rm",
    "-e", "ASPNETCORE_ENVIRONMENT=Production", "-p", "127.0.0.1:18105:8080", "payment"],
    check=True, capture_output=True, text=True).stdout.strip()
try:
    for attempt in range(60):
        try:
            with urllib.request.urlopen("http://localhost:18105/health/live", timeout=3):
                break
        except OSError:
            time.sleep(1)
    else:
        raise AssertionError("Production instance failed to boot")
    for method, path in [("POST", "/simulator/operations"), ("GET", "/simulator/operations/11111111-1111-1111-1111-111111111111")]:
        req = urllib.request.Request("http://localhost:18105" + path, method=method,
            data=b"{}" if method == "POST" else None, headers={"Content-Type": "application/json"})
        try:
            urllib.request.urlopen(req, timeout=3)
            raise AssertionError("Simulator endpoint exposed in Production")
        except urllib.error.HTTPError as error:
            assert error.code == 404, error.code
finally:
    subprocess.run(["docker", "rm", "-f", container], check=True, capture_output=True)
print(f"PASS: {len(expected)} quorum queues, exact bindings, {len(routes)} routed contracts, fan-out, idempotent startup, persistent properties, Production endpoints absent")
