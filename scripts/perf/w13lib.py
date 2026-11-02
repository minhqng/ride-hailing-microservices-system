"""Shared, dependency-free helpers for the W13 performance harness.

The harness is deliberately a thin measurement layer over the real Compose
topology. It never changes service behavior and never fabricates a measurement:
every run records the source revision, the configured and observed environment,
the workload, the per-sample outcome split and the final authoritative state.
"""

from __future__ import annotations

import hashlib
import http.client
import json
import math
import os
import platform
import re
import shutil
import socket
import subprocess
import sys
import time
import urllib.parse
import uuid
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Iterable

ROOT = Path(__file__).resolve().parents[2]
PERF_DIR = ROOT / "scripts" / "perf"
EVIDENCE_ROOT = ROOT / "evidence" / "member-b" / "w13-performance-campaign"
RUNS_ROOT = Path(os.environ.get("W13_RUNS_ROOT", str(EVIDENCE_ROOT / "runs")))
G4_EVIDENCE = EVIDENCE_ROOT / "g4" / "g4-readiness.json"
SCHEMA_PATH = PERF_DIR / "schema" / "w13-manifest.schema.json"
COMPOSE_FILES = ["compose.yaml", "scripts/perf/compose.w13.yaml"]
PROJECT = "ride-hailing-w13"
K6_IMAGE = os.environ.get("W13_K6_IMAGE", "grafana/k6:0.54.0")
BASE_IMAGES = {
    "postgres": "postgres:18.6",
    "redis": "redis:8.10.2",
    "rabbitmq": "rabbitmq:4.3.6-management",
}

EXPERIMENTS = ("E01", "E02", "E03")
RUN_ID_RE = re.compile(r"^W13-(E01|E02|E03)-\d{8}T\d{6}Z-[A-Za-z0-9]{4,16}$")
OUTCOMES = ("success", "domainConflict", "systemError", "timeout", "unexpected")


class HarnessError(RuntimeError):
    """A harness/setup failure that is not a measurement result."""


class GateError(HarnessError):
    """Report-grade execution was refused because the G4 gate is not green."""


def utc_now() -> datetime:
    return datetime.now(timezone.utc)


def utc_stamp(moment: datetime | None = None) -> str:
    return (moment or utc_now()).strftime("%Y%m%dT%H%M%SZ")


def iso(moment: datetime | None = None) -> str:
    return (moment or utc_now()).isoformat().replace("+00:00", "Z")


def new_run_id(experiment: str, seed: int) -> str:
    if experiment not in EXPERIMENTS:
        raise HarnessError(f"unknown experiment {experiment!r}")
    suffix = f"{seed & 0xFFFF:04x}{uuid.uuid4().hex[:4]}"
    return f"W13-{experiment}-{utc_stamp()}-{suffix}"


def seeded_uuid(seed: int, label: str) -> str:
    return str(uuid.uuid5(uuid.NAMESPACE_URL, f"w13:{seed}:{label}"))


def sha256_text(text: str) -> str:
    return hashlib.sha256(text.encode("utf-8")).hexdigest()


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def read_json(path: Path) -> Any:
    with path.open("r", encoding="utf-8") as handle:
        return json.load(handle)


def write_json(path: Path, data: Any) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    temp = path.with_suffix(path.suffix + ".tmp")
    with temp.open("w", encoding="utf-8", newline="\n") as handle:
        json.dump(data, handle, indent=2, sort_keys=False)
        handle.write("\n")
    temp.replace(path)


def write_text(path: Path, text: str) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(text, encoding="utf-8", newline="\n")


def run(
    command: list[str],
    *,
    cwd: Path | None = None,
    env: dict[str, str] | None = None,
    timeout: float | None = 300,
    check: bool = False,
) -> subprocess.CompletedProcess[str]:
    merged = os.environ.copy()
    if env:
        merged.update(env)
    completed = subprocess.run(
        command,
        cwd=str(cwd or ROOT),
        env=merged,
        text=True,
        encoding="utf-8",
        errors="replace",
        capture_output=True,
        timeout=timeout,
        check=False,
    )
    if check and completed.returncode != 0:
        raise HarnessError(
            f"command failed ({completed.returncode}): {' '.join(command)}\n"
            f"stdout:\n{completed.stdout[-4000:]}\nstderr:\n{completed.stderr[-4000:]}"
        )
    return completed


def docker(*args: str, timeout: float | None = 120, check: bool = False) -> subprocess.CompletedProcess[str]:
    return run(["docker", *args], timeout=timeout, check=check)


def compose_args(*args: str) -> list[str]:
    command: list[str] = ["docker", "compose", "--profile", "obs"]
    for compose_file in COMPOSE_FILES:
        command.extend(["-f", compose_file])
    command.extend(args)
    return command


def compose(*args: str, timeout: float | None = 600, check: bool = False) -> subprocess.CompletedProcess[str]:
    return run(compose_args(*args), timeout=timeout, check=check,
               env={"COMPOSE_PROJECT_NAME": PROJECT})


def compose_up(services: Iterable[str], *, build: bool = True, scale: int | None = None,
               wait_timeout: int = 240) -> None:
    args = ["up", "-d", "--wait", "--wait-timeout", str(wait_timeout)]
    if build and os.environ.get("W13_USE_PREBUILT_IMAGES") != "1":
        args.append("--build")
    else:
        args.append("--no-build")
    if scale is not None:
        args.extend(["--scale", f"matching={scale}"])
    else:
        args.extend([])
    args.extend(dict.fromkeys([*services, "otel-collector", "prometheus", "jaeger"]))
    result = compose(*args, timeout=wait_timeout + 300)
    if result.returncode != 0:
        raise HarnessError(
            f"compose up failed ({result.returncode})\nstdout:\n{result.stdout[-4000:]}\n"
            f"stderr:\n{result.stderr[-4000:]}"
        )


def compose_down(*, volumes: bool = True) -> None:
    args = ["down", "--remove-orphans"]
    if volumes:
        args.append("-v")
    result = compose(*args, timeout=180)
    if result.returncode != 0:
        raise HarnessError(f"compose down failed ({result.returncode}): {result.stderr[-2000:]}")


def compose_ps() -> list[dict[str, Any]]:
    result = compose("ps", "--format", "json", timeout=60)
    if result.returncode != 0:
        raise HarnessError(f"compose ps failed: {result.stderr[-2000:]}")
    text = result.stdout.strip()
    if not text:
        return []
    # Compose v2/v5 emit either a JSON array or one object per line.
    try:
        parsed = json.loads(text)
        if isinstance(parsed, dict):
            return [parsed]
        if isinstance(parsed, list):
            return parsed
    except json.JSONDecodeError:
        rows = []
        for line in text.splitlines():
            line = line.strip()
            if line:
                rows.append(json.loads(line))
        return rows
    raise HarnessError("unexpected `docker compose ps --format json` output")


def compose_config() -> dict[str, Any]:
    result = compose("config", "--format", "json", timeout=60)
    if result.returncode != 0:
        raise HarnessError(f"compose config failed: {result.stderr[-2000:]}")
    return json.loads(result.stdout)


def container_stats() -> list[dict[str, Any]]:
    result = docker("stats", "--no-stream", "--format", "{{json .}}", timeout=60)
    if result.returncode != 0:
        return []
    rows = []
    for line in result.stdout.splitlines():
        line = line.strip()
        if line:
            try:
                rows.append(json.loads(line))
            except json.JSONDecodeError:
                continue
    return rows


def psql(database: str, sql: str, *, user: str = "postgres", timeout: float = 30) -> list[list[str]]:
    result = compose(
        "exec", "-T", "-e", "PGOPTIONS=-c default_transaction_read_only=on", "postgres", "psql", "-U", user, "-d", database,
        "-v", "ON_ERROR_STOP=1", "-tA", "-F", "\t", "-c", sql,
        timeout=timeout,
    )
    if result.returncode != 0:
        raise HarnessError(
            f"psql against {database} failed ({result.returncode}): {result.stderr[-2000:]}"
        )
    rows: list[list[str]] = []
    for line in result.stdout.splitlines():
        if line.strip() == "":
            continue
        rows.append(line.split("\t"))
    return rows


def psql_scalar(database: str, sql: str, *, user: str = "postgres") -> str | None:
    rows = psql(database, sql, user=user)
    if not rows or not rows[0]:
        return None
    return rows[0][0]


def psql_json(database: str, sql: str) -> Any:
    rows = psql(database, sql)
    if not rows:
        return None
    payload = "\n".join("\t".join(row) for row in rows)
    return json.loads(payload)


def psql_json_rows(database: str, query: str) -> list[dict[str, Any]]:
    wrapped = f"SELECT COALESCE(json_agg(t), '[]'::json) FROM ({query}) t;"
    value = psql_json(database, wrapped)
    if value is None:
        return []
    return value if isinstance(value, list) else [value]


def management_url(path: str) -> str:
    port = os.environ.get("W13_RABBIT_MGMT_PORT", "25672")
    return f"http://127.0.0.1:{port}{path}"


def rabbit_auth_headers() -> dict[str, str]:
    import base64

    user = os.environ.get("RABBITMQ_USER", "ride_app")
    password = os.environ.get("RABBITMQ_PASSWORD", "ride_dev_pw")
    token = base64.b64encode(f"{user}:{password}".encode("utf-8")).decode("ascii")
    return {"Authorization": f"Basic {token}", "Content-Type": "application/json"}


def rabbit_api(path: str, method: str = "GET", body: Any = None, timeout: float = 10) -> Any:
    status, _, payload, _ = http_request(
        method, management_url(path), body=body, headers=rabbit_auth_headers(), timeout=timeout
    )
    if status < 200 or status >= 300:
        raise HarnessError(f"RabbitMQ management {method} {path} returned {status}: {payload[:500]!r}")
    if not payload:
        return None
    return json.loads(payload)


def wait_rabbit_queue(queue: str, *, timeout: float = 120) -> dict[str, Any]:
    deadline = time.monotonic() + timeout
    encoded = urllib.parse.quote(queue, safe="")
    last_error: Exception | None = None
    while time.monotonic() < deadline:
        try:
            return rabbit_api(f"/api/queues/%2F/{encoded}")
        except Exception as exc:  # noqa: BLE001 - preflight probe
            last_error = exc
            time.sleep(1)
    raise HarnessError(f"RabbitMQ queue {queue} did not become visible: {last_error}")


def http_request(
    method: str,
    url: str,
    *,
    body: Any = None,
    headers: dict[str, str] | None = None,
    timeout: float = 15,
    connection: http.client.HTTPConnection | None = None,
) -> tuple[int, dict[str, str], str, float]:
    parsed = urllib.parse.urlparse(url)
    if parsed.scheme != "http":
        raise HarnessError(f"only local http endpoints are supported, got {url}")
    host = parsed.hostname or "127.0.0.1"
    port = parsed.port or 80
    path = parsed.path or "/"
    if parsed.query:
        path += "?" + parsed.query
    own_connection = connection is None
    conn = connection or http.client.HTTPConnection(host, port, timeout=timeout)
    data = None
    request_headers = {"Accept": "application/json", **(headers or {})}
    if body is not None:
        data = json.dumps(body).encode("utf-8")
        request_headers.setdefault("Content-Type", "application/json")
    started = time.perf_counter()
    try:
        conn.request(method, path, body=data, headers=request_headers)
        response = conn.getresponse()
        payload = response.read().decode("utf-8", errors="replace")
        elapsed_ms = (time.perf_counter() - started) * 1000.0
        response_headers = {key.lower(): value for key, value in response.getheaders()}
        return response.status, response_headers, payload, elapsed_ms
    finally:
        if own_connection:
            conn.close()


def wait_http(url: str, *, timeout: float = 120, expected: tuple[int, ...] = (200,)) -> None:
    deadline = time.monotonic() + timeout
    last: Exception | str | None = None
    while time.monotonic() < deadline:
        try:
            status, _, payload, _ = http_request("GET", url, timeout=5)
            if status in expected:
                return
            last = f"status {status}: {payload[:200]}"
        except Exception as exc:  # noqa: BLE001 - preflight probe
            last = exc
        time.sleep(1)
    raise HarnessError(f"endpoint {url} did not become ready: {last}")


def parse_json(payload: str) -> Any:
    if not payload:
        return None
    try:
        return json.loads(payload)
    except json.JSONDecodeError:
        return None


def percentile_nearest_rank(values: list[float], percentile: float) -> float | None:
    """Nearest-rank percentile (P50/P95 convention documented in the README)."""
    if not values:
        return None
    if any(not math.isfinite(value) or value < 0 for value in values):
        raise HarnessError("Latency samples must be finite and non-negative")
    if not 0 < percentile <= 100:
        raise ValueError("percentile must be within (0, 100]")
    ordered = sorted(values)
    rank = max(1, int(-(-percentile / 100.0 * len(ordered) // 1)))
    return ordered[min(rank, len(ordered)) - 1]


def outcome_summary(samples: list[dict[str, Any]]) -> dict[str, int]:
    summary = {key: 0 for key in OUTCOMES}
    for sample in samples:
        outcome = sample.get("outcome")
        if outcome in summary:
            summary[outcome] += 1
    return summary


def find_dotnet() -> str | None:
    executable = shutil.which("dotnet")
    if executable:
        return executable
    candidate = Path.home() / ".dotnet" / ("dotnet.exe" if os.name == "nt" else "dotnet")
    if candidate.exists():
        return str(candidate)
    return None


def find_k6() -> str | None:
    return shutil.which("k6")


_SECRET_KEY_PARTS = ("password", "secret", "token", "credential", "passwd", "pwd", "privatekey", "private_key", "cookie")
_CONNECTION_SECRET_RE = re.compile(r"(?i)(password|pwd)=([^;\s]+)")


def _redact_string(value: str) -> str:
    value = re.sub(r"([a-zA-Z][a-zA-Z0-9+.-]*://)[^/\s]*@", r"\1<redacted>@", value)
    return _CONNECTION_SECRET_RE.sub(lambda match: f"{match.group(1)}=<redacted>", value)


def _redact(value: Any, key_hint: str = "") -> Any:
    if isinstance(value, dict):
        redacted = {}
        for key, item in value.items():
            lowered = str(key).lower()
            if any(part in lowered for part in _SECRET_KEY_PARTS) or lowered.endswith("pass"):
                redacted[key] = "<redacted>"
            else:
                redacted[key] = _redact(item, str(key))
        return redacted
    if isinstance(value, list):
        return [_redact(item, key_hint) for item in value]
    if isinstance(value, str):
        return _redact_string(value)
    return value


def _safe_json_command(command: list[str], timeout: float = 30) -> Any:
    result = run(command, timeout=timeout)
    if result.returncode != 0:
        return None
    text = result.stdout.strip()
    if not text:
        return None
    try:
        return json.loads(text)
    except json.JSONDecodeError:
        return text


def _powershell(command: str) -> str | None:
    executable = shutil.which("powershell.exe") or shutil.which("powershell")
    if not executable:
        return None
    result = run([executable, "-NoProfile", "-NonInteractive", "-Command", command], timeout=20)
    if result.returncode != 0:
        return None
    return result.stdout.strip() or None


def _host_info() -> dict[str, Any]:
    cpu_model = None
    if os.name == "nt":
        cpu_model = _powershell("(Get-CimInstance Win32_Processor | Select-Object -First 1).Name")
    if not cpu_model:
        try:
            cpuinfo = Path("/proc/cpuinfo")
            if cpuinfo.exists():
                for line in cpuinfo.read_text(encoding="utf-8", errors="replace").splitlines():
                    if line.lower().startswith("model name"):
                        cpu_model = line.split(":", 1)[1].strip()
                        break
        except OSError:
            pass
    docker_info = _safe_json_command(["docker", "info", "--format", "{{json .}}"], timeout=30) or {}
    total_memory = docker_info.get("MemTotal") if isinstance(docker_info, dict) else None
    return {
        "platform": platform.platform(),
        "machine": platform.machine(),
        "os": os.name,
        "python": platform.python_version(),
        "logicalCpus": docker_info.get("NCPU") if isinstance(docker_info, dict) else os.cpu_count(),
        "cpuModel": cpu_model or platform.processor() or "unknown",
        "memoryBytes": total_memory,
        "hostId": sha256_text(socket.gethostname()),
    }


def _image_info(image: str) -> dict[str, Any] | None:
    result = docker("image", "inspect", image, "--format", "{{json .}}", timeout=30)
    if result.returncode != 0:
        return None
    try:
        data = json.loads(result.stdout)
    except json.JSONDecodeError:
        return None
    return {
        "reference": image,
        "id": data.get("Id"),
        "repoDigests": data.get("RepoDigests", []),
        "created": data.get("Created"),
    }


def _container_inspect(container_id: str) -> dict[str, Any] | None:
    result = docker("inspect", container_id, "--format", "{{json .}}", timeout=30)
    if result.returncode != 0:
        return None
    try:
        data = json.loads(result.stdout)
    except json.JSONDecodeError:
        return None
    host_config = data.get("HostConfig") or {}
    config = data.get("Config") or {}
    return {
        "id": data.get("Id"),
        "name": (data.get("Name") or "").lstrip("/"),
        "image": data.get("Image"),
        "hostname": config.get("Hostname"),
        "startedAt": (data.get("State") or {}).get("StartedAt"),
        "status": (data.get("State") or {}).get("Status"),
        "nanoCpus": host_config.get("NanoCpus"),
        "memoryBytes": host_config.get("Memory"),
        "env": _redact({item.split("=", 1)[0]: item.split("=", 1)[1] if "=" in item else "" for item in (config.get("Env") or [])}),
    }


def _configured_env() -> dict[str, Any]:
    keys = sorted(key for key in os.environ if key.startswith("W13_"))
    return _redact({key: os.environ[key] for key in keys})


def capture_environment(*, include_observed: bool = True) -> dict[str, Any]:
    compose_config_data = compose_config()
    services_configured: dict[str, Any] = {}
    for name, service in (compose_config_data.get("services") or {}).items():
        services_configured[name] = {
            "image": service.get("image") or f"build:{service.get('build', {}).get('dockerfile')}",
            "cpus": service.get("cpus"),
            "memLimitBytes": service.get("mem_limit"),
            "publishedPorts": [port.get("published") for port in service.get("ports", [])],
            "environment": _redact(service.get("environment") or {}),
        }

    observed: dict[str, Any] = {"containers": [], "database": None, "broker": None, "redis": None}
    running_services: set[str] = set()
    if include_observed:
        rows = compose_ps()
        running_services = {
            str(row.get("Service") or row.get("service") or "")
            for row in rows
            if str(row.get("State") or row.get("state") or "").lower() == "running"
        }
        for row in rows:
            container_id = row.get("ID") or row.get("Id") or row.get("ContainerID")
            service = row.get("Service") or row.get("service")
            inspect_data = _container_inspect(container_id) if container_id else None
            observed["containers"].append(
                {
                    "service": service,
                    "name": row.get("Name") or row.get("name"),
                    "state": row.get("State") or row.get("state"),
                    "health": row.get("Health") or row.get("health"),
                    "inspect": inspect_data,
                }
            )
        try:
            if "postgres" not in running_services:
                raise HarnessError("postgres service is not running")
            observed["database"] = {
                "serverVersion": psql_scalar("postgres", "SHOW server_version;"),
                "maxConnections": psql_scalar("postgres", "SHOW max_connections;"),
                "sharedBuffers": psql_scalar("postgres", "SHOW shared_buffers;"),
                "workMem": psql_scalar("postgres", "SHOW work_mem;"),
                "activeConnections": psql_scalar(
                    "postgres",
                    "SELECT count(*)::text FROM pg_stat_activity WHERE datname IN "
                    "('driver_db','matching_db','trip_db');",
                ),
            }
        except Exception as exc:  # noqa: BLE001 - capture is best effort
            observed["database"] = {"error": str(exc)}
        try:
            if "rabbitmq" not in running_services:
                raise HarnessError("rabbitmq service is not running")
            overview = rabbit_api("/api/overview")
            observed["broker"] = {
                "version": overview.get("rabbitmq_version"),
                "cluster": overview.get("cluster_name"),
                "queueTotals": overview.get("queue_totals"),
                "objectTotals": overview.get("object_totals"),
            }
        except Exception as exc:  # noqa: BLE001 - capture is best effort
            observed["broker"] = {"error": str(exc)}
        try:
            if "redis" not in running_services:
                raise HarnessError("redis service is not running")
            result = compose("exec", "-T", "redis", "redis-server", "--version", timeout=30)
            info = compose("exec", "-T", "redis", "redis-cli", "INFO", "server", timeout=30)
            version_line = next(
                (line for line in info.stdout.splitlines() if line.startswith("redis_version:")), None
            )
            observed["redis"] = {
                "server": result.stdout.strip(),
                "version": version_line.split(":", 1)[1] if version_line else None,
            }
        except Exception as exc:  # noqa: BLE001 - capture is best effort
            observed["redis"] = {"error": str(exc)}

    dotnet = find_dotnet()
    return {
        "capturedAtUtc": iso(),
        "host": _host_info(),
        "docker": {
            "client": (_safe_json_command(["docker", "version", "--format", "{{json .Client}}"], timeout=30) or {}),
            "server": (_safe_json_command(["docker", "version", "--format", "{{json .Server}}"], timeout=30) or {}),
            "compose": (run(["docker", "compose", "version", "--short"], timeout=30).stdout or "").strip() or None,
        },
        "tooling": {
            "dotnet": run([dotnet, "--version"], timeout=30).stdout.strip() if dotnet else None,
            "python": platform.python_version(),
            "k6Installed": find_k6(),
            "k6Image": _image_info(K6_IMAGE),
        },
        "images": {
            name: _image_info(image) for name, image in BASE_IMAGES.items()
        },
        "project": compose_config_data.get("name"),
        "configured": {
            "composeServices": services_configured,
            "w13Environment": _configured_env(),
        },
        "observed": observed,
    }


def source_binding() -> dict[str, Any]:
    status = run(
        ["git", "status", "--porcelain", "--", ".", ":(exclude)evidence"], timeout=30
    ).stdout
    all_status = run(["git", "status", "--porcelain"], timeout=30).stdout
    commit = run(["git", "rev-parse", "HEAD"], timeout=30, check=True).stdout.strip()
    tree = run(["git", "rev-parse", "HEAD^{tree}"], timeout=30, check=True).stdout.strip()
    branch = run(["git", "branch", "--show-current"], timeout=30).stdout.strip()
    remote = run(["git", "remote", "get-url", "origin"], timeout=30).stdout.strip()
    return {
        "commit": commit,
        "tree": tree,
        "branch": branch,
        "remote": _redact_string(remote),
        "dirty": bool(status.strip()),
        "statusDigest": sha256_text(status),
        "statusPreview": status.strip().splitlines()[:40],
        "evidenceChanges": all_status != status,
    }


def g4_readiness(path: Path | None = None) -> dict[str, Any]:
    """Read the curated G4 evidence and independently re-check live B-owned facts."""
    evidence_path = path or Path(os.environ.get("W13_G4_EVIDENCE", G4_EVIDENCE))
    evidence_path = evidence_path.resolve()
    result: dict[str, Any] = {
        "green": False,
        "verdict": "RED",
        "evidencePath": str(evidence_path),
        "blockers": [],
        "checks": {},
        "checkedAtUtc": iso(),
    }
    try:
        evidence_path.relative_to(EVIDENCE_ROOT.resolve())
    except ValueError:
        result["blockers"].append(
            f"G4 evidence must live under {EVIDENCE_ROOT}: {evidence_path}"
        )
        return result
    if not evidence_path.exists():
        result["blockers"].append(f"G4 evidence is missing: {evidence_path}")
        return result
    try:
        evidence = read_json(evidence_path)
    except Exception as exc:  # noqa: BLE001 - evidence validation
        result["blockers"].append(f"G4 evidence is unreadable: {exc}")
        return result

    if evidence.get("schemaVersion") != 1:
        result["blockers"].append("G4 evidence schemaVersion must be 1")
    checks = evidence.get("checks") or {}
    required = (
        "bOwnedCorrectness",
        "securityBaseline",
        "aCorrectnessAcceptance",
        "cCorrectnessAcceptance",
        "authoritativeAuditor",
        "productionApiSurface",
        "brokerSurface",
        "stableEnvironment",
    )
    for key in required:
        if checks.get(key) != "PASS":
            result["blockers"].append(f"G4 check {key} is {checks.get(key, 'MISSING')}")
    result["checks"] = checks

    source_commit = evidence.get("sourceCommit")
    current = run(["git", "rev-parse", "HEAD"], timeout=30, check=True).stdout.strip()
    source_status = run(["git", "status", "--porcelain", "--", "src", "tests"], timeout=30, check=True)
    if source_status.stdout.strip():
        result["blockers"].append("production/test source has uncommitted changes; G4 is not valid for this source")
    if not source_commit:
        result["blockers"].append("G4 evidence has no sourceCommit")
    else:
        ancestor = run(["git", "merge-base", "--is-ancestor", source_commit, current], timeout=30)
        if ancestor.returncode != 0:
            result["blockers"].append(
                f"G4 sourceCommit {source_commit[:12]} is not an ancestor of the W13 source {current[:12]}"
            )
        code_diff = run(["git", "diff", "--quiet", source_commit, current, "--", "src", "tests"], timeout=60)
        if code_diff.returncode != 0:
            result["blockers"].append(
                "production/test source changed after the G4-certified commit; G4 is not valid for this source"
            )

    blockers = list(evidence.get("blockers") or [])
    result["blockers"].extend(blockers)
    result["verdict"] = str(evidence.get("verdict") or "RED")
    if result["verdict"] == "GREEN" and not result["blockers"]:
        result["green"] = True
    return result


def ensure_report_grade(g4: dict[str, Any], source: dict[str, Any] | None = None) -> None:
    if not g4.get("green"):
        raise GateError(
            "report-grade execution refused: G4 is not green. Blockers: "
            + "; ".join(g4.get("blockers") or ["G4 evidence missing or invalid"])
        )
    source = source or source_binding()
    if source.get("dirty"):
        raise GateError(
            "report-grade execution refused: the source tree outside evidence/ is dirty "
            f"(digest {source.get('statusDigest')})"
        )


def manifest_base(
    *,
    experiment: str,
    run_id: str,
    report_grade: bool,
    smoke: bool,
    g4: dict[str, Any],
    seed: int,
) -> dict[str, Any]:
    return {
        "schemaVersion": 1,
        "experiment": experiment,
        "runId": run_id,
        "reportGrade": report_grade,
        "smoke": smoke,
        "source": source_binding(),
        "g4": {
            "green": bool(g4.get("green")),
            "verdict": g4.get("verdict"),
            "evidencePath": g4.get("evidencePath"),
            "blockers": list(g4.get("blockers") or []),
        },
        "dataset": {"seed": seed, "resetPolicy": "fresh compose project volumes per measured run"},
        "environment": {},
        "workload": {},
        "result": {
            "status": "INCOMPLETE",
            "reason": "run did not finish",
            "sampleCount": 0,
            "outcomes": {key: 0 for key in OUTCOMES},
            "latencyMs": {},
            "assertions": {},
        },
        "artifacts": {
            "runDirectory": None,
            "commands": [],
            "rawSamples": [],
            "finalState": [],
        },
        "limitations": [],
    }


def finalize_manifest(
    manifest: dict[str, Any],
    *,
    run_dir: Path,
    status: str,
    reason: str,
    samples: list[dict[str, Any]],
    latency_values: list[float],
    assertions: dict[str, Any],
    raw_files: list[Path],
    final_state_files: list[Path],
    commands: list[str],
    extra_workload: dict[str, Any] | None = None,
    limitations: list[str] | None = None,
    raw_retained_locally: bool = False,
) -> dict[str, Any]:
    latencies = {
        "p50": percentile_nearest_rank(latency_values, 50),
        "p95": percentile_nearest_rank(latency_values, 95),
        "count": len(latency_values),
    }
    manifest["result"] = {
        "status": status,
        "reason": reason,
        "sampleCount": len(samples),
        "outcomes": outcome_summary(samples),
        "latencyMs": latencies,
        "assertions": assertions,
    }
    if extra_workload:
        manifest["workload"].update(extra_workload)
    manifest["artifacts"] = {
        "runDirectory": run_directory_reference(run_dir),
        "commands": commands,
        "rawSamples": [str(path.relative_to(run_dir)).replace("\\", "/") for path in raw_files],
        "finalState": [str(path.relative_to(run_dir)).replace("\\", "/") for path in final_state_files],
        "rawRetainedLocally": raw_retained_locally,
    }
    if limitations:
        manifest["limitations"] = limitations
    write_json(run_dir / "manifest.json", manifest)
    return manifest


def validate_manifest(manifest: dict[str, Any], run_dir: Path | None = None) -> list[str]:
    errors: list[str] = []

    def check_numbers(value: Any, path: str = "manifest") -> None:
        if isinstance(value, float) and not math.isfinite(value):
            errors.append(f"{path} contains a non-finite number")
        elif isinstance(value, dict):
            for key, item in value.items():
                check_numbers(item, f"{path}.{key}")
        elif isinstance(value, list):
            for index, item in enumerate(value):
                check_numbers(item, f"{path}[{index}]")

    check_numbers(manifest)

    def require(condition: bool, message: str) -> None:
        if not condition:
            errors.append(message)

    require(manifest.get("schemaVersion") == 1, "schemaVersion must be 1")
    experiment = manifest.get("experiment")
    require(experiment in EXPERIMENTS, f"experiment must be one of {EXPERIMENTS}")
    run_id = manifest.get("runId")
    require(isinstance(run_id, str) and bool(RUN_ID_RE.match(run_id)), "runId has the wrong shape")
    require(isinstance(manifest.get("reportGrade"), bool), "reportGrade must be boolean")
    require(isinstance(manifest.get("smoke"), bool), "smoke must be boolean")

    source = manifest.get("source") or {}
    for key in ("commit", "tree", "branch", "remote", "statusDigest"):
        require(isinstance(source.get(key), str) and source.get(key) != "", f"source.{key} is missing")
    for key in ("commit", "tree"):
        value = source.get(key)
        require(isinstance(value, str) and re.fullmatch(r"[0-9a-f]{40}", value) is not None,
                f"source.{key} must be a 40-character lowercase hex object id")
    require(isinstance(source.get("dirty"), bool), "source.dirty must be boolean")

    g4 = manifest.get("g4") or {}
    require(isinstance(g4.get("green"), bool), "g4.green must be boolean")
    require(isinstance(g4.get("blockers"), list), "g4.blockers must be a list")
    if manifest.get("reportGrade") and not g4.get("green"):
        errors.append("reportGrade=true requires a green G4 gate")
    if manifest.get("reportGrade") and source.get("dirty") is True:
        errors.append("reportGrade=true requires a clean source tree")

    environment = manifest.get("environment") or {}
    require(bool(environment.get("capturedAtUtc")), "environment.capturedAtUtc is missing")
    require(isinstance(environment.get("configured"), dict), "environment.configured is missing")
    require(isinstance(environment.get("observed"), dict), "environment.observed is missing")
    require(bool(environment.get("host")), "environment.host is missing")
    require(bool(environment.get("docker")), "environment.docker is missing")

    workload = manifest.get("workload") or {}
    require(bool(workload.get("model")), "workload.model is missing")
    require(isinstance(workload.get("configured"), dict), "workload.configured is missing")
    require(isinstance(workload.get("achieved"), dict), "workload.achieved is missing")
    require(isinstance(manifest.get("dataset", {}).get("seed"), int), "dataset.seed must be an integer")

    result = manifest.get("result") or {}
    status = result.get("status")
    if manifest.get("reportGrade") and status == "PASS":
        observed = environment.get("observed") or {}
        containers = observed.get("containers") or []
        configured_services = (environment.get("configured") or {}).get("composeServices") or {}
        required_services = {
            "E01": {"postgres", "rabbitmq", "identity", "driver"},
            "E02": {"postgres", "redis", "rabbitmq", "identity", "driver", "matching", "trip", "payment", "gateway"},
            "E03": {"postgres", "redis", "rabbitmq", "matching"},
        }.get(experiment, set()) | {"prometheus", "otel-collector", "jaeger"}
        observed_services = {item.get("service") for item in containers if item.get("state") == "running"}
        require(required_services <= observed_services, "required measured services are missing or not running")
        for item in containers:
            name = item.get("service")
            if name not in required_services:
                continue
            expected = configured_services.get(name) or {}
            actual = item.get("inspect") or {}
            require(bool(actual.get("image")), f"observed image identity is missing: {name}")
            require(expected.get("cpus") is not None and actual.get("nanoCpus") == int(float(expected["cpus"]) * 1_000_000_000),
                    f"observed CPU limit differs from configured: {name}")
            require(expected.get("memLimitBytes") is not None and actual.get("memoryBytes") == int(expected["memLimitBytes"]),
                    f"observed memory limit differs from configured: {name}")
    require(status in ("PASS", "FAIL", "BLOCKED", "INVALID", "INCOMPLETE"), "result.status is invalid")
    require(isinstance(result.get("sampleCount"), int) and result.get("sampleCount", -1) >= 0,
            "result.sampleCount must be a non-negative integer")
    if status == "PASS":
        require(int(result.get("sampleCount") or 0) > 0,
                "result.status=PASS requires at least one sample")
    outcomes = result.get("outcomes") or {}
    if isinstance(result.get("sampleCount"), int) and result["sampleCount"] > 0:
        require(isinstance(outcomes, dict), "result.outcomes is missing")
        if isinstance(outcomes, dict):
            require(all(key in outcomes for key in OUTCOMES), "result.outcomes lacks a required class")
            if all(key in outcomes for key in OUTCOMES):
                require(sum(int(outcomes[key]) for key in OUTCOMES) == result["sampleCount"],
                        "outcome counts do not sum to sampleCount")

    artifacts = manifest.get("artifacts") or {}
    require(isinstance(artifacts.get("commands"), list), "artifacts.commands must be a list")
    require(isinstance(artifacts.get("rawSamples"), list), "artifacts.rawSamples must be a list")
    require(isinstance(artifacts.get("finalState"), list), "artifacts.finalState must be a list")
    raw_retained_locally = bool(artifacts.get("rawRetainedLocally", False))
    if run_dir is not None:
        recorded = artifacts.get("runDirectory")
        require(
            isinstance(recorded, str) and resolve_run_directory(recorded) == run_dir.resolve(),
            "artifacts.runDirectory does not match the run directory",
        )
        for relative in artifacts.get("finalState", []):
            require((run_dir / relative).exists(), f"artifact listed in manifest is missing: {relative}")
        if not raw_retained_locally:
            for relative in artifacts.get("rawSamples", []):
                require((run_dir / relative).exists(), f"artifact listed in manifest is missing: {relative}")
    return errors


def load_run(run_dir: Path) -> tuple[dict[str, Any] | None, list[str]]:
    manifest_path = run_dir / "manifest.json"
    if not manifest_path.exists():
        return None, [f"missing manifest: {manifest_path}"]
    try:
        manifest = read_json(manifest_path)
    except Exception as exc:  # noqa: BLE001 - validation
        return None, [f"unreadable manifest {manifest_path}: {exc}"]
    return manifest, validate_manifest(manifest, run_dir)


def campaign_runs(experiment: str, runs_root: Path | None = None) -> list[Path]:
    root = (runs_root or RUNS_ROOT) / experiment
    if not root.exists():
        return []
    return sorted(path for path in root.iterdir() if path.is_dir() and path.name.startswith(f"W13-{experiment}-"))


def container_path(path: Path) -> str:
    try:
        return path.resolve().relative_to(ROOT).as_posix()
    except ValueError as exc:
        raise HarnessError(
            f"path {path} is outside the repository and cannot be mounted into the k6 container"
        ) from exc


def run_directory_reference(run_dir: Path) -> str:
    try:
        return run_dir.resolve().relative_to(ROOT).as_posix()
    except ValueError:
        return str(run_dir)


def resolve_run_directory(reference: str) -> Path:
    path = Path(reference)
    return path.resolve() if path.is_absolute() else (ROOT / path).resolve()


def environment_fingerprint(environment: dict[str, Any]) -> str:
    """Fingerprint configured variables that must be identical across repeats."""
    configured = environment.get("configured") or {}
    services = configured.get("composeServices") or {}
    keep = {}
    for name in sorted(services):
        service = services.get(name) or {}
        keep[name] = {
            "image": service.get("image"),
            "cpus": service.get("cpus"),
            "memLimitBytes": service.get("memLimitBytes"),
            "environment": service.get("environment"),
        }
    observed_images: dict[str, set[str]] = {}
    for container in (environment.get("observed") or {}).get("containers") or []:
        image = (container.get("inspect") or {}).get("image")
        if image:
            observed_images.setdefault(container.get("service"), set()).add(image)
    host = environment.get("host") or {}
    frozen = {"configured": keep, "images": {name: sorted(images) for name, images in observed_images.items()},
              "host": {name: host.get(name) for name in ("hostId", "platform", "cpuModel", "logicalCpus", "memoryBytes")}}
    return sha256_text(json.dumps(frozen, sort_keys=True))


def print_json(data: Any) -> None:
    json.dump(data, sys.stdout, indent=2)
    sys.stdout.write("\n")
