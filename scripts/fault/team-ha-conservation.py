"""Require confirmed, undelivered Payment work to survive a full broker restart.

Only the fixed disposable HA project is touched. Applications keep their own
business authorities; the probe uses the real confirmed AMQP publisher.
"""
import base64
import hashlib
import json
import os
from pathlib import Path
import runpy
import shutil
import subprocess
import time
import urllib.request
import uuid

HELPERS = runpy.run_path(str(Path(__file__).with_name("team-ha-runtime.py")), run_name="ha_helpers")
ROOT, ENV, BROKERS = (HELPERS[name] for name in ("ROOT", "ENV", "BROKERS"))
compose, stamp, source, wait = (HELPERS[name] for name in ("compose", "stamp", "source", "wait"))


def main():
    import argparse
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    output = args.output.resolve()
    output.parent.mkdir(parents=True, exist_ok=True)
    assert not output.exists(), "Refusing to overwrite previous evidence"
    settings = dict(line.split("=", 1) for line in (ROOT / ".env").read_text().splitlines()
                    if "=" in line and not line.startswith("#"))
    user = os.environ.get("RABBITMQ_USER", settings.get("RABBITMQ_USER", "ride_app"))
    password = os.environ.get("RABBITMQ_PASSWORD", settings.get("RABBITMQ_PASSWORD", "ride_dev_pw"))
    headers = {"Authorization": "Basic " + base64.b64encode((user + ":" + password).encode()).decode()}

    def queue():
        request = urllib.request.Request("http://127.0.0.1:15672/api/queues/%2F/payment.commands.q", headers=headers)
        with urllib.request.urlopen(request, timeout=10) as response:
            q = json.load(response)
        return {"atUtc": stamp(), **{key: q.get(key) for key in
                ("members", "online", "leader", "consumers", "messages_ready", "messages_unacknowledged", "message_stats")}}

    run = "conserve-" + uuid.uuid4().hex
    report = {"runId": run, "source": source(), "startedAtUtc": stamp(), "status": "INCOMPLETE"}
    assert not report["source"]["status"], "Clean source required"

    def state():
        query = "SELECT json_build_object(" + ",".join(
            "'" + key + "',(SELECT count(*) FROM " + table + " WHERE " + where + ")"
            for key, table, where in (
                ("operations", "payment_operations", f"operation_key LIKE '{run}:%'"),
                ("succeeded", "payment_operations", f"operation_key LIKE '{run}:%' AND state='SUCCEEDED'"),
                ("effects", "provider_effects", f"operation_key LIKE '{run}:%'"),
                ("processed", "processed_messages", f"consumer_scope='payment.commands.v1' AND message_id LIKE '{run}-%'"),
                ("outbox", "outbox_messages", f"operation_key LIKE '{run}:%'"),
                ("published", "outbox_messages", f"operation_key LIKE '{run}:%' AND status='PUBLISHED'"))) + ")"
        raw = compose("exec", "-T", "-e", "PGOPTIONS=-c default_transaction_read_only=on", "postgres", "psql",
                      "-U", "postgres", "-d", "payment_db", "-XAtc", query)
        return json.loads(raw)

    producer = None
    try:
        dotnet = shutil.which("dotnet") or str(Path.home() / ".dotnet" / ("dotnet.exe" if os.name == "nt" else "dotnet"))
        subprocess.run([dotnet, "build", "scripts/perf/recovery-probe/recovery-probe.csproj", "-c", "Release", "--nologo"],
                       cwd=ROOT, env=ENV, check=True, capture_output=True)
        report["before"] = wait(queue, lambda q: len(q["online"] or []) == 3 and q["consumers"] > 0
                                and q["messages_ready"] == q["messages_unacknowledged"] == 0)
        compose("stop", "payment")
        wait(queue, lambda q: q["consumers"] == 0)
        probe = ROOT / "scripts/perf/recovery-probe/bin/Release/net10.0/recovery-probe.dll"
        environment = dict(ENV, W14_RABBIT_PORT="5672", RABBITMQ_USER=user, RABBITMQ_PASSWORD=password)
        producer = subprocess.Popen([dotnet, str(probe), "publish", "ride.commands", "payment.authorize.v1"],
                                    cwd=ROOT, env=environment, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                    stderr=subprocess.DEVNULL, text=True, encoding="utf-8")
        receipts, envelopes = [], []
        for index in range(12):
            trip = str(uuid.uuid4())
            key = run + ":authorize:" + trip
            envelope = {"messageId": run + f"-message-{index:03}", "messageType": "AuthorizePaymentV1",
                        "schemaVersion": 1, "occurredAt": stamp(), "correlationId": run,
                        "aggregateType": "Trip", "aggregateId": trip, "sagaId": run + f"-saga-{index:03}",
                        "operationKey": key, "payload": {"tripId": trip, "operationKey": key,
                        "amountMinor": 10000 + index, "currency": "VND", "paymentType": 0}}
            envelopes.append(envelope)
            body = json.dumps(envelope)
            for copy in range(2):
                producer.stdin.write(body + "\n")
                producer.stdin.flush()
                receipt = json.loads(producer.stdout.readline())
                assert receipt["confirmed"] is True and receipt["error"] is None
                receipts.append({**receipt, "copy": copy, "bodySha256": hashlib.sha256(body.encode()).hexdigest()})
        producer.stdin.close()
        producer.wait(timeout=15)
        report.update(envelopes=envelopes, receipts=receipts)
        report["durableBeforeStop"] = wait(queue, lambda q: q["messages_ready"] == 24 and q["messages_unacknowledged"] == 0)
        report["stopRequestedAtUtc"] = stamp()
        compose("stop", *BROKERS)
        report["allStoppedAtUtc"] = stamp()
        compose("start", *BROKERS)
        report["restoredBacklog"] = wait(queue, lambda q: len(q["online"] or []) == 3 and q["consumers"] == 0
                                         and q["messages_ready"] == 24 and q["messages_unacknowledged"] == 0)
        assert state() == dict.fromkeys(("operations", "succeeded", "effects", "processed", "outbox", "published"), 0)
        report["membershipRestoredWithoutJoin"] = {}
        for node in BROKERS[1:]:
            logs = subprocess.check_output(["docker", "logs", "--since", report["stopRequestedAtUtc"],
                                            "team-w12-w14-ha-" + node + "-1"], text=True, stderr=subprocess.STDOUT)
            assert "Restoring persisted membership without join_cluster" in logs
            assert "detected node UID change" not in logs
            report["membershipRestoredWithoutJoin"][node] = True
        baseline_ack = (report["restoredBacklog"]["message_stats"] or {}).get("ack", 0)
        compose("start", "payment")
        report["final"] = wait(state, lambda s: all(value == 12 for value in s.values()))
        report["drained"] = wait(queue, lambda q: q["consumers"] > 0 and q["messages_ready"] == q["messages_unacknowledged"] == 0
                                and (q["message_stats"] or {}).get("ack", 0) >= baseline_ack + 24)
        report["ackDeltaAfterRestore"] = report["drained"]["message_stats"]["ack"] - baseline_ack
        assert source() == report["source"], "Source changed during conservation proof"
        report["status"] = "PASS"
    except Exception as error:
        report.update(status="FAIL", error=type(error).__name__ + ": " + str(error))
        raise
    finally:
        if producer is not None and producer.poll() is None:
            producer.kill()
            producer.wait()
        compose("start", *BROKERS, "payment")
        report["finishedAtUtc"] = stamp()
        output.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8", newline="\n")
    print("PASS: 24 confirmed durable deliveries conserved across full broker restart; exactly 12 Payment effects")


if __name__ == "__main__":
    main()
