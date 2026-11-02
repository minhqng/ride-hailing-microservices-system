"""Capture bounded W13 broker/application metric snapshots from Prometheus."""
from __future__ import annotations

import argparse
import datetime as dt
import json
import math
import pathlib
import urllib.parse
import urllib.request

QUERIES = {
    "queue_ready": "sum by (queue) (rabbitmq_detailed_queue_messages_ready)",
    "queue_unacked": "sum by (queue) (rabbitmq_detailed_queue_messages_unacked)",
    "redelivery_rate": "sum by (queue) (rate(rabbitmq_detailed_queue_messages_redelivered_total[1m]))",
    "consumer_delivery_rate": "sum(rate({__name__=~'consumer_delivery(_messages)?_total'}[1m]))",
    "consumer_redelivery_rate": "sum(rate({__name__=~'consumer_redelivery(_messages)?_total'}[1m]))",
    "dedup_rate": "sum by (outcome) (rate({__name__=~'dedup_processed(_messages)?_total'}[1m]))",
    "outbox_pending": "sum({__name__=~'outbox_pending(_messages)?'})",
    "outbox_oldest_pending_age": "max({__name__=~'outbox_oldest_pending_age(_seconds)?'})",
    "saga_active": "sum by (step) ({__name__=~'saga_active(_sagas)?'})",
    "trip_transition_rejected_rate": "sum by (reason) (rate({__name__=~'trip_transition_rejected(_transitions)?_total'}[1m]))",
    "payment_rate": "sum by (type,outcome) (rate({__name__=~'payment_operation(_operations)?_total'}[1m]))",
}

parser = argparse.ArgumentParser()
parser.add_argument("--prometheus", default="http://localhost:9090")
parser.add_argument("--output", required=True, type=pathlib.Path)
parser.add_argument("--run", required=True, help="Run label such as e03-replica2-run1")
parser.add_argument("--queries", nargs="+", choices=sorted(QUERIES), default=list(QUERIES),
                    help="Declare the measured scope explicitly; every selected series must exist and be finite")
parser.add_argument("--start", help="UTC start for a measured range (requires --end)")
parser.add_argument("--end", help="UTC end for a measured range")
args = parser.parse_args()
if bool(args.start) != bool(args.end):
    parser.error("--start and --end must be provided together")
REQUIRED_QUERIES = set(args.queries)


def finite_series(result: list[dict]) -> bool:
    for series in result:
        values = series.get("values", [series.get("value")])
        if not values:
            return False
        for value in values:
            if not isinstance(value, list) or len(value) < 2:
                return False
            try:
                number = float(value[1])
            except (TypeError, ValueError):
                return False
            if not math.isfinite(number):
                return False
    return True

snapshot = {
    "run": args.run,
    "capturedAt": dt.datetime.now(dt.timezone.utc).isoformat(),
    "policy": "consumer count may vary; prefetch/DB pool/retry policy must remain fixed",
    "requiredQueries": sorted(REQUIRED_QUERIES),
    "range": {"start": args.start, "end": args.end, "stepSeconds": 15} if args.start else None,
    "queries": {},
}
empty_queries: list[str] = []
invalid_queries: list[str] = []
for name, query in QUERIES.items():
    if name not in REQUIRED_QUERIES:
        continue
    parameters = {"query": query}
    endpoint = "/api/v1/query"
    if args.start:
        endpoint += "_range"
        parameters.update(start=args.start, end=args.end, step="15s")
    url = args.prometheus.rstrip("/") + endpoint + "?" + urllib.parse.urlencode(parameters)
    with urllib.request.urlopen(url, timeout=10) as response:
        payload = json.load(response)
    if payload.get("status") != "success":
        raise RuntimeError(f"Prometheus query failed: {name}")
    result = payload["data"]["result"]
    if not isinstance(result, list):
        raise RuntimeError(f"Prometheus returned a non-vector result for: {name}")
    if not result:
        empty_queries.append(name)
    if result and not finite_series(result):
        invalid_queries.append(name)
    snapshot["queries"][name] = {
        "query": query,
        "required": name in REQUIRED_QUERIES,
        "empty": not result,
        "result": result,
    }

missing_required = sorted(set(empty_queries) & REQUIRED_QUERIES)
invalid_required = sorted(set(invalid_queries) & REQUIRED_QUERIES)
snapshot["emptyQueries"] = empty_queries
snapshot["invalidQueries"] = invalid_queries
snapshot["verdict"] = "FAIL" if missing_required or invalid_required else "PASS"

args.output.parent.mkdir(parents=True, exist_ok=True)
args.output.write_text(json.dumps(snapshot, indent=2) + "\n", encoding="utf-8")

if missing_required or invalid_required:
    details = []
    if missing_required:
        details.append("no series: " + ", ".join(missing_required))
    if invalid_required:
        details.append("non-finite values: " + ", ".join(invalid_required))
    raise SystemExit(
        f"FAIL: required Prometheus metrics are unusable ({'; '.join(details)}); "
        f"diagnostic snapshot written to {args.output}"
    )

print(f"PASS: wrote {len(REQUIRED_QUERIES)} declared metric snapshots to {args.output}; all series present and finite")
