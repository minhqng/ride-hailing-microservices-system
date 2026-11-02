"""PERF/E02 — controlled offered-load API baseline.

Canonical surface: the authenticated production `POST /trips` route through
the Gateway (or a configured trip host for diagnostics). The harness refuses to
substitute a Testing-only or unauthenticated demo endpoint.

The preflight is an executable readiness boundary, not a fake success path:
- no bearer token -> BLOCKED / AUTH_TOKEN_UNAVAILABLE
- token rejected -> BLOCKED / SHARED_AUTH_NOT_READY
- route absent -> BLOCKED / PRODUCTION_ROUTE_UNAVAILABLE
- clean authenticated request -> k6 constant-arrival-rate run

k6 itself is the open-model load generator; this module only orchestrates,
records the environment and classifies the summary into the canonical outcome
classes (success, domain conflict, system error, timeout, unexpected).
"""

from __future__ import annotations

import argparse
import json
import math
import os
import shutil
import tempfile
import time
from pathlib import Path
from typing import Any

import w13lib as lib
import w13_auth as auth
import w13_metrics

DEFAULT_ENDPOINT = "/trips"


def _read_tokens(path: Path) -> list[str]:
    if not path.is_file():
        return []
    return [line.strip() for line in path.read_text(encoding="utf-8").splitlines() if line.strip()]


def _duration_seconds(value: str) -> int:
    text = value.strip()
    units = {"s": 1, "m": 60, "h": 3600}
    if text and text[-1] in units:
        return int(float(text[:-1]) * units[text[-1]])
    return int(float(text))


def preflight(base: str, endpoint: str, token: str, *, timeout: float = 20) -> dict[str, Any]:
    result: dict[str, Any] = {"baseUrl": base, "endpoint": endpoint, "ready": False, "checks": []}
    parsed = base.rstrip("/")
    healthy = False
    for path in ("/health/ready", "/health/live"):
        try:
            status, _, body, _ = lib.http_request("GET", parsed + path, timeout=5)
        except Exception as exc:  # noqa: BLE001 - readiness probe
            result["checks"].append({"probe": path, "error": f"{type(exc).__name__}: {exc}"})
            continue
        result["checks"].append({"probe": path, "status": status})
        if status == 200:
            healthy = True
            break
    if not healthy:
        result["reason"] = "SERVICE_UNREACHABLE"
        return result

    body = {
        "pickup": {"latitude": 10.7769, "longitude": 106.7009},
        "dropoff": {"latitude": 10.7710, "longitude": 106.6950},
    }
    headers = {
        "Authorization": f"Bearer {token}",
        "Content-Type": "application/json",
        "Accept": "application/json",
        "Idempotency-Key": f"w13-e02-preflight-{lib.utc_stamp()}",
    }
    try:
        status, _, payload, elapsed = lib.http_request(
            "POST", parsed + endpoint, body=body, headers=headers, timeout=timeout
        )
    except Exception as exc:  # noqa: BLE001 - readiness probe
        result["reason"] = "PREFLIGHT_TRANSPORT_FAILURE"
        result["detail"] = f"{type(exc).__name__}: {exc}"
        return result
    parsed_body = lib.parse_json(payload)
    code = parsed_body.get("code") if isinstance(parsed_body, dict) else None
    result.update(
        {
            "status": status,
            "code": code,
            "elapsedMs": elapsed,
            "bodySnippet": payload[:300],
            "preflightTripId": parsed_body.get("tripId") if isinstance(parsed_body, dict) else None,
        }
    )
    if status in (200, 201):
        if not isinstance(parsed_body, dict) or not parsed_body.get("tripId"):
            result["reason"] = "NONCANONICAL_RESPONSE_SHAPE"
            return result
        result["ready"] = True
        return result
    if status == 409 and code in ("DOMAIN_CONFLICT", "IDEMPOTENCY_CONFLICT"):
        result["reason"] = "PREFLIGHT_DOMAIN_CONFLICT"
        return result
    if status in (401, 403):
        result["reason"] = "SHARED_AUTH_NOT_READY"
        return result
    if status == 404:
        result["reason"] = "PRODUCTION_ROUTE_UNAVAILABLE"
        return result
    if status == 429 or status >= 500:
        result["reason"] = "SUT_NOT_READY"
        return result
    result["reason"] = "UNEXPECTED_PREFLIGHT_STATUS"
    return result


def _metric(summary: dict[str, Any], name: str, field: str = "count", default: float = 0) -> float:
    metric = (summary.get("metrics") or {}).get(name) or {}
    values = metric.get("values", metric)
    if field in values and values[field] is not None:
        value = values[field]
        if not isinstance(value, (int, float)) or not math.isfinite(value):
            raise lib.HarnessError(f"Invalid k6 metric {name}.{field}")
        return value
    return default


def _ensure_k6_image() -> dict[str, Any]:
    inspect = lib.docker("image", "inspect", lib.K6_IMAGE, "--format", "{{json .}}", timeout=30)
    pulled = False
    if inspect.returncode != 0:
        pull = lib.docker("pull", lib.K6_IMAGE, timeout=300)
        pulled = True
        if pull.returncode != 0:
            raise lib.HarnessError(f"k6 image pull failed: {pull.stderr[-2000:]}")
        inspect = lib.docker("image", "inspect", lib.K6_IMAGE, "--format", "{{json .}}", timeout=30)
    if inspect.returncode != 0:
        raise lib.HarnessError(f"k6 image {lib.K6_IMAGE} is unavailable")
    data = json.loads(inspect.stdout)
    return {"reference": lib.K6_IMAGE, "pulled": pulled, "id": data.get("Id"),
            "repoDigests": data.get("RepoDigests", [])}


def _network_name() -> str:
    return f"{lib.PROJECT}_ride-net"


def _trip_final_state(started_at: str) -> dict[str, Any]:
    try:
        return {
            "tripCount": lib.psql_scalar(
                "trip_db",
                "SELECT count(*)::text FROM trips "
                f"WHERE created_at >= '{started_at}'::timestamptz;",
            ),
            "e02IdempotencyKeys": lib.psql_scalar(
                "trip_db",
                "SELECT count(*)::text FROM request_idempotency "
                "WHERE key LIKE 'w13-e02-%' AND key NOT LIKE 'w13-e02-preflight-%';",
            ),
            "activeTripsPerRiderViolations": lib.psql_scalar(
                "trip_db",
                "SELECT count(*)::text FROM ("
                "SELECT rider_id, count(*) FROM trips "
                "WHERE state NOT IN ('COMPLETED','CANCELLED','MATCH_FAILED') "
                "GROUP BY rider_id HAVING count(*) > 1) violations;",
            ),
            "duplicateIdempotencyResources": lib.psql_scalar(
                "trip_db",
                "SELECT count(*)::text FROM ("
                "SELECT resource_id, count(*) FROM request_idempotency "
                "GROUP BY resource_id HAVING count(*) > 1) duplicates;",
            ),
        }
    except Exception as exc:  # noqa: BLE001 - final state is recorded, not hidden
        return {"error": f"{type(exc).__name__}: {exc}"}


def _write_blocked(args: argparse.Namespace, *, reason: str, detail: Any, token_count: int,
                   commands: list[str]) -> int:
    run_id = lib.new_run_id("E02", args.seed)
    run_dir = lib.RUNS_ROOT / "E02" / run_id
    run_dir.mkdir(parents=True, exist_ok=True)
    commands_file = run_dir / "commands.txt"
    lib.write_text(commands_file, "\n".join(commands) + "\n")
    g4 = lib.g4_readiness()
    manifest = lib.manifest_base(
        experiment="E02", run_id=run_id, report_grade=args.report_grade,
        smoke=not args.report_grade, g4=g4, seed=args.seed,
    )
    manifest["environment"] = lib.capture_environment(include_observed=True)
    manifest["workload"] = {
        "model": "k6 constant-arrival-rate (open model) against the authenticated POST /trips surface",
        "configured": {
            "configuredRatePerSecond": args.rate,
            "duration": args.duration,
            "executor": "constant-arrival-rate",
            "endpoint": args.endpoint,
            "preflightBaseUrl": args.preflight_base_url,
            "k6BaseUrl": args.k6_base_url,
            "preAllocatedVUs": args.prealloc_vus,
            "maxVUs": args.max_vus,
            "tokenCount": token_count,
            "warmupSeconds": 0,
            "cooldownSeconds": 0,
        },
        "achieved": {},
        "warmupSeconds": 0,
        "measurementSeconds": 0,
        "cooldownSeconds": 0,
        "datasetSeed": args.seed,
    }
    lib.finalize_manifest(
        manifest, run_dir=run_dir, status="BLOCKED", reason=reason,
        samples=[], latency_values=[], assertions={"blockers": [reason], "detail": detail},
        raw_files=[commands_file], final_state_files=[], commands=commands,
        extra_workload={"blockedBeforeLoad": True},
        limitations=[
            "BLOCKED means no report-grade measurement was attempted; it is not a performance result.",
            "E02 requires real Identity Rider grants and the canonical authenticated Gateway route.",
        ],
    )
    lib.print_json({"status": "BLOCKED", "runId": run_id, "reason": reason, "detail": detail})
    return 3


def _prepare_stack(*, reset: bool) -> None:
    if reset:
        lib.compose_down(volumes=True)
    auth.reset()
    lib.compose_up(["postgres", "redis", "rabbitmq", "identity", "driver", "matching", "trip", "payment", "gateway"],
                   build=True, wait_timeout=240)
    lib.wait_http("http://127.0.0.1:" + os.environ.get("W13_TRIP_PORT", "18103") + "/health/ready")


def _no_token_probe(base: str, endpoint: str) -> dict[str, Any]:
    """Record whether the canonical mutation is reachable without authentication."""
    body = {
        "pickup": {"latitude": 10.7769, "longitude": 106.7009},
        "dropoff": {"latitude": 10.7710, "longitude": 106.6950},
    }
    headers = {
        "Content-Type": "application/json",
        "Accept": "application/json",
        "Idempotency-Key": f"w13-e02-noauth-{lib.utc_stamp()}",
    }
    try:
        status, _, payload, elapsed = lib.http_request(
            "POST", base.rstrip("/") + endpoint, body=body, headers=headers, timeout=15
        )
    except Exception as exc:  # noqa: BLE001 - readiness probe
        return {"reachable": False, "error": f"{type(exc).__name__}: {exc}"}
    return {
        "reachable": True,
        "status": status,
        "elapsedMs": elapsed,
        "bodySnippet": payload[:300],
        "unauthenticatedMutationRejected": status in (401, 403),
        "unauthenticatedMutationAccepted": status in (200, 201),
    }


def _target_error(args: argparse.Namespace) -> str | None:
    if args.endpoint != "/trips":
        return "NONCANONICAL_ENDPOINT"
    if args.report_grade:
        gateway_port = os.environ.get("W13_GATEWAY_PORT", "18080")
        if args.preflight_base_url.rstrip("/") != f"http://127.0.0.1:{gateway_port}":
            return "NONCANONICAL_PREFLIGHT_TARGET"
        if args.k6_base_url.rstrip("/") != "http://gateway:8080":
            return "NONCANONICAL_K6_TARGET"
    return None


def _write_token_file(tokens: list[str], run_dir: Path) -> Path:
    directory = lib.ROOT / "plans" / ".w13-credentials"
    directory.mkdir(parents=True, exist_ok=True)
    descriptor, name = tempfile.mkstemp(prefix="riders-", suffix=".txt", dir=directory)
    os.close(descriptor)
    path = Path(name)
    path.write_text("\n".join(tokens) + "\n", encoding="utf-8", newline="\n")
    return path


def _trend_latency(summary: dict[str, Any], metric_name: str) -> dict[str, Any]:
    metric = (summary.get("metrics") or {}).get(metric_name) or {}
    values = metric.get("values", metric)
    return {
        "p50": values.get("p(50)"),
        "p95": values.get("p(95)"),
        "count": values.get("count"),
    }


def _trip_final_checks(final_state: dict[str, Any], success_count: int) -> dict[str, Any]:
    if not isinstance(final_state, dict) or not final_state or final_state.get("error"):
        return {"finalStateAvailable": False}
    required = ("tripCount", "e02IdempotencyKeys", "activeTripsPerRiderViolations",
                "duplicateIdempotencyResources")
    if any(final_state.get(key) is None for key in required):
        return {"finalStateAvailable": False}

    def as_int(value: Any) -> int | None:
        try:
            return int(value)
        except (TypeError, ValueError):
            return None

    trips = as_int(final_state.get("tripCount"))
    idempotency = as_int(final_state.get("e02IdempotencyKeys"))
    return {
        "finalStateAvailable": True,
        "activeTripsPerRiderViolationsZero": str(final_state.get("activeTripsPerRiderViolations")) == "0",
        "duplicateIdempotencyResourcesZero": str(final_state.get("duplicateIdempotencyResources")) == "0",
        "tripCountMatchesSuccesses": trips is not None and trips == success_count,
        "idempotencyCountMatchesSuccesses": idempotency is not None and idempotency == success_count,
    }


def run(args: argparse.Namespace) -> int:
    g4 = lib.g4_readiness()
    if args.report_grade:
        try:
            lib.ensure_report_grade(g4, lib.source_binding())
        except lib.GateError as exc:
            lib.print_json({"status": "BLOCKED", "reason": str(exc), "g4": g4})
            return 3
        if args.repeats < 3:
            lib.print_json({"status": "INVALID", "reason": "report-grade E02 requires at least 3 repeats"})
            return 2

    target_error = _target_error(args)
    if target_error:
        lib.print_json({"status": "BLOCKED", "reason": target_error})
        return 3
    token_source = args.token_file or os.environ.get("W13_E02_TOKEN_FILE", "")
    token_path = Path(token_source).expanduser() if token_source else Path(os.devnull)
    tokens = _read_tokens(token_path) if token_source else []
    commands = [
        f"python scripts/perf/w13.py e02 --preflight-base-url {args.preflight_base_url} "
        f"--k6-base-url {args.k6_base_url} --endpoint {args.endpoint} --rate {args.rate} "
        f"--duration {args.duration} --seed {args.seed} --mode {'report' if args.report_grade else 'smoke'}"
    ]
    try:
        _prepare_stack(reset=args.reset_each)
    except Exception as exc:  # noqa: BLE001 - stack setup
        return _write_blocked(
            args, reason="STACK_START_FAILED", detail=str(exc), token_count=len(tokens), commands=commands
        )
    no_token_probe = _no_token_probe(args.preflight_base_url, args.endpoint)
    trip_base = args.trip_base_url or f"http://127.0.0.1:{os.environ.get('W13_TRIP_PORT', '18103')}"
    trip_probe = _no_token_probe(trip_base, args.endpoint)
    if not token_source:
        tokens = [auth.account("Rider")[1] for _ in range(args.riders + 1)]
    if not tokens:
        return _write_blocked(
            args, reason="AUTH_TOKEN_UNAVAILABLE",
            detail={
                "message": "No W13_E02_TOKEN_FILE / --token-file with a bearer token is configured.",
                "gatewayProbe": no_token_probe,
                "tripDirectProbe": trip_probe,
            },
            token_count=0, commands=commands,
        )
    if not no_token_probe.get("unauthenticatedMutationRejected"):
        return _write_blocked(
            args, reason="AUTH_NOT_ENFORCED_AT_TARGET",
            detail={"gatewayProbe": no_token_probe, "tripDirectProbe": trip_probe},
            token_count=len(tokens), commands=commands,
        )
    if args.report_grade and len(tokens) < 2:
        return _write_blocked(
            args, reason="TOKEN_POOL_INSUFFICIENT",
            detail="report-grade E02 requires at least two rider tokens: one preflight token is "
                   "excluded so the active-Trip guard does not turn the measured workload into all-conflict.",
            token_count=len(tokens), commands=commands,
        )
    preflight_token = tokens[-1]
    load_tokens = tokens[:-1] if len(tokens) > 1 else tokens

    try:
        k6_image = _ensure_k6_image()
    except Exception as exc:  # noqa: BLE001 - harness setup
        return _write_blocked(
            args, reason="K6_UNAVAILABLE", detail=str(exc), token_count=len(tokens), commands=commands
        )
    network = _network_name()
    network_check = lib.docker("network", "inspect", network, timeout=30)
    if network_check.returncode != 0:
        return _write_blocked(
            args, reason="COMPOSE_NETWORK_MISSING",
            detail=f"network {network} is not present; start the W13 compose environment first",
            token_count=len(tokens), commands=commands,
        )

    seed = args.seed
    results = []
    for repeat in range(args.repeats):
        if repeat > 0:
            try:
                _prepare_stack(reset=True)
            except Exception as exc:  # noqa: BLE001 - stack setup
                return _write_blocked(
                    args, reason="STACK_START_FAILED", detail=str(exc),
                    token_count=len(tokens), commands=commands,
                )
            if not token_source:
                tokens = [auth.account("Rider")[1] for _ in range(args.riders + 1)]
                preflight_token, load_tokens = tokens[-1], tokens[:-1]
        environment = lib.capture_environment()
        pre = preflight(args.preflight_base_url, args.endpoint, preflight_token, timeout=args.preflight_timeout)
        if not pre.get("ready"):
            return _write_blocked(
                args, reason=str(pre.get("reason") or "PREFLIGHT_FAILED"), detail=pre,
                token_count=len(tokens), commands=commands,
            )
        run_id = lib.new_run_id("E02", seed + repeat)
        run_dir = lib.RUNS_ROOT / "E02" / run_id
        raw_dir = run_dir / "raw"
        raw_dir.mkdir(parents=True, exist_ok=True)
        commands_file = run_dir / "commands.txt"
        lib.write_text(commands_file, "\n".join(commands) + "\n")
        mount_root = lib.ROOT.as_posix()
        k6_token_path = _write_token_file(load_tokens, run_dir)
        relative_token = lib.container_path(k6_token_path)
        run_relative = lib.container_path(run_dir)
        summary_rel = f"{run_relative}/raw/k6-summary.json"
        points_rel = f"{run_relative}/raw/k6-points.json"
        console_file = raw_dir / "k6-console.txt"
        k6_command = [
            "docker", "run", "--rm",
            "--network", network,
            "-v", f"{mount_root}:/work",
            "-w", "/work",
            "-e", f"K6_TOKEN_FILE=/work/{relative_token}",
            "-e", f"K6_BASE_URL={args.k6_base_url}",
            "-e", f"K6_ENDPOINT={args.endpoint}",
            "-e", f"K6_RATE={args.rate}",
            "-e", f"W13_MEASUREMENT_DURATION={args.duration}",
            "-e", f"K6_PREALLOC_VUS={args.prealloc_vus}",
            "-e", f"K6_MAX_VUS={args.max_vus}",
            "-e", f"K6_SEED={seed + repeat}",
            lib.K6_IMAGE, "run",
            "--summary-export", f"/work/{summary_rel}",
            "--out", f"json=/work/{points_rel}",
            "/work/scripts/perf/k6/e02-create-ride.js",
        ]
        started = time.perf_counter()
        started_iso = lib.iso()
        try:
            k6_result = lib.run(k6_command, timeout=_duration_seconds(args.duration) + 240)
        finally:
            k6_token_path.unlink(missing_ok=True)
        elapsed = time.perf_counter() - started
        lib.write_text(console_file, k6_result.stdout + "\n--- stderr ---\n" + k6_result.stderr)
        summary_path = run_dir / "raw" / "k6-summary.json"
        summary = lib.read_json(summary_path) if summary_path.exists() else None
        final_state_path = run_dir / "final-state" / "e02-trip-state.json"
        final_state = _trip_final_state(started_iso) if summary is not None else {}
        lib.write_json(final_state_path, {
            "synthetic": False,
            "reportGrade": args.report_grade,
            "startedAtUtc": started_iso,
            "finalState": final_state,
        })

        if summary is None:
            status, reason = "BLOCKED", "K6_SUMMARY_MISSING"
            samples: list[dict[str, Any]] = []
            latencies: list[float] = []
            assertions = {"k6SummaryPresent": False}
            achieved: dict[str, Any] = {}
        else:
            success = int(_metric(summary, "w13_success"))
            domain_conflicts = int(_metric(summary, "w13_domain_conflicts"))
            auth_failures = int(_metric(summary, "w13_auth_failures"))
            system_errors = int(_metric(summary, "w13_system_errors"))
            unexpected = int(_metric(summary, "w13_unexpected"))
            iterations = int(_metric(summary, "iterations"))
            sample_count = success + domain_conflicts + auth_failures + system_errors + unexpected
            durations = (summary.get("metrics") or {}).get("http_req_duration") or {}
            values = durations.get("values", durations)
            http_requests = int(_metric(summary, "http_reqs"))
            latencies = []
            latency_by_outcome = {
                "success": _trend_latency(summary, "w13_latency_success_ms"),
                "domainConflict": _trend_latency(summary, "w13_latency_domain_conflict_ms"),
                "authFailure": _trend_latency(summary, "w13_latency_auth_failure_ms"),
                "systemError": _trend_latency(summary, "w13_latency_system_error_ms"),
                "unexpected": _trend_latency(summary, "w13_latency_unexpected_ms"),
            }
            samples = (
                [{"outcome": "success"}] * success
                + [{"outcome": "domainConflict"}] * domain_conflicts
                + [{"outcome": "systemError"}] * system_errors
                + [{"outcome": "unexpected"}] * auth_failures
                + [{"outcome": "unexpected"}] * unexpected
            )
            achieved = {
                "configuredRatePerSecond": args.rate,
                "achievedRatePerSecond": _metric(summary, "iterations", "rate"),
                "iterations": iterations,
                "httpRequests": http_requests,
                "successCount": success,
                "domainConflictCount": domain_conflicts,
                "systemErrorCount": system_errors,
                "authFailureCount": auth_failures,
                "unexpectedCount": unexpected,
                "sampleCount": sample_count,
                "droppedIterations": int(_metric(summary, "dropped_iterations")),
                "vusMax": _metric(summary, "vus_max", "value"),
                "vus": _metric(summary, "vus", "value"),
                "p50Ms": values.get("p(50)"),
                "p95Ms": values.get("p(95)"),
                "latencyCount": http_requests,
                "latencyByOutcome": latency_by_outcome,
                "elapsedSeconds": elapsed,
            }
            final_checks = _trip_final_checks(final_state, success)
            assertions = {
                "k6SummaryPresent": True,
                "k6ExitZero": k6_result.returncode == 0,
                "preflightReady": True,
                "sampleCountMatchesIterations": sample_count == iterations,
                "zeroAuthFailures": auth_failures == 0,
                "zeroUnexpected": unexpected == 0,
                "successesObserved": success > 0,
                "systemErrorsZero": system_errors == 0,
                "notAllDomainConflicts": not (iterations > 0 and domain_conflicts == iterations),
                **final_checks,
            }
            if auth_failures > 0:
                status, reason = "BLOCKED", "AUTH_FAILURE_DURING_LOAD"
            elif unexpected > 0 or sample_count != iterations:
                status, reason = "FAIL", "UNEXPECTED_OUTCOME_OR_SAMPLE_MISMATCH"
            elif k6_result.returncode != 0:
                status, reason = "FAIL", "K6_PROCESS_FAILED"
            elif success == 0:
                status, reason = "FAIL", "NO_SUCCESSFUL_REQUESTS"
            elif system_errors > 0:
                status, reason = "FAIL", "SYSTEM_ERRORS_OBSERVED"
            elif iterations > 0 and domain_conflicts == iterations:
                status, reason = "FAIL", "ALL_REQUESTS_WERE_DOMAIN_CONFLICTS"
            elif not all(final_checks.values()):
                status, reason = "FAIL", "AUTHORITATIVE_FINAL_STATE_ASSERTION_FAILED"
            else:
                status, reason = "PASS", "k6 open-model run completed with authoritative final-state agreement"
        after = lib.capture_environment(include_observed=True)
        environment_after = {
            "capturedAtUtc": after.get("capturedAtUtc"),
            "observed": after.get("observed"),
            "runtimeStats": lib.container_stats(),
        }
        manifest = lib.manifest_base(
            experiment="E02", run_id=run_id, report_grade=args.report_grade,
            smoke=not args.report_grade, g4=g4, seed=seed + repeat,
        )
        manifest["environment"] = environment
        manifest["environment"]["observedAfter"] = environment_after
        manifest["environment"]["k6"] = k6_image
        manifest["workload"] = {
            "model": "k6 constant-arrival-rate (open model) against the authenticated POST /trips surface",
            "configured": {
                "configuredRatePerSecond": args.rate,
                "duration": args.duration,
                "executor": "constant-arrival-rate",
                "endpoint": args.endpoint,
                "preflightBaseUrl": args.preflight_base_url,
                "k6BaseUrl": args.k6_base_url,
                "preAllocatedVUs": args.prealloc_vus,
                "maxVUs": args.max_vus,
                "tokenCount": len(tokens),
                "loadTokenCount": len(load_tokens),
                "preflightTokenExcludedFromLoad": len(tokens) > 1,
                "unauthenticatedProbe": no_token_probe,
                "reportGradeTargetCanonical": args.report_grade,
                "warmupSeconds": 0,
                "cooldownSeconds": 0,
                "preflight": pre,
            },
            "achieved": achieved,
            "warmupSeconds": 0,
            "measurementSeconds": elapsed,
            "cooldownSeconds": 0,
            "datasetSeed": seed + repeat,
        }
        metrics_ok, metric_file = w13_metrics.capture(run_dir, "E02", start=started_iso)
        assertions["declaredMetricsPresentAndFinite"] = metrics_ok
        if not metrics_ok:
            status, reason = "FAIL", "DECLARED_METRICS_MISSING_OR_INVALID"
        lib.finalize_manifest(
            manifest, run_dir=run_dir, status=status, reason=reason,
            samples=samples, latency_values=latencies, assertions=assertions,
            raw_files=[summary_path, commands_file, metric_file] if summary_path.exists() else [commands_file, metric_file],
            final_state_files=[final_state_path],
            commands=commands,
            extra_workload={},
            limitations=[
                "No thresholds were invented before a baseline; the analyzer reports configured vs achieved load and outcome classes.",
                "Domain conflicts are expected when the Rider active-Trip guard rejects a repeated Rider; they are not system failures.",
                "Real Identity Rider accounts authenticate through Gateway. The finite Rider cohort and active-Trip conflicts are part of the declared workload; offered request rate is not business completion throughput.",
                "k6 JSON points and console output remain local; the summary export is the curated input.",
            ],
            raw_retained_locally=True,
        )
        if summary is not None:
            manifest["result"]["latencyMs"] = {
                "p50": achieved.get("p50Ms"),
                "p95": achieved.get("p95Ms"),
                "count": achieved.get("latencyCount"),
            }
            lib.write_json(run_dir / "manifest.json", manifest)
        results.append({"runId": run_id, "status": status, "achieved": achieved})
    summary_status = "PASS" if results and all(r["status"] == "PASS" for r in results) else "FAIL"
    lib.print_json({"status": summary_status, "runs": results})
    return 0 if summary_status == "PASS" else 2


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description="PERF/E02 controlled offered-load harness")
    parser.add_argument("--preflight-base-url", default="http://127.0.0.1:18080")
    parser.add_argument("--trip-base-url", default=None,
                        help="diagnostic trip-direct base URL for the unauthenticated-readiness probe")
    parser.add_argument("--k6-base-url", default="http://gateway:8080")
    parser.add_argument("--endpoint", default=DEFAULT_ENDPOINT)
    parser.add_argument("--token-file", default=None)
    parser.add_argument("--riders", type=int, choices=range(2, 21), default=10,
                        help="real disposable Rider cohort; token issuance is outside the measured interval")
    parser.add_argument("--rate", type=int, default=10)
    parser.add_argument("--duration", default="30s")
    parser.add_argument("--prealloc-vus", type=int, default=30)
    parser.add_argument("--max-vus", type=int, default=80)
    parser.add_argument("--preflight-timeout", type=float, default=20.0)
    parser.add_argument("--seed", type=int, default=1302)
    parser.add_argument("--repeats", type=int, default=1)
    parser.add_argument("--reset-each", action=argparse.BooleanOptionalAction, default=True,
                        help="destroy and recreate the disposable W13 volumes before each repeat")
    parser.add_argument("--mode", choices=("smoke", "report"), default="smoke")
    return parser


def main(argv: list[str] | None = None) -> int:
    parser = build_parser()
    args = parser.parse_args(argv)
    args.report_grade = args.mode == "report"
    return run(args)


if __name__ == "__main__":
    raise SystemExit(main())
