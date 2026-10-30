#!/usr/bin/env python3
"""B-side REL-16 adapter: observe C's topology; never manage its broker nodes."""
from __future__ import annotations

import argparse
import json
from pathlib import Path
import w13lib as lib
from w14_analyze import analyze_group, timestamp


def snapshot() -> dict:
    nodes = lib.rabbit_api("/api/nodes")
    from w13_e03 import _queue_snapshot
    q = _queue_snapshot()
    return {"capturedAtUtc": lib.iso(), "source": lib.source_binding(),
            "cluster": lib.rabbit_api("/api/overview")["cluster_name"],
            "queue": {k: q.get(k) for k in ("name", "type", "leader", "members",
                       "online", "messages_ready", "messages_unacknowledged", "consumers", "message_stats")},
            "nodes": [{"name": n["name"], "running": n["running"]} for n in nodes]}


def topology_identity(point: dict) -> str:
    return lib.sha256_text(json.dumps({"cluster": point["cluster"],
        "queue": point["queue"]["name"], "type": point["queue"]["type"],
        "members": sorted(point["queue"]["members"] or []),
        "nodes": sorted(n["name"] for n in point["nodes"])}, sort_keys=True))


def verify(before: dict, during: dict, after: dict, lost: str, runs: list[Path]) -> dict:
    errors = []
    if not (timestamp(before["capturedAtUtc"]) < timestamp(during["capturedAtUtc"])
            < timestamp(after["capturedAtUtc"])):
        errors.append("node-loss snapshots are not chronological")
    members = set(before["queue"]["members"] or [])
    if len(members) != 3 or before["queue"]["leader"] not in members or lost not in members:
        errors.append("baseline does not prove a known leader and three queue members")
    if set(before["queue"]["online"] or []) != members or len(members) != 3:
        errors.append("all three quorum members must be online at baseline")
    if {node["name"] for node in before["nodes"]} != members:
        errors.append("baseline must observe the actual three-node broker profile")
    for point in (before, during, after):
        if (point["cluster"] != before["cluster"] or point["queue"]["type"] != "quorum"
                or set(point["queue"]["members"] or []) != members):
            errors.append("cluster or quorum membership changed unexpectedly")
        running = {node["name"] for node in point["nodes"] if node["running"]}
        if set(point["queue"]["online"] or []) - running:
            errors.append("queue-online members do not match observed running RabbitMQ nodes")
    online = set(during["queue"]["online"] or [])
    if lost in online or len(online & members) < 2 or during["queue"]["leader"] not in online:
        errors.append("node-loss sample does not prove surviving majority and live leader")
    if set(after["queue"]["online"] or []) != members or not after["queue"]["consumers"]:
        errors.append("node return and consumer restoration not observed")
    # REL-16 leaves application consumers running. E04's consumer-stop proof
    # cannot establish application behaviour through quorum leader/member loss.
    analysis = analyze_group(runs, experiment="REL16")
    if analysis["status"] not in ("ACCEPTED", "SMOKE_ONLY"):
        errors.append("B application proof is invalid or unavailable")
    for run in runs:
        manifest = lib.read_json(run / "manifest.json")
        broker = manifest.get("environment", {}).get("observed", {}).get("broker", {})
        if broker.get("cluster") != before["cluster"]:
            errors.append("application run was not executed against C's observed HA cluster")
        if topology_identity(manifest["topology"]) != topology_identity(before):
            errors.append("application run queue members and node identities differ from C topology")
        window = manifest.get("interruption", {})
        if not (timestamp(window["startUtc"]) <= timestamp(during["capturedAtUtc"])
                <= timestamp(window["endUtc"])):
            errors.append("node loss did not overlap the application observation window")
        if (manifest["source"] != before["source"] or before["source"] != after["source"]
                or before["source"] != during["source"]):
            errors.append("HA and application source provenance differ")
    return {"status": "FAIL" if errors else "B_SUPPORT_PASS", "errors": errors,
            "teamAcceptance": "GATED", "application": analysis,
            "limitations": ["C owns primary REL-16 execution and topology acceptance.",
                "No PostgreSQL/Redis HA, whole-system zero downtime or multi-region claim."]}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="command", required=True)
    capture = sub.add_parser("snapshot")
    capture.add_argument("--management-port", type=int, required=True)
    capture.add_argument("--out", type=Path, required=True)
    check = sub.add_parser("verify")
    for label in ("before", "during", "after"):
        check.add_argument(f"--{label}", type=Path, required=True)
    check.add_argument("--lost-node", required=True)
    check.add_argument("--runs", type=Path, nargs="+", required=True)
    args = parser.parse_args()
    if args.command == "snapshot":
        import os
        os.environ["W13_RABBIT_MGMT_PORT"] = str(args.management_port)
        lib.write_json(args.out, snapshot())
        return 0
    result = verify(*(lib.read_json(getattr(args, key)) for key in ("before", "during", "after")),
                    args.lost_node, args.runs)
    lib.print_json(result)
    return 0 if result["status"] == "B_SUPPORT_PASS" else 2


if __name__ == "__main__":
    raise SystemExit(main())
