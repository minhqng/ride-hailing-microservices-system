"""W3 HTTP + real PostgreSQL assertions; use a disposable Compose project."""
import json
import subprocess
import time
import urllib.error
import urllib.request
import uuid


def request(method, path, body=None, port=8105):
    req = urllib.request.Request(f"http://localhost:{port}{path}", method=method,
        data=None if body is None else json.dumps(body).encode(), headers={"Content-Type": "application/json"})
    try:
        with urllib.request.urlopen(req, timeout=15) as response:
            return response.status, json.load(response)
    except urllib.error.HTTPError as error:
        raw = error.read()
        try:
            body = json.loads(raw) if raw else None
        except ValueError:
            body = raw.decode(errors="replace")
        return error.code, body


def sql(query):
    result = subprocess.run(["docker", "compose", "exec", "-T", "postgres", "sh", "-c",
        'export PGPASSWORD="$PAYMENT_DB_PASSWORD"; exec psql -h 127.0.0.1 -U payment_app -d payment_db -v ON_ERROR_STOP=1 -Atc "$1"',
        "payment-smoke", query], check=True, capture_output=True, text=True)
    return result.stdout.strip()


cases = [
    ("Success", 200, "Succeeded", True),
    ("Decline", 200, "Declined", False),
    ("TransientFailure", 503, "Pending", False),
    ("TimeoutBeforeEffect", 504, "Unknown", False),
    ("TimeoutAfterEffect", 504, "Unknown", False),
    ("SlowSuccess", 200, "Succeeded", True),
]
saved = []
for operation_type in ["Authorize", "Capture", "Void"]:
    for mode, status, state, effect in cases:
        body = dict(operationKey=f"smoke-{uuid.uuid4()}", tripId=str(uuid.uuid4()),
                    type=operation_type, amountMinor=12000, currency="VND", mode=mode)
        actual_status, result = request("POST", "/simulator/operations", body)
        assert actual_status == status, (mode, actual_status, result)
        assert result["state"] == state and result["providerEffectOccurred"] is effect, result
        assert result["type"] == operation_type and result["attemptCount"] == 1
        assert (result["completedAt"] is not None) == (state in ["Succeeded", "Declined"])
        reread = request("GET", f'/simulator/operations/{result["id"]}')
        assert reread == (200, result), (result, reread)
        # Query persisted authority independently of the HTTP repository read.
        actual = sql(f"SELECT state || '|' || provider_effect_occurred::text || '|' || attempt_count::text FROM payment_operations WHERE id = '{uuid.UUID(result['id'])}'")
        assert actual == f"{state.upper()}|{str(effect).lower()}|1", actual
        assert request("POST", "/simulator/operations", body) == (status, result)
        assert request("POST", "/simulator/operations", dict(body, amountMinor=12001))[0] == 409
        saved.append((result, body, status))

before = sql("SELECT count(*) FROM payment_operations")
for update in [dict(type=999), dict(mode=999), dict(type="invalid"), dict(mode="invalid"),
               dict(amountMinor=0), dict(currency="vnd"), dict(operationKey=""), dict(tripId=str(uuid.UUID(int=0)))]:
    invalid = dict(body, **update)
    assert request("POST", "/simulator/operations", invalid)[0] == 400, update
for field in body:
    missing = {key: value for key, value in body.items() if key != field}
    assert request("POST", "/simulator/operations", missing)[0] == 400, field
assert sql("SELECT count(*) FROM payment_operations") == before

subprocess.run(["docker", "compose", "restart", "payment"], check=True)
for attempt in range(60):
    try:
        if request("GET", "/health/ready")[0] == 200:
            break
    except OSError:
        pass
    time.sleep(1)
else:
    raise AssertionError("Payment did not recover after restart")
for result, body, status in saved:
    assert request("POST", "/simulator/operations", body) == (status, result)
    assert request("GET", f'/simulator/operations/{result["id"]}') == (200, result)
print("PASS: 18 type/mode cases, DB assertions, 14 invalid/missing-field requests with no row writes, and persistence after restart. Same-key replay and conflicting payload rejection verified, including service restart.")

# W7: independent provider truth survives process restart; caller uncertainty is reconciled.
for result, body, status in saved:
    if body["mode"] == "TimeoutAfterEffect":
        assert sql("SELECT count(*) FROM provider_effects WHERE effect_id = 'sim-" + uuid.UUID(result["id"]).hex + "'") == "1"
        reconciled = request("POST", f'/simulator/operations/{result["id"]}/reconcile')
        assert reconciled[0] == 200 and reconciled[1]["state"] == "Succeeded", reconciled
        assert reconciled[1]["id"] == result["id"] and reconciled[1]["attemptCount"] == 1
        assert request("POST", "/simulator/operations", body) == reconciled
print("PASS: W7 response loss after independent provider commit reconciles same key after restart")
