"""PERF/E03 — asynchronous consumer scaling.

Independent variable: Matching intake consumer replicas (1, 2, 4).
Controlled variables: prefetch, DB pool, queue topology, arrival rate, payload
shape/size, CPU/memory limits and the autonomous worker mode.

The autonomous Matching worker is disabled by default in this experiment so
the measurement isolates the intake consumer path. That is recorded as a
controlled variable, not hidden: enabling it would scale worker concurrency
together with consumer count and confound the independent variable.

Replica registration is proven from the broker (consumer count), and actual
work distribution is proven from each replica's own consumption log. Starting
extra containers is not accepted as proof by itself.
"""

from __future__ import annotations

import argparse
import json
import math
import os
import time
import urllib.parse
from collections import Counter
from pathlib import Path
from typing import Any

import w13lib as lib
import w13_metrics

DEFAULT_REPLICAS = [1, 2, 4]
QUEUE = "matching.ride-requested.q"
EXCHANGE = "ride.events"
ROUTING_KEY = "trip.ride-requested.v1"
LOG_MARKER = "Consumed RideRequestedV1"


def _compose_services(replicas: int, worker_mode: str) -> None:
    os.environ["W13_MATCHING_WORKER_ENABLED"] = "true" if worker_mode == "enabled" else "false"
    lib.compose_down(volumes=True)
    lib.compose_up(["postgres", "redis", "rabbitmq", "matching"], build=True, scale=replicas, wait_timeout=240)


def _matching_containers() -> list[dict[str, Any]]:
    result = lib.compose("ps", "-q", "matching", timeout=30)
    if result.returncode != 0:
        raise lib.HarnessError(f"compose ps matching failed: {result.stderr[-1000:]}")
    ids = [line.strip() for line in result.stdout.splitlines() if line.strip()]
    containers = []
    for container_id in ids:
        inspect = lib._container_inspect(container_id)
        containers.append(inspect or {"id": container_id})
    return containers


def _wait_consumers(replicas: int, *, timeout: float = 180) -> dict[str, Any]:
    encoded = urllib.parse.quote(QUEUE, safe="")
    deadline = time.monotonic() + timeout
    last: dict[str, Any] | None = None
    while time.monotonic() < deadline:
        try:
            queue = lib.rabbit_api(f"/api/queues/%2F/{encoded}")
            last = queue
            if int(queue.get("consumers") or 0) >= replicas:
                return queue
        except Exception:  # noqa: BLE001 - readiness probe
            pass
        time.sleep(1)
    raise lib.HarnessError(
        f"expected {replicas} consumers on {QUEUE}; observed {last.get('consumers') if last else 'none'}"
    )


def _queue_snapshot() -> dict[str, Any]:
    encoded = urllib.parse.quote(QUEUE, safe="")
    return lib.rabbit_api(f"/api/queues/%2F/{encoded}")


def _publish(envelope: dict[str, Any]) -> tuple[bool, str]:
    body = {
        "properties": {
            "delivery_mode": 2,
            "content_type": "application/json",
        },
        "routing_key": ROUTING_KEY,
        "payload": json.dumps(envelope),
        "payload_encoding": "string",
    }
    try:
        response = lib.rabbit_api(
            f"/api/exchanges/%2F/{urllib.parse.quote(EXCHANGE, safe='')}/publish",
            method="POST",
            body=body,
            timeout=10,
        )
    except Exception as exc:  # noqa: BLE001 - publish failure is a measurement result
        return False, f"{type(exc).__name__}: {exc}"
    routed = bool(response.get("routed")) if isinstance(response, dict) else False
    return routed, "" if routed else f"unrouted response: {response}"


def _envelope(seed: int, replica: int, repeat: int, sequence: int) -> dict[str, Any]:
    trip_id = lib.seeded_uuid(seed + repeat, f"e03-trip-{replica}-{sequence}")
    message_id = lib.seeded_uuid(seed + repeat, f"e03-message-{replica}-{sequence}")
    occurred = lib.iso()
    return {
        "messageId": message_id,
        "messageType": "RideRequestedV1",
        "schemaVersion": 1,
        "occurredAt": occurred,
        "correlationId": lib.seeded_uuid(seed + repeat, f"e03-correlation-{replica}-{sequence}"),
        "causationId": None,
        "aggregateType": "trip",
        "aggregateId": trip_id,
        "payload": {
            "tripId": trip_id,
            "matchingRound": 1,
            "pickup": {"latitude": 10.7769, "longitude": 106.7009},
            "dropoff": {"latitude": 10.7710, "longitude": 106.6950},
            "requestedAt": occurred,
        },
    }


def _publish_series(
    seed: int,
    replica: int,
    repeat: int,
    *,
    rate: float,
    count: int,
    duplicate_every: int,
    start_sequence: int = 0,
) -> tuple[list[dict[str, Any]], list[dict[str, Any]], float]:
    """Publish at a fixed offered rate; return (unique, deliveries, duration)."""
    unique: list[dict[str, Any]] = []
    deliveries: list[dict[str, Any]] = []
    interval = 1.0 / rate if rate > 0 else 0.0
    started = time.perf_counter()
    sequence = start_sequence
    for index in range(count):
        target = started + (index + 1) * interval
        now = time.perf_counter()
        if target > now:
            time.sleep(target - now)
        envelope = _envelope(seed, replica, repeat, sequence)
        sent_at = lib.iso()
        sent_mono = time.perf_counter()
        routed, error = _publish(envelope)
        record = {
            "sequence": sequence,
            "tripId": envelope["payload"]["tripId"],
            "messageId": envelope["messageId"],
            "sentAtUtc": sent_at,
            "sentMono": sent_mono,
            "payloadBytes": len(json.dumps(envelope).encode("utf-8")),
            "routed": routed,
            "error": error,
            "duplicate": False,
            "outcome": "success" if routed else "systemError",
            "scheduleLatenessMs": max(0.0, (sent_mono - target) * 1000.0),
        }
        unique.append(record)
        deliveries.append(record)
        if duplicate_every > 0 and (index + 1) % duplicate_every == 0:
            duplicate_record = dict(record)
            duplicate_record["duplicate"] = True
            duplicate_record["sentAtUtc"] = lib.iso()
            duplicate_record["sentMono"] = time.perf_counter()
            routed, error = _publish(envelope)
            duplicate_record["routed"] = routed
            duplicate_record["error"] = error
            duplicate_record["outcome"] = "success" if routed else "systemError"
            duplicate_record["scheduleLatenessMs"] = max(
                0.0, (duplicate_record["sentMono"] - target) * 1000.0
            )
            deliveries.append(duplicate_record)
        sequence += 1
    duration = time.perf_counter() - started
    return unique, deliveries, duration


def _poll_queue(stop: Any, samples: list[dict[str, Any]], interval: float = 0.5) -> None:
    while not stop.is_set():
        try:
            queue = _queue_snapshot()
            stats = queue.get("message_stats") or {}
            samples.append(
                {
                    "atUtc": lib.iso(),
                    "messagesReady": queue.get("messages_ready"),
                    "messagesUnacked": queue.get("messages_unacknowledged"),
                    "messages": queue.get("messages"),
                    "consumers": queue.get("consumers"),
                    "publish": stats.get("publish"),
                    "deliver": stats.get("deliver_get"),
                    "ack": stats.get("ack"),
                    "redeliver": stats.get("redeliver"),
                }
            )
        except Exception as exc:  # noqa: BLE001 - polling is best effort
            samples.append({"atUtc": lib.iso(), "error": f"{type(exc).__name__}: {exc}"})
        time.sleep(interval)


def _wait_drain(*, timeout: float = 120) -> tuple[bool, float]:
    deadline = time.monotonic() + timeout
    started = time.monotonic()
    while time.monotonic() < deadline:
        try:
            queue = _queue_snapshot()
            if int(queue.get("messages") or 0) == 0:
                return True, time.monotonic() - started
        except Exception:  # noqa: BLE001 - readiness probe
            pass
        time.sleep(1)
    return False, time.monotonic() - started


def _round_rows(started_at: str) -> list[dict[str, Any]]:
    return lib.psql_json_rows(
        "matching_db",
        "SELECT input_message_id, trip_id::text AS trip_id, status, "
        "created_at::text AS created_at FROM matching_rounds "
        f"WHERE created_at >= '{started_at}'::timestamptz ORDER BY created_at",
    )


def _matching_state() -> dict[str, Any]:
    return {
        "roundCount": lib.psql_scalar("matching_db", "SELECT count(*)::text FROM matching_rounds;"),
        "attemptCount": lib.psql_scalar("matching_db", "SELECT count(*)::text FROM matching_attempts;"),
        "duplicateRounds": lib.psql_scalar(
            "matching_db",
            "SELECT count(*)::text FROM (SELECT trip_id, matching_round, count(*) "
            "FROM matching_rounds GROUP BY trip_id, matching_round HAVING count(*) > 1) duplicates;",
        ),
        "duplicateInputIds": lib.psql_scalar(
            "matching_db",
            "SELECT count(*)::text FROM (SELECT input_message_id, count(*) "
            "FROM matching_rounds GROUP BY input_message_id HAVING count(*) > 1) duplicates;",
        ),
        "roundStatusCounts": lib.psql_json_rows(
            "matching_db",
            "SELECT status, count(*)::text AS count FROM matching_rounds GROUP BY status ORDER BY status",
        ),
    }


def _consumer_log_counts(
    containers: list[dict[str, Any]], measured_trip_ids: list[str]
) -> list[dict[str, Any]]:
    counts = []
    measured = set(measured_trip_ids)
    for container in containers:
        container_id = container.get("id")
        result = lib.docker("logs", container_id, timeout=60) if container_id else None
        text = (result.stdout or "") if result is not None else ""
        text += (result.stderr or "") if result is not None else ""
        measured_lines = 0
        warmup_lines = 0
        for line in text.splitlines():
            if LOG_MARKER not in line:
                continue
            if any(trip_id in line for trip_id in measured):
                measured_lines += 1
            else:
                warmup_lines += 1
        counts.append(
            {
                "containerId": container_id,
                "name": container.get("name"),
                "consumedLines": measured_lines + warmup_lines,
                "measuredConsumedLines": measured_lines,
                "warmupConsumedLines": warmup_lines,
            }
        )
    return counts


def _latency_from_rows(rows: list[dict[str, Any]], sent: dict[str, str]) -> tuple[list[float], int]:
    from datetime import datetime

    latencies = []
    missing = 0
    for row in rows:
        message_id = row.get("input_message_id")
        sent_at = sent.get(message_id)
        if not sent_at or not row.get("created_at"):
            missing += 1
            continue
        try:
            sent_dt = datetime.fromisoformat(sent_at.replace("Z", "+00:00"))
            created_dt = datetime.fromisoformat(row["created_at"].replace("Z", "+00:00"))
        except ValueError:
            missing += 1
            continue
        latencies.append((created_dt - sent_dt).total_seconds() * 1000.0)
    return latencies, missing


def _run_configuration_inner(
    args: argparse.Namespace,
    replicas: int,
    repeat: int,
    g4: dict[str, Any],
) -> int:
    run_id = lib.new_run_id("E03", args.seed + repeat + replicas)
    run_dir = lib.RUNS_ROOT / "E03" / run_id
    run_dir.mkdir(parents=True, exist_ok=True)
    raw_dir = run_dir / "raw"
    raw_dir.mkdir(parents=True, exist_ok=True)
    commands = [
        f"python scripts/perf/w13.py e03 --replicas {replicas} --rate {args.rate} "
        f"--duration {args.duration} --worker-mode {args.worker_mode} --seed {args.seed} "
        f"--mode {'report' if args.report_grade else 'smoke'}"
    ]
    commands_file = run_dir / "commands.txt"
    lib.write_text(commands_file, "\n".join(commands) + "\n")

    setup_started = time.perf_counter()
    try:
        _compose_services(replicas, args.worker_mode)
        queue = _wait_consumers(replicas)
        containers = _matching_containers()
        if len(containers) != replicas:
            raise lib.HarnessError(f"expected {replicas} matching containers, observed {len(containers)}")
        environment = lib.capture_environment()
    except Exception as exc:  # noqa: BLE001 - setup failure is BLOCKED, not FAIL
        import traceback
        detail = traceback.format_exc()
        manifest = lib.manifest_base(
            experiment="E03", run_id=run_id, report_grade=args.report_grade,
            smoke=not args.report_grade, g4=g4, seed=args.seed + repeat + replicas,
        )
        manifest["environment"] = lib.capture_environment(include_observed=False)
        manifest["workload"] = {
            "model": "fixed-rate RabbitMQ publisher -> real Matching RideRequestedV1 intake consumer",
            "configured": {
                "replicas": replicas,
                "configuredRatePerSecond": args.rate,
                "durationSeconds": args.duration_seconds,
                "prefetch": 16,
                "dbPool": 20,
                "queue": QUEUE,
                "workerMode": args.worker_mode,
                "warmupSeconds": args.warmup_seconds,
                "cooldownSeconds": args.cooldown_seconds,
            },
            "achieved": {},
            "warmupSeconds": args.warmup_seconds,
            "measurementSeconds": 0,
            "cooldownSeconds": args.cooldown_seconds,
            "datasetSeed": args.seed + repeat + replicas,
        }
        lib.finalize_manifest(
            manifest, run_dir=run_dir, status="BLOCKED", reason=f"E03 setup failed: {exc}",
            samples=[], latency_values=[], assertions={"setup": False},
            raw_files=[commands_file], final_state_files=[], commands=commands,
            extra_workload={"setupSeconds": time.perf_counter() - setup_started},
            limitations=["BLOCKED is an environment failure, not a consumer-scaling result."],
        )
        lib.print_json({"status": "BLOCKED", "runId": run_id, "replicas": replicas, "reason": str(exc)})
        return 3

    # Warm-up: exercise the same consumer path before the measured interval.
    warmup_unique, warmup_deliveries, warmup_duration = _publish_series(
        args.seed + repeat, replicas, repeat,
        rate=args.rate, count=max(1, int(args.rate * args.warmup_seconds)),
        duplicate_every=0, start_sequence=0,
    )
    warmup_drain, warmup_drain_seconds = _wait_drain(timeout=args.drain_timeout)

    poll_samples: list[dict[str, Any]] = []
    stop_poll = __import__("threading").Event()
    poll_thread = __import__("threading").Thread(
        target=_poll_queue, args=(stop_poll, poll_samples), daemon=True
    )
    poll_thread.start()
    measurement_started_iso = lib.iso()
    measurement_started = time.perf_counter()
    unique, deliveries, duration = _publish_series(
        args.seed + repeat, replicas, repeat,
        rate=args.rate, count=args.count, duplicate_every=args.duplicate_every,
        start_sequence=1_000_000,
    )
    measurement_finished = time.perf_counter()
    drain_ok, drain_seconds = _wait_drain(timeout=args.drain_timeout)
    stop_poll.set()
    poll_thread.join(timeout=5)
    rows = _round_rows(measurement_started_iso)
    state = _matching_state()
    consumer_counts = _consumer_log_counts(containers, [record["tripId"] for record in unique])
    queue_after = _queue_snapshot()
    environment_after = lib.capture_environment(include_observed=True)

    sent = {record["messageId"]: record["sentAtUtc"] for record in unique}
    latencies, missing_latency = _latency_from_rows(rows, sent)
    publish_failures = sum(1 for record in deliveries if not record.get("routed"))
    unique_count = len(unique)
    delivery_count = len(deliveries)
    duplicate_deliveries = delivery_count - unique_count
    rounds_created = len(rows)
    queue_peaks = [
        int(sample.get("messages") or 0)
        for sample in poll_samples
        if sample.get("messages") is not None
    ]
    redeliver_values = [
        int(sample.get("redeliver") or 0)
        for sample in poll_samples
        if sample.get("redeliver") is not None
    ]
    redelivery = (max(redeliver_values) - min(redeliver_values)) if redeliver_values else 0
    per_replica_measured = [int(item["measuredConsumedLines"]) for item in consumer_counts]
    per_replica_nonzero = all(value > 0 for value in per_replica_measured) if replicas > 1 else True
    distribution_total_matches = sum(per_replica_measured) == unique_count
    schedule_lateness = [float(record.get("scheduleLatenessMs") or 0.0) for record in deliveries]
    assertions = {
        "replicaContainerCount": len(containers) == replicas,
        "brokerConsumerCount": int(queue.get("consumers") or 0) == replicas,
        "brokerConsumersRemainAtMeasurementEnd": int(queue_after.get("consumers") or 0) == replicas,
        "warmupDrained": warmup_drain,
        "publishFailuresZero": publish_failures == 0,
        "roundsCreatedEqualsUnique": rounds_created == unique_count,
        "noDuplicateRounds": int(state.get("duplicateRounds") or 0) == 0,
        "noDuplicateInputIds": int(state.get("duplicateInputIds") or 0) == 0,
        "queueDrained": drain_ok,
        "allReplicasConsumedWork": per_replica_nonzero,
        "measuredDistributionTotalMatches": distribution_total_matches,
        "zeroMissingLatencySamples": missing_latency == 0,
        "validLatencySamples": len(latencies) == unique_count and all(math.isfinite(value) and value >= 0 for value in latencies),
        "duplicateIntakeSuppressed": duplicate_deliveries == 0 or rounds_created == unique_count,
    }
    if args.worker_mode == "disabled":
        assertions["workerIsolation"] = int(state.get("attemptCount") or 0) == 0
    status = "PASS" if all(assertions.values()) else "FAIL"
    reason = (
        "consumer scaling configuration completed and every consumer processed work"
        if status == "PASS"
        else "one or more consumer-scaling assertions failed"
    )
    if publish_failures == delivery_count:
        status, reason = "BLOCKED", "every broker publish failed; no consumer load was offered"
    achieved = {
        "replicas": replicas,
        "configuredRatePerSecond": args.rate,
        "achievedSendRatePerSecond": delivery_count / duration if duration > 0 else None,
        "uniqueMessages": unique_count,
        "deliveries": delivery_count,
        "duplicateDeliveries": duplicate_deliveries,
        "publishFailures": publish_failures,
        "durationSeconds": duration,
        "warmupMessages": len(warmup_unique),
        "warmupDurationSeconds": warmup_duration,
        "warmupDrainSeconds": warmup_drain_seconds,
        "roundsCreated": rounds_created,
        "achievedConsumeRatePerSecond": rounds_created / (duration + drain_seconds) if duration + drain_seconds > 0 else None,
        "consumeObservationSeconds": duration + drain_seconds,
        "p50IntakeLatencyMs": lib.percentile_nearest_rank(latencies, 50),
        "p95IntakeLatencyMs": lib.percentile_nearest_rank(latencies, 95),
        "latencySampleCount": len(latencies),
        "missingLatencySamples": missing_latency,
        "queuePeakMessages": max(queue_peaks) if queue_peaks else None,
        "queueSamples": len(poll_samples),
        "maxScheduleLatenessMs": max(schedule_lateness) if schedule_lateness else None,
        "perReplicaMeasuredConsumed": per_replica_measured,
        "redeliveryDelta": redelivery,
        "brokerConsumers": queue_after.get("consumers"),
        "perReplicaConsumed": consumer_counts,
        "drainSeconds": drain_seconds,
        "matchingState": state,
        "environmentAfter": {
            "capturedAtUtc": environment_after.get("capturedAtUtc"),
            "observed": environment_after.get("observed"),
            "runtimeStats": lib.container_stats(),
        },
    }
    manifest = lib.manifest_base(
        experiment="E03", run_id=run_id, report_grade=args.report_grade,
        smoke=not args.report_grade, g4=g4, seed=args.seed + repeat + replicas,
    )
    manifest["environment"] = environment
    manifest["workload"] = {
        "model": "fixed-rate RabbitMQ publisher -> real Matching RideRequestedV1 intake consumer",
        "configured": {
            "replicas": replicas,
            "configuredRatePerSecond": args.rate,
            "durationSeconds": args.duration_seconds,
            "prefetch": 16,
            "dbPool": 20,
            "queue": QUEUE,
            "exchange": EXCHANGE,
            "routingKey": ROUTING_KEY,
            "workerMode": args.worker_mode,
            "payloadBytes": unique[0]["payloadBytes"] if unique else None,
            "duplicateEvery": args.duplicate_every,
            "warmupSeconds": args.warmup_seconds,
            "cooldownSeconds": args.cooldown_seconds,
        },
        "achieved": achieved,
        "warmupSeconds": warmup_duration + warmup_drain_seconds,
        "measurementSeconds": measurement_finished - measurement_started,
        "cooldownSeconds": drain_seconds,
        "datasetSeed": args.seed + repeat + replicas,
    }
    raw_samples = raw_dir / "e03-deliveries.json"
    poll_file = raw_dir / "e03-queue-samples.json"
    lib.write_json(raw_samples, {
        "synthetic": False,
        "reportGrade": args.report_grade,
        "unique": unique,
        "deliveries": deliveries,
    })
    lib.write_json(poll_file, poll_samples)
    final_state_path = run_dir / "final-state" / "e03-matching-state.json"
    lib.write_json(final_state_path, {
        "synthetic": False,
        "reportGrade": args.report_grade,
        "rounds": rows,
        "state": state,
        "consumerCounts": consumer_counts,
        "queueAfter": queue_after,
    })
    metrics_ok, metric_file = w13_metrics.capture(run_dir, "E03", start=measurement_started_iso)
    assertions["declaredMetricsPresentAndFinite"] = metrics_ok
    if not metrics_ok:
        status, reason = "FAIL", "declared metrics missing or invalid"
    lib.finalize_manifest(
        manifest, run_dir=run_dir, status=status, reason=reason,
        samples=deliveries, latency_values=latencies, assertions=assertions,
        raw_files=[raw_samples, poll_file, commands_file, metric_file],
        final_state_files=[final_state_path],
        commands=commands,
        extra_workload={},
        limitations=[
            "The autonomous Matching worker is disabled by default to isolate intake consumer scaling; "
            "worker-enabled runs are a separate, confounded variant.",
            "Intake latency is published-at to durable-round-created-at; it is not full workflow latency.",
            "C-owned broker replica management and shared metrics remain external dependencies.",
            "Report-grade execution additionally requires a green G4 gate and the campaign analyzer.",
        ],
        raw_retained_locally=True,
    )
    lib.print_json({"status": status, "runId": run_id, "replicas": replicas, "achieved": achieved})
    return 0 if status == "PASS" else 2


def run_configuration(
    args: argparse.Namespace,
    replicas: int,
    repeat: int,
    g4: dict[str, Any],
) -> int:
    try:
        return _run_configuration_inner(args, replicas, repeat, g4)
    except Exception as exc:  # noqa: BLE001 - a crashed harness run is BLOCKED, not a result
        base = lib.RUNS_ROOT / "E03"
        orphans = [
            path for path in base.glob("W13-E03-*")
            if path.is_dir() and not (path / "manifest.json").exists()
        ] if base.exists() else []
        if orphans:
            run_dir = max(orphans, key=lambda path: path.stat().st_mtime)
            run_id = run_dir.name
        else:
            run_id = lib.new_run_id("E03", args.seed + repeat + replicas)
            run_dir = base / run_id
        run_dir.mkdir(parents=True, exist_ok=True)
        commands = [
            f"python scripts/perf/w13.py e03 --replicas {replicas} --rate {args.rate} "
            f"--duration {args.duration} --worker-mode {args.worker_mode} --seed {args.seed} "
            f"--mode {'report' if args.report_grade else 'smoke'}"
        ]
        import traceback
        detail = traceback.format_exc()
        manifest = lib.manifest_base(
            experiment="E03", run_id=run_id, report_grade=args.report_grade,
            smoke=not args.report_grade, g4=g4, seed=args.seed + repeat + replicas,
        )
        manifest["environment"] = lib.capture_environment(include_observed=False)
        manifest["workload"] = {
            "model": "fixed-rate RabbitMQ publisher -> real Matching RideRequestedV1 intake consumer",
            "configured": {
                "replicas": replicas,
                "configuredRatePerSecond": args.rate,
                "durationSeconds": args.duration_seconds,
                "workerMode": args.worker_mode,
            },
            "achieved": {},
            "warmupSeconds": args.warmup_seconds,
            "measurementSeconds": 0,
            "cooldownSeconds": args.cooldown_seconds,
            "datasetSeed": args.seed + repeat + replicas,
        }
        lib.finalize_manifest(
            manifest, run_dir=run_dir, status="BLOCKED",
            reason=f"E03 harness failure: {type(exc).__name__}: {exc}",
            samples=[], latency_values=[], assertions={"harnessCompleted": False, "traceback": detail},
            raw_files=[], final_state_files=[], commands=commands,
            limitations=["BLOCKED is a harness/environment failure, never a consumer-scaling result."],
            raw_retained_locally=True,
        )
        try:
            lib.compose_down(volumes=True)
        except Exception:  # noqa: BLE001 - best-effort cleanup
            pass
        lib.print_json({"status": "BLOCKED", "runId": run_id, "replicas": replicas,
                        "reason": f"{type(exc).__name__}: {exc}"})
        return 3


def run(args: argparse.Namespace) -> int:
    g4 = lib.g4_readiness()
    if args.report_grade:
        try:
            lib.ensure_report_grade(g4, lib.source_binding())
        except lib.GateError as exc:
            lib.print_json({"status": "BLOCKED", "reason": str(exc), "g4": g4})
            return 3
        if args.repeats < 3:
            lib.print_json({"status": "INVALID", "reason": "report-grade E03 requires at least 3 repeats"})
            return 2

    args.count = max(1, int(args.rate * args.duration_seconds))
    statuses = []
    for repeat in range(args.repeats):
        for replicas in args.replicas:
            code = run_configuration(args, replicas, repeat, g4)
            statuses.append(code)
    overall = 0 if all(code == 0 for code in statuses) else (3 if any(code == 3 for code in statuses) else 2)
    lib.print_json({"status": "PASS" if overall == 0 else "FAIL_OR_BLOCKED", "codes": statuses})
    return overall


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description="PERF/E03 consumer-scaling harness")
    parser.add_argument("--replicas", type=lambda value: [int(part) for part in value.split(",")],
                        default=list(DEFAULT_REPLICAS))
    parser.add_argument("--rate", type=float, default=20.0)
    parser.add_argument("--duration", default="20s")
    parser.add_argument("--warmup-seconds", type=float, default=3.0)
    parser.add_argument("--cooldown-seconds", type=float, default=3.0)
    parser.add_argument("--drain-timeout", type=float, default=120.0)
    parser.add_argument("--duplicate-every", type=int, default=0)
    parser.add_argument("--worker-mode", choices=("disabled", "enabled"), default="disabled")
    parser.add_argument("--seed", type=int, default=1303)
    parser.add_argument("--repeats", type=int, default=1)
    parser.add_argument("--mode", choices=("smoke", "report"), default="smoke")
    return parser


def main(argv: list[str] | None = None) -> int:
    parser = build_parser()
    args = parser.parse_args(argv)
    args.report_grade = args.mode == "report"
    try:
        args.duration_seconds = int(args.duration.rstrip("s"))
    except ValueError:
        parser.error("--duration must be an integer number of seconds (e.g. 20s)")
    if args.rate <= 0:
        parser.error("--rate must be greater than zero")
    if args.duration_seconds <= 0:
        parser.error("--duration must be greater than zero")
    return run(args)


if __name__ == "__main__":
    raise SystemExit(main())
