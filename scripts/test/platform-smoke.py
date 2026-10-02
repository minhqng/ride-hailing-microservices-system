"""Run against a dedicated Compose project; never resets user data."""
import base64
import os
import urllib.parse
import json
import subprocess
import time
import urllib.request


def compose(*args, check=True):
    return subprocess.run(["docker", "compose", *args], check=check, text=True, capture_output=True)


def wait_json(url, predicate=lambda data: True):
    for attempt in range(60):
        try:
            with urllib.request.urlopen(url, timeout=3) as response:
                data = json.load(response)
                if predicate(data):
                    return data
        except (OSError, ValueError):
            pass
        time.sleep(1)
    raise AssertionError(f"Timed out: {url}")


for port in [8080, 8101, 8102, 8103, 8104, 8105]:
    wait_json(f"http://localhost:{port}/health/live")
    wait_json(f"http://localhost:{port}/health/ready")

# Use TCP authentication, not the container's trusted local Unix socket.
# Positive control first; negative tests must specifically fail CONNECT permission.
for service in ["identity", "driver", "trip", "matching", "payment", "notification"]:
    password_variable = service.upper() + "_DB_PASSWORD"
    base = ["exec", "-T", "postgres", "sh", "-c",
            'export PGPASSWORD="$' + password_variable + '"; exec psql -h 127.0.0.1 -U "$1" -d "$2" -v ON_ERROR_STOP=1 -Atc "SELECT 1"',
            "smoke", service + "_app"]
    assert compose(*base, service + "_db").stdout.strip() == "1"
    for other in ["identity", "driver", "trip", "matching", "payment", "notification"]:
        if other == service:
            continue
        result = compose(*base, other + "_db", check=False)
        assert result.returncode != 0 and "permission denied for database" in result.stderr, (
            f"Expected CONNECT rejection: {service} -> {other}")

compose("exec", "-T", "redis", "redis-cli", "ping")
compose("exec", "-T", "rabbitmq", "rabbitmq-diagnostics", "ping")
wait_json("http://localhost:8105/info")
wait_json("http://localhost:16686/api/traces?service=payment-service&limit=1", lambda data: bool(data.get("data")))
for service in ["gateway", "identity", "driver", "trip", "matching", "payment", "notification",
                "postgres", "redis", "rabbitmq", "otel-collector", "jaeger", "prometheus", "grafana"]:
    container_id = compose("ps", "-a", "-q", service).stdout.strip()
    assert container_id, f"Missing service container: {service}"
    state = json.loads(subprocess.run(["docker", "inspect", container_id], check=True,
        text=True, capture_output=True).stdout)[0]["State"]
    assert state["Running"] and not state["Restarting"], (service, state["Status"])
# Versioned W2 declarations survive repeated Payment startup.
credential = base64.b64encode((os.getenv("RABBITMQ_USER", "ride_app") + ":" + os.getenv("RABBITMQ_PASSWORD", "ride_dev_pw")).encode()).decode()
def exchange_snapshot():
    snapshot = []
    for name in ["ride.commands", "ride.events"]:
        request = urllib.request.Request("http://localhost:15672/api/exchanges/%2F/" + name,
            headers={"Authorization": "Basic " + credential})
        with urllib.request.urlopen(request, timeout=5) as response:
            data = json.load(response)
        assert data["type"] == "topic" and data["durable"] and not data["auto_delete"], data
        snapshot.append((name, data["type"], data["durable"], data["auto_delete"]))
    return snapshot
before = exchange_snapshot()
compose("restart", "payment")
wait_json("http://localhost:8105/health/ready")
assert before == exchange_snapshot()
# OTel metric export is periodic; wait up to three minutes for a real HTTP metric.
query = urllib.parse.urlencode({"query": '{__name__=~"http_server_request_duration.*"}'})
for attempt in range(180):
    metrics = wait_json("http://localhost:9090/api/v1/query?" + query)
    if metrics.get("data", {}).get("result"):
        break
    time.sleep(1)
else:
    raise AssertionError("No exported ASP.NET HTTP request metrics in Prometheus")
wait_json("http://localhost:3000/api/health", lambda data: data.get("database") == "ok")
print("PASS: W2 exchange declarations/restart, exported HTTP metrics and Grafana health")
print("PASS: all 14 containers running, six HTTP processes, six own DB connections, 30 forbidden cross-DB connections, Redis, RabbitMQ, and Payment trace via Collector/Jaeger")
