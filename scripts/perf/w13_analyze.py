"""W13 campaign analysis and validity gate.

Design rules:
- individual run manifests are the unit of evidence; nothing is aggregated
  from a missing or invalid run;
- a percentile always carries its sample count;
- configured and achieved load are separate fields;
- expected domain conflicts are never folded into system errors;
- report-grade acceptance needs >= 3 repeats, a green G4 gate, an auditor
  PASS and complete canonical configurations;
- smoke evidence can be valid harness evidence but can NEVER become an
  acceptance result.
"""

from __future__ import annotations

import argparse
import json
import statistics
from collections import defaultdict
from pathlib import Path
from typing import Any, Callable

import w13lib as lib

CANONICAL_NS = (1, 10, 25, 50, 100)
CANONICAL_RATES = (10, 25, 50, 100)
CANONICAL_REPLICAS = (1, 2, 4)


def _numeric(values: list[float | int | None]) -> dict[str, Any]:
    usable = [float(value) for value in values if value is not None]
    if not usable:
        return {"count": 0, "median": None, "min": None, "max": None, "range": None}
    return {
        "count": len(usable),
        "median": statistics.median(usable),
        "min": min(usable),
        "max": max(usable),
        "range": max(usable) - min(usable),
    }


def _auditor_status(manifest: dict[str, Any]) -> str:
    auditor = manifest.get("auditor") or {}
    if isinstance(auditor, dict) and auditor.get("status"):
        return str(auditor["status"])
    assertions = (manifest.get("result") or {}).get("assertions") or {}
    if assertions.get("auditorPass") is True:
        return "PASS"
    return "MISSING"


def _final_state_payloads(manifest: dict[str, Any]) -> list[dict[str, Any]]:
    recorded = (manifest.get("artifacts") or {}).get("runDirectory") or ""
    run_directory = lib.resolve_run_directory(recorded) if recorded else Path("")
    payloads = []
    for relative in (manifest.get("artifacts") or {}).get("finalState", []):
        path = run_directory / relative
        if not path.exists():
            continue
        try:
            data = lib.read_json(path)
        except Exception:  # noqa: BLE001 - analysis tolerates a broken optional artifact
            continue
        if isinstance(data, dict):
            payloads.append(data)
    return payloads


def _load(experiment: str, runs_root: Path) -> tuple[list[dict[str, Any]], list[str]]:
    manifests = []
    errors = []
    for run_dir in lib.campaign_runs(experiment, runs_root):
        manifest, run_errors = lib.load_run(run_dir)
        if manifest is None:
            errors.extend(run_errors)
            continue
        if run_errors:
            errors.extend(f"{run_dir.name}: {error}" for error in run_errors)
            continue
        manifests.append(manifest)
    return manifests, errors


def _base_result(experiment: str, manifests: list[dict[str, Any]], errors: list[str]) -> dict[str, Any]:
    report_grades = {bool(manifest.get("reportGrade")) for manifest in manifests}
    live_g4 = lib.g4_readiness()
    return {
        "experiment": experiment,
        "runCount": len(manifests),
        "reportGrade": (report_grades.pop() if len(report_grades) == 1 else None),
        "mixedReportGrade": len(report_grades) > 1,
        "manifestErrors": errors,
        "g4Green": bool(live_g4.get("green")) if manifests else False,
        "liveG4": live_g4,
        "manifestG4Mismatch": any(
            bool((manifest.get("g4") or {}).get("green")) != bool(live_g4.get("green"))
            for manifest in manifests
        ),
        "sourceCommits": sorted({(manifest.get("source") or {}).get("commit") for manifest in manifests}),
        "environmentFingerprints": sorted({
            lib.environment_fingerprint(manifest.get("environment") or {}) for manifest in manifests
        }),
        "auditor": {
            "statuses": [_auditor_status(manifest) for manifest in manifests],
            "allPass": bool(manifests) and all(_auditor_status(manifest) == "PASS" for manifest in manifests),
        },
        "synthetic": any(manifest.get("synthetic") is True for manifest in manifests),
        "reasons": [],
    }


def _acceptance_gate(result: dict[str, Any], manifests: list[dict[str, Any]], min_repeats: int,
                     *, extra_reasons: list[str] | None = None) -> None:
    reasons = list(result.get("reasons") or [])
    if result["manifestErrors"]:
        reasons.append("one or more run manifests are missing or invalid")
    if result["mixedReportGrade"]:
        reasons.append("campaign mixes report-grade and smoke runs")
    if result["reportGrade"]:
        if not result["g4Green"]:
            reasons.append("the live G4 gate is not green for report-grade analysis")
        if result.get("manifestG4Mismatch"):
            reasons.append("a manifest's recorded G4 verdict disagrees with the live G4 gate")
        if len(result["sourceCommits"]) != 1:
            reasons.append("report-grade runs were not all measured from the same source commit")
        dirty = [
            manifest for manifest in manifests
            if (manifest.get("source") or {}).get("dirty") is True
        ]
        if dirty:
            reasons.append("report-grade runs were measured from a dirty source tree")
        if len(result["environmentFingerprints"]) != 1:
            reasons.append("report-grade runs did not share one configured environment fingerprint")
        if not result["auditor"]["allPass"]:
            reasons.append("the independent scoped authority auditor did not PASS for every run")
    if extra_reasons:
        reasons.extend(extra_reasons)
    statuses = [(manifest.get("result") or {}).get("status") for manifest in manifests]
    non_pass = [status for status in statuses if status != "PASS"]
    if non_pass:
        reasons.append(f"{len(non_pass)} run(s) did not complete with PASS: {sorted(set(non_pass))}")
    if result.get("synthetic"):
        reasons.append("synthetic fixtures cannot establish acceptance; they only exercise parser/analyzer logic")
    result["reasons"] = reasons


def analyze_e01(manifests: list[dict[str, Any]], errors: list[str], min_repeats: int) -> dict[str, Any]:
    result = _base_result("E01", manifests, errors)
    grouped: dict[int, list[dict[str, Any]]] = defaultdict(list)
    for manifest in manifests:
        for outcome in ((manifest.get("workload") or {}).get("achieved") or {}).get("runs", []):
            if isinstance(outcome, dict) and isinstance(outcome.get("n"), int):
                grouped[outcome["n"]].append({"manifest": manifest, "outcome": outcome})
    groups = []
    reasons = []
    for n in sorted(grouped):
        entries = grouped[n]
        outcomes = [entry["outcome"] for entry in entries]
        assertions = [bool(all((outcome.get("assertions") or {}).values())) for outcome in outcomes]
        winners = [outcome.get("winnerCount") for outcome in outcomes]
        conflicts = [outcome.get("conflictCount") for outcome in outcomes]
        errors_count = [outcome.get("systemErrorCount") for outcome in outcomes]
        aud01 = [outcome.get("aud01") for outcome in outcomes]
        group = {
            "n": n,
            "runCount": len(entries),
            "sampleCount": sum(int(outcome.get("sampleCount") or 0) for outcome in outcomes),
            "winnerCounts": winners,
            "conflictCounts": conflicts,
            "systemErrorCounts": errors_count,
            "aud01": aud01,
            "assertionsAllPass": all(assertions),
            "p50Ms": _numeric([outcome.get("p50Ms") for outcome in outcomes]),
            "p95Ms": _numeric([outcome.get("p95Ms") for outcome in outcomes]),
            "wallMs": _numeric([outcome.get("wallMs") for outcome in outcomes]),
            "barrierSpreadMs": _numeric([outcome.get("barrierSpreadMs") for outcome in outcomes]),
        }
        if not group["assertionsAllPass"]:
            reasons.append(f"N={n}: a run failed its contention assertions")
        if any(value != 1 for value in winners if value is not None):
            reasons.append(f"N={n}: winner count was not exactly 1 in every run")
        if any(int(value or 0) != 0 for value in errors_count):
            reasons.append(f"N={n}: system errors were observed")
        if any(value != "PASS" for value in aud01):
            reasons.append(f"N={n}: AUD-01 was not PASS in every run")
        if result["reportGrade"] and len(entries) < min_repeats:
            reasons.append(f"N={n}: only {len(entries)} run(s); report-grade requires {min_repeats}")
        groups.append(group)
    missing_ns = [n for n in CANONICAL_NS if n not in grouped]
    result["missingCanonicalPoints"] = missing_ns
    if missing_ns and result["reportGrade"]:
        reasons.append(f"missing canonical contention points: {missing_ns}")
    statuses = [(manifest.get("result") or {}).get("status") for manifest in manifests]
    failures = [status for status in statuses if status in ("FAIL", "INVALID", "INCOMPLETE")]
    blocked = [status for status in statuses if status == "BLOCKED"]
    if result["manifestErrors"]:
        verdict = "INVALID"
    elif not manifests:
        verdict = "NOT_RUN"
    elif failures:
        verdict = "FAIL"
    elif blocked and len(blocked) == len(statuses):
        verdict = "BLOCKED"
    elif result["reportGrade"] and result["reasons"]:
        verdict = "INCOMPLETE"
    elif result["reportGrade"]:
        verdict = "ACCEPTED" if not reasons else "INCOMPLETE"
    else:
        verdict = "SMOKE_PASS" if not reasons else "SMOKE_FAIL"
    result.update({"groups": groups, "verdict": verdict, "reasons": result.get("reasons", []) + reasons})
    _acceptance_gate(result, manifests, min_repeats)
    if result["reasons"] and verdict == "ACCEPTED":
        result["verdict"] = "INCOMPLETE"
    if result.get("synthetic") and result["verdict"] in ("ACCEPTED", "SMOKE_PASS"):
        result["verdict"] = "SYNTHETIC_ONLY"
    result["acceptance"] = result["verdict"] == "ACCEPTED"
    return result


def analyze_e02(manifests: list[dict[str, Any]], errors: list[str], min_repeats: int,
                expected_rates: tuple[int, ...]) -> dict[str, Any]:
    result = _base_result("E02", manifests, errors)
    grouped: dict[float, list[dict[str, Any]]] = defaultdict(list)
    for manifest in manifests:
        if not ((manifest.get("workload") or {}).get("achieved") or {}):
            continue
        configured = ((manifest.get("workload") or {}).get("configured") or {})
        rate = configured.get("configuredRatePerSecond")
        if isinstance(rate, (int, float)) and rate > 0:
            grouped[float(rate)].append(manifest)
    groups = []
    reasons = []
    no_data = [
        manifest for manifest in manifests
        if not ((manifest.get("workload") or {}).get("achieved") or {})
    ]
    if no_data:
        reasons.append(f"{len(no_data)} run(s) have no achieved workload data")
    for rate in sorted(grouped):
        runs = grouped[rate]
        achieved = [((manifest.get("workload") or {}).get("achieved") or {}) for manifest in runs]
        ratios = [
            (item.get("achievedRatePerSecond") or 0) / rate
            for item in achieved
            if item.get("achievedRatePerSecond") is not None
        ]
        p50 = _numeric([item.get("p50Ms") for item in achieved])
        p95 = _numeric([item.get("p95Ms") for item in achieved])
        group = {
            "configuredRatePerSecond": rate,
            "runCount": len(runs),
            "sampleCount": sum(int(item.get("sampleCount") or 0) for item in achieved),
            "achievedRatePerSecond": _numeric([item.get("achievedRatePerSecond") for item in achieved]),
            "achievedRatio": _numeric(ratios),
            "p50Ms": p50,
            "p95Ms": p95,
            "droppedIterations": _numeric([item.get("droppedIterations") for item in achieved]),
            "domainConflicts": _numeric([item.get("domainConflictCount") for item in achieved]),
            "systemErrors": _numeric([item.get("systemErrorCount") for item in achieved]),
            "authFailures": _numeric([item.get("authFailureCount") for item in achieved]),
            "unexpected": _numeric([item.get("unexpectedCount") for item in achieved]),
            "finalState": [payload.get("finalState") for manifest in runs for payload in _final_state_payloads(manifest)],
        }
        finals = group["finalState"]
        success_counts = [int(item.get("successCount") or 0) for item in achieved]
        group["successCounts"] = success_counts
        group["latencyMethod"] = "k6 summary trend percentiles per run; not pooled"
        if result["reportGrade"] and not finals:
            reasons.append(f"rate {rate:g}/s: authoritative Trip final state is missing")
        if finals:
            if any(not item or item.get("error") for item in finals):
                reasons.append(f"rate {rate:g}/s: authoritative Trip final state is unreadable or errored")
            if any(str(item.get("activeTripsPerRiderViolations")) != "0" for item in finals if item):
                reasons.append(f"rate {rate:g}/s: active-Trip-per-Rider violations were observed")
            if any(str(item.get("duplicateIdempotencyResources")) != "0" for item in finals if item):
                reasons.append(f"rate {rate:g}/s: duplicate idempotency resources were observed")
            if len(finals) == len(success_counts):
                for final, successes in zip(finals, success_counts):
                    if str(final.get("tripCount")) != str(successes):
                        reasons.append(f"rate {rate:g}/s: Trip count does not match successful requests")
                    if str(final.get("e02IdempotencyKeys")) != str(successes):
                        reasons.append(f"rate {rate:g}/s: idempotency count does not match successful requests")
        if any(count == 0 for count in success_counts):
            reasons.append(f"rate {rate:g}/s: a run produced zero successful requests")
        if result["reportGrade"] and len(runs) < min_repeats:
            reasons.append(f"rate {rate:g}/s: only {len(runs)} run(s); report-grade requires {min_repeats}")
        if p50["count"] == 0 or p95["count"] == 0:
            reasons.append(f"rate {rate:g}/s: latency percentiles are missing")
        if any((item.get("authFailureCount") or 0) > 0 for item in achieved):
            reasons.append(f"rate {rate:g}/s: authentication failures occurred during load")
        if any((item.get("unexpectedCount") or 0) > 0 for item in achieved):
            reasons.append(f"rate {rate:g}/s: unexpected HTTP outcomes occurred")
        if any((item.get("systemErrorCount") or 0) > 0 for item in achieved):
            reasons.append(f"rate {rate:g}/s: system errors occurred and require review")
        if any((item.get("droppedIterations") or 0) > 0 for item in achieved):
            reasons.append(f"rate {rate:g}/s: dropped iterations occurred; generator vs SUT saturation must be interpreted")
        if ratios and min(ratios) < 0.9:
            reasons.append(f"rate {rate:g}/s: achieved load was below 90% of the configured rate")
        groups.append(group)
    missing_rates = [rate for rate in expected_rates if float(rate) not in grouped]
    result["missingCanonicalRates"] = missing_rates
    if missing_rates and result["reportGrade"]:
        reasons.append(f"missing canonical offered-load rates: {missing_rates}")
    statuses = [(manifest.get("result") or {}).get("status") for manifest in manifests]
    if result["manifestErrors"]:
        verdict = "INVALID"
    elif not manifests:
        verdict = "NOT_RUN"
    elif any(status in ("FAIL", "INVALID", "INCOMPLETE") for status in statuses):
        verdict = "FAIL"
    elif statuses and all(status == "BLOCKED" for status in statuses):
        verdict = "BLOCKED"
    elif statuses and any(status != "PASS" for status in statuses):
        verdict = "FAIL"
    elif result["reportGrade"] and (reasons or not result["g4Green"]):
        verdict = "INCOMPLETE"
    elif result["reportGrade"]:
        verdict = "ACCEPTED"
    else:
        verdict = "SMOKE_PASS" if not reasons else "SMOKE_FAIL"
    result.update({"groups": groups, "verdict": verdict, "reasons": result.get("reasons", []) + reasons})
    _acceptance_gate(result, manifests, min_repeats)
    if result["reasons"] and verdict == "ACCEPTED":
        result["verdict"] = "INCOMPLETE"
    if result.get("synthetic") and result["verdict"] in ("ACCEPTED", "SMOKE_PASS"):
        result["verdict"] = "SYNTHETIC_ONLY"
    result["acceptance"] = result["verdict"] == "ACCEPTED"
    return result


def analyze_e03(manifests: list[dict[str, Any]], errors: list[str], min_repeats: int) -> dict[str, Any]:
    result = _base_result("E03", manifests, errors)
    grouped: dict[int, list[dict[str, Any]]] = defaultdict(list)
    no_data = [
        manifest for manifest in manifests
        if not ((manifest.get("workload") or {}).get("achieved") or {})
    ]
    for manifest in manifests:
        if not ((manifest.get("workload") or {}).get("achieved") or {}):
            continue
        configured = ((manifest.get("workload") or {}).get("configured") or {})
        replicas = configured.get("replicas")
        if isinstance(replicas, int):
            grouped[replicas].append(manifest)
    groups = []
    reasons = []
    if no_data:
        reasons.append(f"{len(no_data)} run(s) have no achieved workload data")
    for replicas in sorted(grouped):
        runs = grouped[replicas]
        achieved = [((manifest.get("workload") or {}).get("achieved") or {}) for manifest in runs]
        group = {
            "replicas": replicas,
            "runCount": len(runs),
            "sampleCount": sum(int(item.get("deliveries") or 0) for item in achieved),
            "configuredRatePerSecond": _numeric([item.get("configuredRatePerSecond") for item in achieved]),
            "achievedSendRatePerSecond": _numeric([item.get("achievedSendRatePerSecond") for item in achieved]),
            "achievedConsumeRatePerSecond": _numeric([item.get("achievedConsumeRatePerSecond") for item in achieved]),
            "p50IntakeLatencyMs": _numeric([item.get("p50IntakeLatencyMs") for item in achieved]),
            "p95IntakeLatencyMs": _numeric([item.get("p95IntakeLatencyMs") for item in achieved]),
            "queuePeakMessages": _numeric([item.get("queuePeakMessages") for item in achieved]),
            "redeliveryDelta": _numeric([item.get("redeliveryDelta") for item in achieved]),
            "perReplicaNonzero": all(
                all(int(item.get("measuredConsumedLines") or item.get("consumedLines") or 0) > 0
                    for item in (entry.get("perReplicaConsumed") or []))
                for entry in achieved
            ) if replicas > 1 else True,
            "workerMode": sorted({str(((manifest.get("workload") or {}).get("configured") or {}).get("workerMode"))
                                  for manifest in runs}),
        }
        if result["reportGrade"] and len(runs) < min_repeats:
            reasons.append(f"replicas={replicas}: only {len(runs)} run(s); report-grade requires {min_repeats}")
        if not all(bool((manifest.get("result") or {}).get("assertions")) for manifest in runs):
            reasons.append(f"replicas={replicas}: missing result assertions")
        if any(not all(((manifest.get("result") or {}).get("assertions") or {}).values()) for manifest in runs):
            reasons.append(f"replicas={replicas}: one or more consumer-scaling assertions failed")
        if not group["perReplicaNonzero"]:
            reasons.append(f"replicas={replicas}: at least one replica consumed no work")
        if group["p50IntakeLatencyMs"]["count"] == 0 or group["p95IntakeLatencyMs"]["count"] == 0:
            reasons.append(f"replicas={replicas}: intake latency percentiles are missing")
        if any((item.get("redeliveryDelta") or 0) > 0 for item in achieved):
            reasons.append(f"replicas={replicas}: redelivery was observed and requires review")
        if result["reportGrade"]:
            for item in achieved:
                if int(item.get("deliveries") or 0) < 100:
                    reasons.append(f"replicas={replicas}: fewer than 100 measured messages")
                configured_rate = float(item.get("configuredRatePerSecond") or 0)
                achieved_rate = float(item.get("achievedSendRatePerSecond") or 0)
                if configured_rate <= 0 or achieved_rate / configured_rate < 0.9:
                    reasons.append(f"replicas={replicas}: achieved send rate is below 90% of configured")
                if (item.get("missingLatencySamples") or 0) != 0:
                    reasons.append(f"replicas={replicas}: measured latency samples are missing")
                measured = item.get("perReplicaMeasuredConsumed") or []
                if sum(int(value or 0) for value in measured) != int(item.get("deliveries") or 0):
                    reasons.append(f"replicas={replicas}: per-replica measured work does not sum to unique messages")
            if any(str(mode) != "disabled" for mode in group["workerMode"]):
                reasons.append(f"replicas={replicas}: worker-enabled mode is not an isolated consumer-scaling configuration")
        groups.append(group)
    missing_replicas = [value for value in CANONICAL_REPLICAS if value not in grouped]
    result["missingCanonicalReplicas"] = missing_replicas
    if missing_replicas and result["reportGrade"]:
        reasons.append(f"missing canonical replica points: {missing_replicas}")
    statuses = [(manifest.get("result") or {}).get("status") for manifest in manifests]
    if result["manifestErrors"]:
        verdict = "INVALID"
    elif not manifests:
        verdict = "NOT_RUN"
    elif any(status in ("FAIL", "INVALID", "INCOMPLETE") for status in statuses):
        verdict = "FAIL"
    elif statuses and all(status == "BLOCKED" for status in statuses):
        verdict = "BLOCKED"
    elif statuses and any(status != "PASS" for status in statuses):
        verdict = "FAIL"
    elif result["reportGrade"] and (reasons or not result["g4Green"]):
        verdict = "INCOMPLETE"
    elif result["reportGrade"]:
        verdict = "ACCEPTED"
    else:
        verdict = "SMOKE_PASS" if not reasons else "SMOKE_FAIL"
    result.update({"groups": groups, "verdict": verdict, "reasons": result.get("reasons", []) + reasons})
    _acceptance_gate(result, manifests, min_repeats)
    if result["reasons"] and verdict == "ACCEPTED":
        result["verdict"] = "INCOMPLETE"
    if result.get("synthetic") and result["verdict"] in ("ACCEPTED", "SMOKE_PASS"):
        result["verdict"] = "SYNTHETIC_ONLY"
    result["acceptance"] = result["verdict"] == "ACCEPTED"
    return result


def markdown(result: dict[str, Any]) -> str:
    lines = [
        f"# W13 analysis — {result['experiment']}",
        "",
        f"Verdict: **{result['verdict']}**",
        "",
        f"Acceptance: **{'YES' if result.get('acceptance') else 'NO'}**",
        "",
        f"Runs: {result['runCount']} · reportGrade: {result.get('reportGrade')} · G4 green: {result.get('g4Green')}",
        "",
    ]
    if result.get("reasons"):
        lines.append("## Reasons")
        lines.extend(f"- {reason}" for reason in result["reasons"])
        lines.append("")
    lines.append("## Groups")
    if not result.get("groups"):
        lines.append("No measured groups.")
    else:
        lines.append("```json")
        lines.append(json.dumps(result["groups"], indent=2))
        lines.append("```")
    lines.append("")
    lines.append("## Validity notes")
    lines.append("- Domain conflicts are reported separately from system errors.")
    lines.append("- P50/P95 are nearest-rank percentiles with sample counts.")
    lines.append("- Smoke evidence can never become an acceptance result.")
    return "\n".join(lines) + "\n"


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="W13 campaign analysis")
    parser.add_argument("--experiment", choices=("E01", "E02", "E03", "ALL"), default="ALL")
    parser.add_argument("--runs-root", type=Path, default=lib.RUNS_ROOT)
    parser.add_argument("--out-dir", type=Path, default=lib.EVIDENCE_ROOT / "analysis")
    parser.add_argument("--min-repeats", type=int, default=3)
    parser.add_argument("--expected-rates", type=lambda value: tuple(int(part) for part in value.split(",")),
                        default=CANONICAL_RATES)
    args = parser.parse_args(argv)
    min_repeats = max(args.min_repeats, 3)
    expected_rates = tuple(sorted(set(args.expected_rates) | set(CANONICAL_RATES)))

    experiments = list(lib.EXPERIMENTS) if args.experiment == "ALL" else [args.experiment]
    all_results = []
    exit_code = 0
    for experiment in experiments:
        manifests, errors = _load(experiment, args.runs_root)
        if experiment == "E01":
            result = analyze_e01(manifests, errors, min_repeats)
        elif experiment == "E02":
            result = analyze_e02(manifests, errors, min_repeats, expected_rates)
        else:
            result = analyze_e03(manifests, errors, min_repeats)
        result["generatedAtUtc"] = lib.iso()
        all_results.append(result)
        args.out_dir.mkdir(parents=True, exist_ok=True)
        lib.write_json(args.out_dir / f"{experiment.lower()}-analysis.json", result)
        lib.write_text(args.out_dir / f"{experiment.lower()}-analysis.md", markdown(result))
        if result["verdict"] not in ("ACCEPTED", "SMOKE_PASS", "NOT_RUN"):
            exit_code = 3 if result["verdict"] == "BLOCKED" else 2
    campaign_accepted = bool(all_results) and all(result.get("acceptance") for result in all_results)
    lib.write_json(args.out_dir / "campaign-analysis.json", {
        "schemaVersion": 1,
        "generatedAtUtc": lib.iso(),
        "experiments": all_results,
        "verdict": "ACCEPTED" if campaign_accepted else "NOT_ACCEPTED",
        "acceptance": campaign_accepted,
        "latencyMethod": {
            "E01": "nearest-rank over per-request latencies",
            "E02": "k6 summary trend percentiles per run; group values are medians of per-run percentiles",
            "E03": "nearest-rank over per-message intake latencies",
        },
    })
    lib.print_json({"verdicts": {result["experiment"]: result["verdict"] for result in all_results},
                    "outDir": str(args.out_dir)})
    return exit_code


if __name__ == "__main__":
    raise SystemExit(main())
