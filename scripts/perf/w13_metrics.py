"""Capture actual Prometheus samples for the explicitly declared experiment scope."""
import os
from pathlib import Path
import sys
import time

import w13lib as lib


def capture(run_dir: Path, experiment: str, *, start: str | None = None) -> tuple[bool, Path]:
    queries = ["outbox_pending", "outbox_oldest_pending_age"]
    if experiment != "E01":
        queries += ["queue_ready", "queue_unacked", "redelivery_rate", "consumer_delivery_rate",
                    "consumer_redelivery_rate", "consumer_processing_p95_ms", "dedup_rate"]
    if experiment == "E02":
        queries += ["saga_active", "trip_transition_rejected_rate"]
    path = run_dir / "raw" / "prometheus.json"
    command = [sys.executable, str(lib.ROOT / "scripts/evidence/performance-snapshot.py"),
               "--prometheus", f"http://127.0.0.1:{os.environ.get('W13_PROMETHEUS_PORT', '19090')}",
               "--output", str(path), "--run", run_dir.name, "--queries", *queries]
    if start:
        command += ["--start", start, "--end", lib.iso()]
    deadline = time.monotonic() + 75
    while True:
        result = lib.run(command, timeout=45)
        if result.returncode == 0:
            return True, path
        if time.monotonic() >= deadline:
            if not path.exists():
                lib.write_json(path, {"verdict": "FAIL", "reason": "Prometheus capture unavailable",
                                      "diagnostic": result.stderr[-2000:]})
            return False, path
        time.sleep(5)
