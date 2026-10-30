"""Fail-closed E04 artifact validation and observed-rate analysis."""
from __future__ import annotations

import json
import math
import statistics
import uuid
from datetime import datetime
from pathlib import Path

import w13lib as lib


def number(value, name: str, *, positive=False) -> float:
    if isinstance(value, bool) or not isinstance(value, (int, float)) or not math.isfinite(value):
        raise ValueError(f"{name} must be finite numeric data")
    if value < 0 or (positive and value == 0):
        raise ValueError(f"{name} is outside its valid range")
    return float(value)


def timestamp(value) -> datetime:
    parsed = datetime.fromisoformat(value.replace("Z", "+00:00"))
    if parsed.tzinfo is None:
        raise ValueError("timestamps require a timezone")
    return parsed


def identity(value) -> str:
    return str(uuid.UUID(value))


def artifact(run_dir: Path, item: dict):
    path = (run_dir / item["path"]).resolve()
    if not path.is_relative_to(run_dir.resolve()) or not path.is_file():
        raise ValueError("artifact is missing or escapes the run directory")
    if lib.sha256_file(path) != item["sha256"]:
        raise ValueError("artifact digest mismatch")
    return lib.read_json(path)


def validate_run(run_dir: Path) -> tuple[dict, dict]:
    manifest = lib.read_json(run_dir / "manifest.json")
    if (manifest["schemaVersion"] != 1 or manifest["experiment"] != "E04"
            or manifest["status"] != "PASS" or type(manifest["synthetic"]) is not bool
            or manifest["mode"] not in ("smoke", "report")):
        raise ValueError("invalid, incomplete or failed E04 manifest")
    source = manifest["source"]
    for key in ("commit", "tree"):
        if not isinstance(source[key], str) or len(source[key]) != 40:
            raise ValueError("missing source object identity")
    if source["dirty"] is not False or source["statusDigest"] != lib.sha256_text(""):
        raise ValueError("dirty or uncertain source provenance")
    if manifest["sourceAfter"] != source:
        raise ValueError("source changed during the experiment")
    tree = lib.run(["git", "rev-parse", f"{source['commit']}^{{tree}}"], check=True).stdout.strip()
    if tree != source["tree"]:
        raise ValueError("source commit/tree mismatch")
    environment = manifest["environment"]
    if manifest["fingerprint"] != lib.environment_fingerprint(environment):
        raise ValueError("environment fingerprint mismatch")
    if not manifest["synthetic"]:
        for key in ("host", "docker", "tooling", "configured", "observed"):
            if not environment.get(key):
                raise ValueError(f"missing observed environment {key}")
        containers = environment["observed"]["containers"]
        if not containers or any(not c.get("inspect", {}).get("image") for c in containers):
            raise ValueError("missing container image identity")
        for key in ("database", "broker", "redis"):
            if not environment["observed"].get(key) or "error" in environment["observed"][key]:
                raise ValueError(f"missing real provider observation: {key}")
        if not manifest.get("resources") or not manifest.get("environmentAfter"):
            raise ValueError("missing resources or post-recovery environment")
    samples = artifact(run_dir, manifest["artifacts"]["samples"])
    receipts = artifact(run_dir, manifest["artifacts"]["receipts"])
    final = artifact(run_dir, manifest["artifacts"]["final-state"])
    if len(samples) < 4 or not receipts:
        raise ValueError("missing backlog samples or publisher confirmations")
    outage = manifest["interruption"]
    start = number(outage["start"], "interruption.start")
    end = number(outage["end"], "interruption.end", positive=True)
    duration = end - start
    wall_duration = (timestamp(outage["endUtc"]) - timestamp(outage["startUtc"])).total_seconds()
    if duration <= 0 or abs(duration - wall_duration) > 0.5:
        raise ValueError("invalid interruption timestamps")
    steady = number(manifest["steadyElapsed"], "steadyElapsed")
    if steady <= end:
        raise ValueError("steady state precedes recovery")
    configured = manifest["configuration"]
    for key in ("rate", "outageSeconds", "baselineSeconds", "recoveryTimeout"):
        number(configured[key], key, positive=True)
    by_phase = {p: [s for s in samples if s["phase"] == p]
                for p in ("baseline", "outage", "recovery", "quiescence")}
    if any(not rows for rows in by_phase.values()):
        raise ValueError("missing baseline/outage/recovery/quiescence observations")
    previous = None
    for row in samples:
        for key in ("elapsed", "ready", "unacked", "consumers", "redeliver", "ack",
                    "intake", "terminal", "pending"):
            number(row[key], key)
        timestamp(row["atUtc"])
        if row["terminal"] + row["pending"] != row["intake"]:
            raise ValueError("durable work counts disagree")
        if row["type"] != "quorum":
            raise ValueError("expected actual quorum queue topology")
        if previous and (row["elapsed"] <= previous["elapsed"]
                         or timestamp(row["atUtc"]) <= timestamp(previous["atUtc"])
                         or row["terminal"] < previous["terminal"]
                         or row["intake"] < previous["intake"]):
            raise ValueError("nonmonotonic timestamps or durable counters")
        previous = row
    if by_phase["baseline"][-1]["terminal"] < 1:
        raise ValueError("no healthy baseline processing")
    if any(s["consumers"] != 0 for s in by_phase["outage"]):
        raise ValueError("consumer was not isolated during outage")
    ids = set()
    for receipt in receipts:
        if receipt["confirmed"] is not True or receipt.get("error"):
            raise ValueError("unconfirmed or uncertain publication")
        if receipt["messageId"] in ids:
            raise ValueError("duplicate scheduled identity")
        ids.add(receipt["messageId"])
        number(receipt["elapsed"], "receipt.elapsed")
        number(receipt["sentElapsed"], "receipt.sentElapsed")
        number(receipt["latencyMs"], "confirm latency")
        if timestamp(receipt["confirmedAtUtc"]) < timestamp(receipt["sentAtUtc"]):
            raise ValueError("confirmation precedes send")
    if manifest["duplicateReceipt"]["confirmed"] is not True:
        raise ValueError("duplicate replay was not confirmed")
    if manifest["duplicateReceipt"]["messageId"] not in ids:
        raise ValueError("duplicate replay changed message identity")
    rounds = final["rounds"]
    if (len(rounds) != len(receipts) or {r["input_message_id"] for r in rounds} != ids
            or len({(r["trip_id"], r["matching_round"]) for r in rounds}) != len(receipts)):
        raise ValueError("authoritative intake identities do not match confirmed work")
    trip_by_message = {r["messageId"]: r["tripId"] for r in receipts}
    outbox_by_id = {identity(o["message_id"]): o for o in final["matchingOutbox"]}
    if len(final["matchingOutbox"]) != len(receipts) or len(outbox_by_id) != len(receipts):
        raise ValueError("duplicate or missing Outbox business effects")
    for row in rounds:
        if (row["trip_id"] != trip_by_message[row["input_message_id"]]
                or row["status"] != "NO_DRIVER" or row["auto_execute"] is not True
                or row["selected_driver_id"] is not None or row["selected_reservation_id"] is not None):
            raise ValueError("false terminal state or changed work identity")
        event = outbox_by_id[identity(row["result_message_id"])]
        if (event["status"] != "PUBLISHED" or not event["published_at"]
                or event["message_type"] != "MatchingFailedV1"
                or event["payload_json"]["tripId"] != row["trip_id"]):
            raise ValueError("terminal result lacks confirmed matching Outbox publication")
    if final["attempts"] or final["drivers"] != manifest["driverBefore"] or final["driverOutbox"]:
        raise ValueError("empty-dataset experiment fabricated Driver work or changed authority")
    audit = manifest["auditor"]
    if audit["sourceCommit"] != source["commit"] or audit["sourceTree"] != source["tree"]:
        raise ValueError("auditor source is not the experiment source")
    checks = {c["checkId"]: c["status"] for c in audit["checks"]}
    if any(checks.get(key) != "PASS" for key in ("AUD-01", "AUD-02", "AUD-06", "AUD-08")):
        raise ValueError("B-owned run-dataset auditor did not pass")
    if any(value in ("FAIL", "BLOCKED") for value in checks.values()):
        raise ValueError("auditor failure or unavailable authority")
    if samples[-1]["ready"] or samples[-1]["unacked"] or samples[-1]["pending"]:
        raise ValueError("broker/application work has not quiesced")
    outage_arrivals = [r for r in receipts if start <= r["sentElapsed"] < end]
    if not outage_arrivals or max(r["ready"] for r in by_phase["outage"]) == 0:
        raise ValueError("no observed durable accumulation")
    incoming = len(outage_arrivals) / duration
    recovery = [s for s in samples if s["phase"] == "recovery" and s["elapsed"] <= steady]
    if len(recovery) < 2:
        raise ValueError("insufficient recovery throughput observations")
    elapsed = recovery[-1]["elapsed"] - recovery[0]["elapsed"]
    mu = (recovery[-1]["terminal"] - recovery[0]["terminal"]) / elapsed
    recovery_incoming = sum(recovery[0]["elapsed"] <= r["sentElapsed"] < recovery[-1]["elapsed"]
                            for r in receipts) / elapsed
    interval_rates = [(b["terminal"] - a["terminal"]) / (b["elapsed"] - a["elapsed"])
                      for a, b in zip(recovery, recovery[1:])]
    peak_ready = max(s["ready"] for s in samples)
    outstanding = [max(0, sum(r["elapsed"] <= s["elapsed"] for r in receipts) - s["terminal"])
                   for s in samples]
    backlog = max(outstanding)
    first = next(s for s in recovery if s["terminal"] > by_phase["outage"][-1]["terminal"])
    broker_zero = next((s["elapsed"] - end for s in recovery if s["ready"] == s["unacked"] == 0), None)
    latency = [r["latencyMs"] for r in receipts]
    return manifest, {
        "confirmedPublications": len(receipts), "samples": len(samples),
        "lambdaOutage": incoming, "lambdaRecovery": recovery_incoming,
        "T": duration, "predictedBacklog": incoming * duration,
        "peakReady": peak_ready, "peakUnacked": max(s["unacked"] for s in samples),
        "peakOutstandingBusinessWork": backlog,
        "mu": mu, "muIntervals": interval_rates,
        "muIntervalRange": [min(interval_rates), max(interval_rates)],
        "drainableUnderMeasuredWorkload": mu > recovery_incoming,
        "predictedDrainSeconds": backlog / (mu - recovery_incoming) if mu > recovery_incoming else None,
        "observedSteadySeconds": steady - end,
        "firstSuccessfulProcessingSeconds": first["elapsed"] - end,
        "brokerZeroSeconds": broker_zero,
        "applicationTerminalSeconds": samples[-1]["elapsed"] - end,
        "redeliverDelta": samples[-1]["redeliver"] - samples[0]["redeliver"],
        "explicitDuplicateDeliveries": 1, "duplicateBusinessEffects": 0,
        "confirmLatencyMs": {"n": len(latency), "p50": lib.percentile_nearest_rank(latency, 50),
                             "p95": lib.percentile_nearest_rank(latency, 95)},
        "auditor": checks,
        "limitations": ["Empty Driver dataset proves real NO_DRIVER terminal work, not the full Trip/Saga path.",
            "Queue counters are broker management samples; completion timing has polling resolution.",
            "Interval μ varies; the formula is an approximation including sampled startup/reconnect effects.",
            "Manual duplicate replay is distinct from broker redelivery; REL-15 tests prove the latter."]}


def analyze_group(run_dirs: list[Path]) -> dict:
    errors, runs, metrics = [], [], []
    for path in run_dirs:
        try:
            manifest, measured = validate_run(path)
            runs.append(manifest)
            metrics.append(measured)
        except (KeyError, ValueError, TypeError, OSError, StopIteration, lib.HarnessError) as exc:
            errors.append(f"{path.name}: {type(exc).__name__}: {exc}")
    if not run_dirs:
        errors.append("missing run group")
    if errors:
        return {"status": "INVALID", "errors": errors, "runs": metrics}
    first = runs[0]
    for run in runs:
        for key in ("group", "source", "configuration", "fingerprint", "mode", "synthetic", "plannedRepeats"):
            if run[key] != first[key]:
                errors.append(f"inconsistent run group {key}")
    if sorted(r["repeat"] for r in runs) != list(range(1, first["plannedRepeats"] + 1)):
        errors.append("incomplete or duplicated run repetitions")
    if first["mode"] == "report":
        if len(runs) < 3:
            errors.append("report-grade E04 requires at least three measured repetitions")
        if any(not r["g4"].get("green") for r in runs) or not lib.g4_readiness()["green"]:
            errors.append("live G4/experiment gate is not GREEN")
        drift = lib.run(["git", "diff", "--quiet", first["source"]["commit"], "HEAD", "--", ".",
                         ":(exclude)evidence"])
        if drift.returncode or lib.source_binding()["dirty"]:
            errors.append("source is dirty or differs from measured source")
    status = "INVALID" if errors else ("SYNTHETIC_ONLY" if first["synthetic"] else
             "ACCEPTED" if first["mode"] == "report" else "SMOKE_ONLY")
    stats = {}
    for key in ("lambdaOutage", "lambdaRecovery", "T", "peakReady", "mu", "observedSteadySeconds"):
        values = [r[key] for r in metrics]
        stats[key] = {"runs": len(values), "median": statistics.median(values),
                      "min": min(values), "max": max(values),
                      "sampleVariance": statistics.variance(values) if len(values) > 1 else None}
    return {"status": status, "errors": errors, "runs": metrics, "statistics": stats}
