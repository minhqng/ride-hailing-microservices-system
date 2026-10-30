"""Capture bounded W13 broker/application metric snapshots from Prometheus."""
from __future__ import annotations

import argparse
import datetime as dt
import json
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
    "payment_rate": "sum by (type,outcome) (rate({__name__=~'payment_operation(_operations)?_total'}[1m]))",
}

parser = argparse.ArgumentParser()
parser.add_argument("--prometheus", default="http://localhost:9090")
parser.add_argument("--output", required=True, type=pathlib.Path)
parser.add_argument("--run", required=True, help="Run label such as e03-replica2-run1")
args = parser.parse_args()

snapshot = {
    "run": args.run,
    "capturedAt": dt.datetime.now(dt.timezone.utc).isoformat(),
    "policy": "consumer count may vary; prefetch/DB pool/retry policy must remain fixed",
    "queries": {},
}
for name, query in QUERIES.items():
    url = args.prometheus.rstrip("/") + "/api/v1/query?" + urllib.parse.urlencode({"query": query})
    with urllib.request.urlopen(url, timeout=10) as response:
        payload = json.load(response)
    if payload.get("status") != "success":
        raise RuntimeError(f"Prometheus query failed: {name}")
    snapshot["queries"][name] = {"query": query, "result": payload["data"]["result"]}

args.output.parent.mkdir(parents=True, exist_ok=True)
args.output.write_text(json.dumps(snapshot, indent=2) + "\n", encoding="utf-8")
print(f"PASS: wrote {len(QUERIES)} metric snapshots to {args.output}")
