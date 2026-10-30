"""PERF/E01 — same-Driver contention curve.

Canonical scenario (Doc 03 §18): N independent reservation requests target one
AVAILABLE Driver, released through a real barrier. The authoritative Driver
row is the correctness oracle; HTTP 409 is an expected domain conflict, never
a system failure.

The runner is intentionally a harness, not a benchmark claim: report-grade
execution is refused unless the curated G4 gate is green.
"""

from __future__ import annotations

import argparse
import http.client
import json
import statistics
import threading
import time
import urllib.parse
from pathlib import Path
from typing import Any

import w13lib as lib

DEFAULT_NS = [1, 10, 25, 50, 100]


def _base_url(value: str) -> str:
    return value.rstrip("/")


def _url(base: str, path: str) -> urllib.parse.ParseResult:
    return urllib.parse.urlparse(base + path)


def http_json(
    base: str,
    method: str,
    path: str,
    *,
    body: Any = None,
    timeout: float = 30,
) -> tuple[int, Any, float]:
    status, _, payload, elapsed = lib.http_request(method, base + path, body=body, timeout=timeout)
    return status, lib.parse_json(payload), elapsed


def create_and_online(base: str, driver_id: str, *, timeout: float) -> dict[str, Any]:
    status, body, _ = http_json(
        base, "POST", "/drivers",
        body={"driverId": driver_id, "userId": lib.seeded_uuid(0, f"e01-owner-{driver_id}")},
        timeout=timeout,
    )
    if status not in (200, 201):
        raise lib.HarnessError(f"driver create failed with {status}: {body}")
    status, body, _ = http_json(
        base, "POST", f"/drivers/{driver_id}/online",
        body={"expectedVersion": 1}, timeout=timeout,
    )
    if status != 200:
        raise lib.HarnessError(f"driver online failed with {status}: {body}")
    status, body, _ = http_json(base, "GET", f"/drivers/{driver_id}", timeout=timeout)
    if status != 200 or not isinstance(body, dict):
        raise lib.HarnessError(f"driver read failed with {status}: {body}")
    return body


def warmup(base: str, seed: int, repeat_index: int, timeout: float, cycles: int = 3) -> dict[str, Any]:
    driver_id = lib.seeded_uuid(seed, f"e01-warmup-driver-{repeat_index}")
    driver = create_and_online(base, driver_id, timeout=timeout)
    version = int(driver["version"])
    completed = []
    for cycle in range(cycles):
        trip_id = lib.seeded_uuid(seed, f"e01-warmup-trip-{repeat_index}-{cycle}")
        reservation_id = lib.seeded_uuid(seed, f"e01-warmup-reservation-{repeat_index}-{cycle}")
        status, body, _ = http_json(
            base, "POST", f"/drivers/{driver_id}/reserve",
            body={
                "tripId": trip_id,
                "reservationId": reservation_id,
                "expectedVersion": version,
                "matchingRound": 1,
            },
            timeout=timeout,
        )
        if status != 200:
            raise lib.HarnessError(f"warm-up reserve failed with {status}: {body}")
        version = int(body["version"])
        status, body, _ = http_json(
            base, "POST", f"/drivers/{driver_id}/release",
            body={"tripId": trip_id, "reservationId": reservation_id},
            timeout=timeout,
        )
        if status != 200:
            raise lib.HarnessError(f"warm-up release failed with {status}: {body}")
        version = int(body["version"])
        completed.append({"cycle": cycle, "tripId": trip_id, "reservationId": reservation_id})
    return {"driverId": driver_id, "cycles": completed, "finalVersion": version}


def _classify(status: int, body: Any, elapsed_ms: float) -> dict[str, Any]:
    response_outcome = body.get("outcome") if isinstance(body, dict) else None
    code = body.get("code") if isinstance(body, dict) else None
    sample: dict[str, Any] = {
        "status": status,
        "code": code,
        "responseOutcome": response_outcome,
        "latencyMs": elapsed_ms,
    }
    if status == 200 and response_outcome in ("won", "replayed"):
        sample["outcome"] = "success"
    elif status == 409:
        sample["outcome"] = "domainConflict"
    elif status == 503 or status >= 500:
        sample["outcome"] = "systemError"
    else:
        sample["outcome"] = "unexpected"
    return sample


def contend(
    base: str,
    driver_id: str,
    expected_version: int,
    n: int,
    seed: int,
    repeat_index: int,
    *,
    timeout: float,
) -> list[dict[str, Any]]:
    parsed = _url(base, "")
    host = parsed.hostname or "127.0.0.1"
    port = parsed.port or 80
    path = f"/drivers/{driver_id}/reserve"
    barrier = threading.Barrier(n)
    results: list[dict[str, Any] | None] = [None] * n
    release_lock = threading.Lock()
    barrier_release_ns: list[int] = []

    def worker(index: int) -> None:
        trip_id = lib.seeded_uuid(seed, f"e01-trip-{repeat_index}-{n}-{index}")
        reservation_id = lib.seeded_uuid(seed, f"e01-reservation-{repeat_index}-{n}-{index}")
        payload = {
            "tripId": trip_id,
            "reservationId": reservation_id,
            "expectedVersion": expected_version,
            "matchingRound": 1,
        }
        connection = http.client.HTTPConnection(host, port, timeout=timeout)
        connect_error: str | None = None
        try:
            connection.connect()
        except Exception as exc:  # noqa: BLE001 - connection setup is part of the experiment
            connect_error = f"{type(exc).__name__}: {exc}"
        try:
            barrier.wait(timeout=max(30, timeout))
        except threading.BrokenBarrierError as exc:
            results[index] = {
                "index": index,
                "tripId": trip_id,
                "reservationId": reservation_id,
                "outcome": "unexpected",
                "status": 0,
                "code": "BARRIER_BROKEN",
                "error": str(exc),
                "latencyMs": 0.0,
            }
            connection.close()
            return
        with release_lock:
            barrier_release_ns.append(time.perf_counter_ns())
        if connect_error is not None:
            results[index] = {
                "index": index,
                "tripId": trip_id,
                "reservationId": reservation_id,
                "outcome": "systemError",
                "status": 0,
                "code": "CONNECT_FAILED",
                "error": connect_error,
                "latencyMs": 0.0,
            }
            connection.close()
            return
        started = time.perf_counter()
        try:
            connection.request(
                "POST", path, body=json.dumps(payload),
                headers={"Content-Type": "application/json", "Accept": "application/json"},
            )
            response = connection.getresponse()
            raw = response.read().decode("utf-8", errors="replace")
            elapsed_ms = (time.perf_counter() - started) * 1000.0
            body = lib.parse_json(raw)
            sample = _classify(response.status, body, elapsed_ms)
            sample.update({"index": index, "tripId": trip_id, "reservationId": reservation_id})
            results[index] = sample
        except (TimeoutError, OSError) as exc:
            elapsed_ms = (time.perf_counter() - started) * 1000.0
            results[index] = {
                "index": index,
                "tripId": trip_id,
                "reservationId": reservation_id,
                "outcome": "timeout",
                "status": 0,
                "code": type(exc).__name__,
                "error": str(exc),
                "latencyMs": elapsed_ms,
            }
        except Exception as exc:  # noqa: BLE001 - unexpected client failure
            elapsed_ms = (time.perf_counter() - started) * 1000.0
            results[index] = {
                "index": index,
                "tripId": trip_id,
                "reservationId": reservation_id,
                "outcome": "systemError",
                "status": 0,
                "code": type(exc).__name__,
                "error": str(exc),
                "latencyMs": elapsed_ms,
            }
        finally:
            connection.close()

    threads = [threading.Thread(target=worker, args=(index,), name=f"e01-{index}", daemon=True) for index in range(n)]
    started_at = time.perf_counter()
    for thread in threads:
        thread.start()
    for thread in threads:
        thread.join(timeout=max(60, timeout * 2))
    wall_ms = (time.perf_counter() - started_at) * 1000.0
    for index, thread in enumerate(threads):
        if thread.is_alive():
            results[index] = {
                "index": index,
                "outcome": "timeout",
                "status": 0,
                "code": "THREAD_HUNG",
                "latencyMs": wall_ms,
            }
    if barrier_release_ns:
        spread_ms = (max(barrier_release_ns) - min(barrier_release_ns)) / 1_000_000.0
    else:
        spread_ms = None
    completed = [sample for sample in results if sample is not None]
    for sample in completed:
        sample["barrierSpreadMs"] = spread_ms
        sample["wallMs"] = wall_ms
    return completed


def final_state(driver_id: str, winner: dict[str, Any] | None) -> dict[str, Any]:
    driver_rows = lib.psql_json(
        "driver_db",
        "SELECT COALESCE(json_agg(t), '[]'::json) FROM ("
        "SELECT driver_id::text AS driver_id, user_id::text AS user_id, status, version, "
        "reserved_trip_id::text AS reserved_trip_id, reservation_id::text AS reservation_id, "
        "active_trip_id::text AS active_trip_id, reserved_round "
        f"FROM drivers WHERE driver_id = '{driver_id}') t;",
    ) or []
    active_rows = lib.psql_json(
        "driver_db",
        "SELECT COALESCE(json_agg(t), '[]'::json) FROM ("
        "SELECT driver_id::text AS driver_id, reservation_id::text AS reservation_id, "
        "reserved_trip_id::text AS reserved_trip_id FROM drivers "
        f"WHERE driver_id = '{driver_id}' AND status IN ('RESERVED','ON_TRIP')) t;",
    ) or []
    outbox_rows = lib.psql_json(
        "driver_db",
        "SELECT COALESCE(json_agg(t), '[]'::json) FROM ("
        "SELECT message_id, message_type, status, aggregate_id, "
        "payload_json->>'reservationId' AS reservation_id, "
        "payload_json->>'tripId' AS trip_id FROM outbox_messages "
        f"WHERE message_type = 'DriverReservedV1' AND replace(aggregate_id, '-', '') = '{driver_id.replace('-', '')}') t;",
    ) or []
    lifecycle_rows = lib.psql_json(
        "driver_db",
        "SELECT COALESCE(json_agg(t), '[]'::json) FROM ("
        "SELECT phase, trip_id::text AS trip_id, reservation_id::text AS reservation_id "
        f"FROM driver_lifecycle_effects WHERE driver_id = '{driver_id}') t;",
    ) or []
    reservation_holders = []
    if winner is not None:
        reservation_holders = lib.psql_json(
            "driver_db",
            "SELECT COALESCE(json_agg(t), '[]'::json) FROM ("
            "SELECT driver_id::text AS driver_id, status FROM drivers "
            f"WHERE reservation_id = '{winner['reservationId']}') t;",
        ) or []
    return {
        "driver": driver_rows[0] if driver_rows else None,
        "activeReservationRows": active_rows,
        "reserveOutbox": outbox_rows,
        "lifecycleEffects": lifecycle_rows,
        "reservationHolders": reservation_holders,
    }


def aud01(final: dict[str, Any], winner: dict[str, Any] | None) -> dict[str, Any]:
    driver = final.get("driver") or {}
    holders = final.get("reservationHolders") or []
    checks = {
        "driverExists": driver not in ({}, None),
        "reservedState": driver.get("status") == "RESERVED",
        "singleActiveRow": len(final.get("activeReservationRows") or []) == 1,
        "oneReservationHolder": winner is not None and len(holders) == 1,
        "winnerReservationMatches": winner is not None and driver.get("reservation_id") == winner.get("reservationId"),
        "winnerTripMatches": winner is not None and driver.get("reserved_trip_id") == winner.get("tripId"),
        "oneReserveOutboxIntent": len(final.get("reserveOutbox") or []) == 1,
        "noLifecycleEffect": len(final.get("lifecycleEffects") or []) == 0,
    }
    status = "PASS" if all(checks.values()) else "FAIL"
    return {"status": status, "checks": checks}


def run_one(
    base: str,
    *,
    n: int,
    seed: int,
    repeat_index: int,
    timeout: float,
    run_dir: Path,
    report_grade: bool,
) -> tuple[list[dict[str, Any]], dict[str, Any], list[Path], list[Path]]:
    driver_id = lib.seeded_uuid(seed, f"e01-driver-{repeat_index}-{n}")
    created = create_and_online(base, driver_id, timeout=timeout)
    expected_version = int(created["version"])
    samples = contend(base, driver_id, expected_version, n, seed, repeat_index, timeout=timeout)
    winners = [sample for sample in samples if sample.get("outcome") == "success"]
    winner = winners[0] if len(winners) == 1 else None
    final = final_state(driver_id, winner)
    audit = aud01(final, winner)
    conflicts = sum(1 for sample in samples if sample.get("outcome") == "domainConflict")
    system_errors = sum(1 for sample in samples if sample.get("outcome") in ("systemError", "timeout"))
    unexpected = sum(1 for sample in samples if sample.get("outcome") == "unexpected")
    checks = {
        "sampleCountEqualsContenders": len(samples) == n,
        "exactlyOneWinner": len(winners) == 1,
        "expectedConflicts": conflicts == n - 1,
        "zeroSystemErrors": system_errors == 0,
        "zeroUnexpected": unexpected == 0,
        "driversReserved": (final.get("driver") or {}).get("status") == "RESERVED",
        "singleActiveReservation": len(final.get("activeReservationRows") or []) == 1,
        "singleReserveOutbox": len(final.get("reserveOutbox") or []) == 1,
        "aud01Pass": audit["status"] == "PASS",
    }
    raw_samples = run_dir / "raw" / f"e01-n{n}-samples.json"
    final_file = run_dir / "final-state" / f"e01-n{n}.json"
    lib.write_json(raw_samples, {
        "synthetic": False,
        "reportGrade": report_grade,
        "experiment": "E01",
        "n": n,
        "driverId": driver_id,
        "expectedVersion": expected_version,
        "samples": samples,
    })
    lib.write_json(final_file, {
        "synthetic": False,
        "reportGrade": report_grade,
        "driverId": driver_id,
        "expectedVersion": expected_version,
        "winner": winner,
        "final": final,
        "aud01": audit,
        "assertions": checks,
    })
    outcome = {
        "n": n,
        "driverId": driver_id,
        "contenders": n,
        "winnerCount": len(winners),
        "conflictCount": conflicts,
        "systemErrorCount": system_errors,
        "unexpectedCount": unexpected,
        "sampleCount": len(samples),
        "p50Ms": lib.percentile_nearest_rank([s["latencyMs"] for s in samples], 50),
        "p95Ms": lib.percentile_nearest_rank([s["latencyMs"] for s in samples], 95),
        "wallMs": samples[0].get("wallMs") if samples else None,
        "barrierSpreadMs": samples[0].get("barrierSpreadMs") if samples else None,
        "assertions": checks,
        "aud01": audit["status"],
    }
    return samples, outcome, [raw_samples], [final_file]


def _prepare_stack(base: str, *, reset: bool) -> None:
    if reset:
        lib.compose_down(volumes=True)
    lib.compose_up(["postgres", "rabbitmq", "driver"], build=True, wait_timeout=240)
    lib.wait_http(base + "/health/ready")


def run(args: argparse.Namespace) -> int:
    g4 = lib.g4_readiness()
    if args.report_grade:
        try:
            lib.ensure_report_grade(g4)
        except lib.GateError as exc:
            lib.print_json({"status": "BLOCKED", "reason": str(exc), "g4": g4})
            return 3
    if args.report_grade and args.repeats < 3:
        lib.print_json({"status": "INVALID", "reason": "report-grade E01 requires at least 3 repeats"})
        return 2

    seed = args.seed
    base = _base_url(args.base_url)
    results = []
    commands = [f"python scripts/perf/w13.py e01 --base-url {base} --seed {seed} --repeats {args.repeats} "
                f"--n {','.join(str(value) for value in args.n)} --mode {'report' if args.report_grade else 'smoke'}"]
    for repeat in range(args.repeats):
        run_id = lib.new_run_id("E01", seed + repeat)
        run_dir = lib.RUNS_ROOT / "E01" / run_id
        run_dir.mkdir(parents=True, exist_ok=True)
        try:
            _prepare_stack(base, reset=args.reset_each)
            lib.wait_http(base + "/health/ready")
            environment = lib.capture_environment()
        except Exception as exc:  # noqa: BLE001 - setup failure is BLOCKED
            manifest = lib.manifest_base(
                experiment="E01", run_id=run_id, report_grade=args.report_grade,
                smoke=not args.report_grade, g4=g4, seed=seed,
            )
            manifest["environment"] = lib.capture_environment(include_observed=False)
            lib.finalize_manifest(
                manifest, run_dir=run_dir, status="BLOCKED",
                reason=f"E01 stack preparation failed: {exc}",
                samples=[], latency_values=[], assertions={"stackReady": False},
                raw_files=[], final_state_files=[], commands=commands,
                limitations=["BLOCKED is an environment failure, never a contention result."],
            )
            lib.print_json({"status": "BLOCKED", "runId": run_id, "reason": str(exc)})
            return 3
        manifest = lib.manifest_base(
            experiment="E01", run_id=run_id, report_grade=args.report_grade,
            smoke=not args.report_grade, g4=g4, seed=seed,
        )
        manifest["environment"] = environment
        manifest["workload"] = {
            "model": "closed synchronized barrier; N independent reservations for one Driver",
            "configured": {
                "contenders": args.n,
                "sameDriver": True,
                "barrier": "threading.Barrier (connections established before release)",
                "matchingRound": 1,
                "requestTimeoutSeconds": args.timeout,
                "warmupReserveReleaseCycles": args.warmup_cycles,
                "repeatIndex": repeat,
                "repeats": args.repeats,
            },
            "achieved": {},
            "warmupSeconds": None,
            "measurementSeconds": None,
            "cooldownSeconds": 0,
            "datasetSeed": seed,
        }
        commands_file = run_dir / "commands.txt"
        lib.write_text(commands_file, "\n".join(commands) + "\n")
        raw_files: list[Path] = []
        final_files: list[Path] = []
        outcomes: list[dict[str, Any]] = []
        all_samples: list[dict[str, Any]] = []
        all_latencies: list[float] = []
        assertions: dict[str, Any] = {}
        started = time.perf_counter()
        try:
            warmup_result = warmup(base, seed, repeat, args.timeout, cycles=args.warmup_cycles)
            manifest["workload"]["warmupSeconds"] = (time.perf_counter() - started)
            for n in args.n:
                samples, outcome, raws, finals = run_one(
                    base, n=n, seed=seed, repeat_index=repeat, timeout=args.timeout,
                    run_dir=run_dir, report_grade=args.report_grade,
                )
                outcomes.append(outcome)
                raw_files.extend(raws)
                final_files.extend(finals)
                all_samples.extend(samples)
                all_latencies.extend(sample["latencyMs"] for sample in samples)
                assertions[f"n{n}"] = outcome["assertions"]
        except Exception as exc:  # noqa: BLE001 - classify harness failures honestly
            manifest["environment"] = environment
            manifest["workload"]["achieved"] = {"runs": outcomes, "warmup": warmup_result}
            lib.finalize_manifest(
                manifest, run_dir=run_dir, status="BLOCKED", reason=f"E01 run failed: {exc}",
                samples=[], latency_values=[], assertions=assertions,
                raw_files=[commands_file], final_state_files=[],
                commands=commands,
                extra_workload={
                    "achieved": {"runs": outcomes, "warmup": warmup_result},
                    "measurementSeconds": time.perf_counter() - started,
                },
                limitations=["BLOCKED is a harness/environment failure, never a performance result."],
                raw_retained_locally=True,
            )
            lib.print_json({"status": "BLOCKED", "runId": run_id, "reason": str(exc)})
            return 3

        all_pass = all(all(value for value in outcome["assertions"].values()) for outcome in outcomes)
        status = "PASS" if all_pass else "FAIL"
        reason = "all E01 assertions passed" if all_pass else "one or more E01 assertions failed"
        achieved = {
            "runs": outcomes,
            "warmup": warmup_result,
            "measurementSeconds": time.perf_counter() - started,
        }
        lib.finalize_manifest(
            manifest, run_dir=run_dir, status=status, reason=reason,
            samples=all_samples,
            latency_values=all_latencies, assertions=assertions,
            raw_files=raw_files + [commands_file], final_state_files=final_files,
            commands=commands,
            extra_workload={"achieved": achieved, "measurementSeconds": achieved["measurementSeconds"]},
            limitations=[
                "E01 measures the Driver reserve authority directly; HTTP 409 is an expected domain conflict.",
                "The current Driver surface has no authenticated principal (SEC-AUTH-01); this is a functional contention harness, not a security acceptance.",
                "Report-grade status additionally requires the campaign analyzer, at least 3 repeats and a green G4 gate.",
            ],
            raw_retained_locally=True,
        )
        results.append({"runId": run_id, "status": status, "outcomes": outcomes})
    summary = {"status": "PASS" if results and all(r["status"] == "PASS" for r in results) else "FAIL",
               "runs": results}
    lib.print_json(summary)
    return 0 if summary["status"] == "PASS" else 2


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description="PERF/E01 same-Driver contention harness")
    parser.add_argument("--base-url", default="http://127.0.0.1:18102")
    parser.add_argument("--seed", type=int, default=1301)
    parser.add_argument("--n", type=lambda value: [int(part) for part in value.split(",")],
                        default=list(DEFAULT_NS))
    parser.add_argument("--repeats", type=int, default=1)
    parser.add_argument("--timeout", type=float, default=30.0)
    parser.add_argument("--warmup-cycles", type=int, default=3)
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
