"""Run against a dedicated Compose project; never resets user data."""
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
print("PASS: six HTTP processes, six own DB connections, 30 forbidden cross-DB connections, Redis, RabbitMQ, and Payment trace via Collector/Jaeger")
