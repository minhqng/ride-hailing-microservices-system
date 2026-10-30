#!/usr/bin/env python3
"""Controlled consumer outage over W13's real-provider measurement utilities."""
from __future__ import annotations

import argparse
import json
import os
import subprocess
import sys
import threading
import time
import uuid
from pathlib import Path

import w13lib as lib
from w13_e03 import _envelope, _queue_snapshot

PROBE = lib.PERF_DIR / "recovery-probe" / "recovery-probe.csproj"
PROBE_DLL = PROBE.parent / "bin" / "Release" / "net10.0" / "recovery-probe.dll"
RUNS = lib.ROOT / "evidence/member-b/w14-recovery/outputs"


def release_gate() -> dict:
    gate = lib.g4_readiness()
    path = Path(gate["evidencePath"])
    certificate = lib.read_json(path) if path.is_file() else None
    gate["certificate"] = certificate
    gate["certificateSha256"] = lib.sha256_text(json.dumps(certificate, sort_keys=True))
    return gate


def isolate() -> None:
    # Only this process changes helper context; W13 files and CLI stay untouched.
    lib.PROJECT = "ride-hailing-w14"
    lib.COMPOSE_FILES = ["compose.yaml", "scripts/perf/compose.w13.yaml", "scripts/perf/compose.w14.yaml"]
    ports = {"POSTGRES": 25432, "REDIS": 26379, "RABBIT": 25673,
             "RABBIT_MGMT": 35672, "DRIVER": 28102, "GATEWAY": 28080,
             "IDENTITY": 28101, "TRIP": 28103, "PAYMENT": 28105}
    for name, port in ports.items():
        os.environ[f"W13_{name}_PORT"] = os.environ.get(f"W14_{name}_PORT", str(port))
    os.environ["W13_MATCHING_WORKER_ENABLED"] = "true"
    os.environ["W13_RABBIT_PREFETCH"] = "16"
    os.environ["W13_DB_POOL"] = "20"


def queue() -> dict:
    q = _queue_snapshot()
    stats = q.get("message_stats", {})
    return {"ready": q["messages_ready"], "unacked": q["messages_unacknowledged"],
            "consumers": q["consumers"], "redeliver": stats.get("redeliver", 0),
            "ack": stats.get("ack", 0), "type": q["type"], "leader": q.get("leader"),
            "members": q.get("members", [])}


def state() -> dict:
    return {
        "rounds": lib.psql_json_rows("matching_db", "SELECT trip_id::text, matching_round, "
            "input_message_id, status, auto_execute, result_message_id::text, "
            "selected_driver_id::text, selected_reservation_id::text FROM matching_rounds"),
        "attempts": lib.psql_json_rows("matching_db", "SELECT trip_id::text, matching_round, "
            "ordinal, driver_id::text, reservation_id::text, state FROM matching_attempts"),
        "matchingOutbox": lib.psql_json_rows("matching_db", "SELECT message_id, message_type, "
            "aggregate_id, payload_json, status, published_at::text FROM outbox_messages"),
        "drivers": lib.psql_json_rows("driver_db", "SELECT driver_id::text, status, version, "
            "reservation_id::text, reserved_trip_id::text FROM drivers"),
        "driverOutbox": lib.psql_json_rows("driver_db", "SELECT message_id, message_type, "
            "aggregate_id, status FROM outbox_messages"),
    }


def sample(started: float, phase: str) -> dict:
    # Both broker ownership and durable application ownership are observed.
    work = lib.psql_json("matching_db", "SELECT json_build_object('intake', count(*), "
        "'terminal', count(*) FILTER (WHERE status IN ('RESERVED','NO_DRIVER','CANCELLED')), "
        "'pending', count(*) FILTER (WHERE status NOT IN ('RESERVED','NO_DRIVER','CANCELLED')), "
        "'outbox', (SELECT json_build_object('committed', count(*), "
        "'published', count(*) FILTER (WHERE status='PUBLISHED')) FROM outbox_messages)) "
        "FROM matching_rounds")
    broker = queue()
    return {"atUtc": lib.iso(), "elapsed": time.monotonic() - started,
            "phase": phase, **broker, **work}


def wait_for(predicate, label: str, timeout: float = 120):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        try:
            value = predicate()
        except (lib.HarnessError, KeyError):
            # Queue creation and its first management statistics lag process startup.
            value = None
        if value:
            return value
        time.sleep(0.2)
    raise lib.HarnessError(f"timeout waiting for {label}")


class Publisher:
    def __init__(self):
        env = os.environ.copy()
        env["W14_RABBIT_PORT"] = env["W13_RABBIT_PORT"]
        self.process = subprocess.Popen([lib.find_dotnet(), str(PROBE_DLL)], cwd=lib.ROOT,
            env=env, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
            text=True, encoding="utf-8", bufsize=1)

    def publish(self, envelope: dict) -> dict:
        self.process.stdin.write(json.dumps(envelope) + "\n")
        self.process.stdin.flush()
        line = self.process.stdout.readline()
        if not line:
            raise lib.HarnessError("confirm probe exited without a receipt")
        return json.loads(line)

    def close(self):
        try:
            try:
                self.process.stdin.close()
            except BrokenPipeError:
                pass
        finally:
            try:
                self.process.wait(timeout=15)
            except subprocess.TimeoutExpired:
                self.process.kill()
                self.process.wait()
            finally:
                self.process.stdout.close()


def finish_run(manifest, run_dir, publisher, producer, stop, samples, receipts, resource_thread=None):
    stop.set()
    try:
        if producer:
            producer.join(timeout=20)
        if publisher:
            publisher.close()
    except Exception as exc:
        manifest["status"] = "FAIL"
        manifest["cleanupError"] = type(exc).__name__
    finally:
        try:
            if resource_thread:
                resource_thread.join(timeout=65)
            for name, data in (("samples", samples), ("receipts", receipts)):
                path = run_dir / f"{name}.json"
                lib.write_json(path, data)
                manifest["artifacts"][name] = {"path": path.name, "sha256": lib.sha256_file(path)}
            manifest["finishedAtUtc"] = lib.iso()
            manifest["sourceAfter"] = lib.source_binding()
            lib.write_json(run_dir / "manifest.json", manifest)
        finally:
            lib.compose_down(volumes=True)


def audit(source: dict) -> dict:
    env = {"B12_AUDIT_SOURCE_COMMIT": source["commit"], "B12_AUDIT_SOURCE_TREE": source["tree"]}
    for service in ("driver", "matching"):
        password = os.environ.get(f"{service.upper()}_DB_PASSWORD", f"{service}_dev_pw")
        env[f"W14_{service.upper()}_CONNECTION"] = (
            f"Host=127.0.0.1;Port={os.environ['W13_POSTGRES_PORT']};Database={service}_db;"
            f"Username={service}_app;Password={password}")
    result = lib.run([lib.find_dotnet(), str(PROBE_DLL), "audit"], env=env, check=True)
    return json.loads(result.stdout)


def run_one(args, repeat: int, group: str) -> Path:
    source = lib.source_binding()
    g4 = release_gate()
    if args.mode == "report":
        lib.ensure_report_grade(g4, source)
    run_id = f"W14-E04-{lib.utc_stamp()}-{uuid.uuid4().hex[:8]}"
    run_dir = RUNS / run_id
    run_dir.mkdir(parents=True)
    manifest = {"schemaVersion": 1, "experiment": "E04", "runId": run_id,
        "group": group, "repeat": repeat, "plannedRepeats": args.repeats,
        "mode": args.mode, "synthetic": False,
        "source": source, "g4": g4, "status": "INCOMPLETE", "artifacts": {},
        "configuration": {"rate": args.rate, "outageSeconds": args.outage,
            "baselineSeconds": args.baseline, "recoveryTimeout": args.timeout,
            "seed": args.seed, "consumerCount": 1, "prefetch": 16,
            "dataset": "one OFFLINE Driver and healthy empty Redis; real NO_DRIVER outcomes",
            "outage": "compose kill -s SIGKILL matching; producer and broker remain running",
            "reset": "fresh W14 project volumes per repeat"}}
    lib.write_json(run_dir / "manifest.json", manifest)
    publisher = None
    producer = None
    resource_thread = None
    stop = threading.Event()
    samples, receipts, errors = [], [], []
    phase = {"value": "baseline"}
    try:
        if lib.PROJECT != "ride-hailing-w14":
            raise lib.HarnessError("refusing destructive reset outside isolated W14 project")
        lib.compose_down(volumes=True)
        lib.compose_up(["postgres", "redis", "rabbitmq", "driver", "matching"], scale=1)
        wait_for(lambda: queue()["consumers"] == 1, "healthy intake consumer")
        driver_id = lib.seeded_uuid(args.seed + repeat, "e04-offline-driver")
        status, _, _, _ = lib.http_request("POST",
            f"http://127.0.0.1:{os.environ['W13_DRIVER_PORT']}/drivers",
            body={"driverId": driver_id, "userId": lib.seeded_uuid(args.seed + repeat, "e04-owner")})
        if status != 201:
            raise lib.HarnessError(f"OFFLINE Driver seed failed: HTTP {status}")
        manifest["environment"] = lib.capture_environment()
        from w14_ha import snapshot
        manifest["topology"] = snapshot()
        manifest["fingerprint"] = lib.environment_fingerprint(manifest["environment"])
        manifest["driverBefore"] = state()["drivers"]
        manifest["resourcesBefore"] = [r for r in lib.container_stats()
            if str(r.get("Name", "")).startswith(lib.PROJECT)]
        publisher = Publisher()
        started = time.monotonic()

        def produce():
            index = 0
            while not stop.is_set():
                target = started + index / args.rate
                if stop.wait(max(0, target - time.monotonic())):
                    break
                envelope = _envelope(args.seed + repeat, 1, repeat, index)
                current_phase = phase["value"]
                sent_elapsed = time.monotonic() - started
                try:
                    receipt = publisher.publish(envelope)
                    receipts.append({**receipt, "tripId": envelope["payload"]["tripId"],
                        "phase": current_phase, "sentElapsed": sent_elapsed,
                        "elapsed": time.monotonic() - started,
                        "latenessSeconds": max(0, sent_elapsed - index / args.rate)})
                except Exception as exc:
                    errors.append(type(exc).__name__)
                    stop.set()
                index += 1

        producer = threading.Thread(target=produce, name="confirmed-arrivals")
        producer.start()

        def observe_until(duration: float, predicate=None):
            deadline = time.monotonic() + duration
            while time.monotonic() < deadline:
                row = sample(started, phase["value"])
                samples.append(row)
                if errors:
                    raise lib.HarnessError("publisher failed")
                if predicate and predicate(row):
                    return row
                time.sleep(0.2)
            if predicate:
                raise lib.HarnessError("recovery did not reach required state within deadline")

        observe_until(args.baseline)
        wait_for(lambda: samples[-1]["terminal"] > 0 or sample(started, "baseline")["terminal"] > 0,
                 "baseline authoritative completion")
        manifest["beforeInterruption"] = sample(started, "baseline")
        samples.append(manifest["beforeInterruption"])
        manifest["stopRequestedAtUtc"] = lib.iso()
        phase["value"] = "outage"
        manifest["interruption"] = {"startUtc": lib.iso(), "start": time.monotonic() - started}
        # Include the actual fault action in T; a graceful shutdown wait must
        # not accumulate unmeasured backlog before the interruption clock.
        lib.compose("kill", "-s", "SIGKILL", "matching", check=True)
        manifest["killCommandCompletedAtUtc"] = lib.iso()
        wait_for(lambda: queue()["consumers"] == 0, "consumer removal", timeout=args.outage)
        manifest["consumerRemovedAtUtc"] = lib.iso()
        def capture_outage_resources():
            try:
                manifest["resourcesDuring"] = [r for r in lib.container_stats()
                    if str(r.get("Name", "")).startswith(lib.PROJECT)]
            except Exception as error:
                manifest["resourceCaptureError"] = type(error).__name__
        resource_thread = threading.Thread(target=capture_outage_resources, name="outage-resources")
        resource_thread.start()
        remaining = args.outage - (time.monotonic() - started - manifest["interruption"]["start"])
        if remaining <= 0:
            raise lib.HarnessError("consumer removal exceeded the requested interruption budget")
        observe_until(remaining)
        manifest["interruption"].update(endUtc=lib.iso(), end=time.monotonic() - started)
        phase["value"] = "recovery"
        manifest["restoreRequestedAtUtc"] = lib.iso()
        # Keep publishing throughout recovery: λ is not silently reduced to zero.
        lib.compose("start", "matching", check=True)
        manifest["restoreCommandCompletedAtUtc"] = lib.iso()
        restore = manifest["interruption"]["end"]
        baseline_terminal = samples[-1]["terminal"]
        first = observe_until(args.timeout, lambda row: row["terminal"] > baseline_terminal)
        manifest["firstSuccessSeconds"] = first["elapsed"] - restore
        # With arrivals still running, return to the measured baseline DB work level.
        # Full application quiescence is a separate barrier after stopping the producer.
        stable = []
        baseline_pending = manifest["beforeInterruption"]["pending"]
        manifest["steadyPolicy"] = {"consecutiveSamples": 3,
            "ready": 0, "unacked": 0, "maxPending": baseline_pending}
        def drained(row):
            good = (row["ready"] == row["unacked"] == 0 and row["pending"] <= baseline_pending
                    and row["consumers"] == 1)
            stable.append(good)
            return len(stable) >= 3 and all(stable[-3:])
        drained_at = observe_until(args.timeout, drained)
        manifest["steadyElapsed"] = drained_at["elapsed"]
        stop.set()
        producer.join(timeout=20)
        if producer.is_alive():
            raise lib.HarnessError("publisher did not stop")
        phase["value"] = "quiescence"
        def quiesced(row):
            return (row["ready"] == row["unacked"] == row["pending"] == 0
                and row["intake"] == len(receipts)
                and row["outbox"]["published"] == row["intake"])
        observe_until(args.timeout, quiesced)
        # Explicit duplicate publication uses the original identity, after terminal completion.
        original = _envelope(args.seed + repeat, 1, repeat, 0)
        manifest["duplicateReceipt"] = publisher.publish(original)
        observe_until(30, lambda row: row["ready"] == row["unacked"] == row["pending"] == 0
            and row["ack"] >= len(receipts) + 1)
        final = state()
        manifest["auditor"] = audit(source)
        manifest["environmentAfter"] = lib.capture_environment()
        manifest["resources"] = [r for r in lib.container_stats()
            if str(r.get("Name", "")).startswith(lib.PROJECT)]
        for name, data in (("samples", samples), ("receipts", receipts), ("final-state", final)):
            path = run_dir / f"{name}.json"
            lib.write_json(path, data)
            manifest["artifacts"][name] = {"path": path.name, "sha256": lib.sha256_file(path)}
        manifest["status"] = "PASS" if not errors else "FAIL"
    except Exception as exc:
        manifest["status"] = "FAIL"
        manifest["error"] = type(exc).__name__ + ": " + lib._redact_string(str(exc))[:1000]
    finally:
        finish_run(manifest, run_dir, publisher, producer, stop, samples, receipts, resource_thread)
    return run_dir


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="command", required=True)
    e04 = sub.add_parser("e04")
    e04.add_argument("--mode", choices=("smoke", "report"), default="smoke")
    e04.add_argument("--rate", type=float, default=0.5)
    e04.add_argument("--outage", type=float, default=5)
    e04.add_argument("--baseline", type=float, default=10)
    e04.add_argument("--timeout", type=float, default=120)
    e04.add_argument("--repeats", type=int, default=1)
    e04.add_argument("--seed", type=int, default=1400)
    analyze = sub.add_parser("analyze")
    analyze.add_argument("runs", nargs="+", type=Path)
    sub.add_parser("g4")
    args = parser.parse_args(argv)
    if args.command == "analyze":
        from w14_analyze import analyze_group
        result = analyze_group(args.runs)
        lib.print_json(result)
        if result["status"] == "GATED":
            return 3
        return 0 if result["status"] in ("ACCEPTED", "SMOKE_ONLY", "SYNTHETIC_ONLY") else 2
    if args.command == "g4":
        lib.print_json(lib.g4_readiness())
        return 0 if lib.g4_readiness()["green"] else 3
    import math
    if (not all(math.isfinite(v) and v > 0 for v in (args.rate, args.outage, args.baseline, args.timeout))
            or args.repeats < 1):
        parser.error("rates, durations and repetitions must be positive and finite")
    if args.mode == "report":
        if args.repeats < 3:
            parser.error("report-grade E04 requires at least three measured repeats")
        lib.ensure_report_grade(lib.g4_readiness())
    isolate()
    dotnet = lib.find_dotnet()
    if not dotnet:
        raise lib.HarnessError(".NET SDK is required for AMQP confirmation receipts and auditor")
    lib.run([dotnet, "build", str(PROBE), "-c", "Release", "--nologo"], check=True)
    group = uuid.uuid4().hex
    runs = [run_one(args, repeat, group) for repeat in range(1, args.repeats + 1)]
    from w14_analyze import analyze_group
    result = analyze_group(runs)
    lib.print_json({"runs": [str(path) for path in runs], "analysis": result})
    if result["status"] == "GATED":
        return 3
    return 0 if result["status"] in ("ACCEPTED", "SMOKE_ONLY") else 2


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except lib.GateError as error:
        print(str(error), file=sys.stderr)
        raise SystemExit(3)
    except lib.HarnessError as error:
        print(str(error), file=sys.stderr)
        raise SystemExit(2)
