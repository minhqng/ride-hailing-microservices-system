"""Run real cross-owner journeys in the disposable HA project, retaining source proof.

Start a fresh full HA stack first. This runner never resets volumes or creates
business rows; core-runtime-gate uses real authenticated APIs and production
lifecycle handlers. Node restoration runs even when an assertion fails.
"""
import argparse
import base64
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import time
import urllib.request

ROOT = Path(__file__).resolve().parents[2]
PROJECT = "team-w12-w14-ha"
BROKERS = ("rabbitmq", "rabbitmq-2", "rabbitmq-3")
APPLICATIONS = ("identity", "gateway", "driver", "trip", "matching", "payment", "notification")
ENV = dict(os.environ, COMPOSE_PROJECT_NAME=PROJECT,
           COMPOSE_FILE=os.pathsep.join(str(ROOT / p) for p in
                                      ("compose.yaml", "deploy/compose/compose.ha.yaml")),
           TRIP_MESSAGING_ENABLED="true")


def stamp():
    return datetime.now(timezone.utc).isoformat()


def command(args):
    return subprocess.check_output(args, cwd=ROOT, env=ENV, text=True, encoding="utf-8").strip()


def compose(*args):
    return command(["docker", "compose", "--profile", "ha", "--profile", "obs", *args])


def source():
    return {"commit": command(["git", "rev-parse", "HEAD"]),
            "tree": command(["git", "rev-parse", "HEAD^{tree}"]),
            "status": command(["git", "status", "--porcelain", "--", ".", ":(exclude)evidence"])}


def applications():
    result = {}
    for service in APPLICATIONS:
        identifier = compose("ps", "-q", service)
        assert identifier, "Missing application: " + service
        row = json.loads(command(["docker", "inspect", identifier]))[0]
        assert row["State"]["Running"], "Application is not running: " + service
        result[service] = {"id": row["Id"], "image": row["Image"],
                           "startedAt": row["State"]["StartedAt"], "restarts": row["RestartCount"]}
    return result


def final_state():
    result = {}
    for database, tables in {
            "driver_db": ("drivers", "outbox_messages"),
            "matching_db": ("matching_rounds", "matching_attempts", "outbox_messages"),
            "trip_db": ("trips", "ride_sagas", "outbox_messages"),
            "payment_db": ("payment_operations", "provider_effects", "processed_messages", "outbox_messages")}.items():
        result[database] = {}
        for table in tables:
            query = "SELECT coalesce(json_agg(x),'[]') FROM (SELECT * FROM " + table + ") x"
            raw = compose("exec", "-T", "-e", "PGOPTIONS=-c default_transaction_read_only=on", "postgres",
                          "psql", "-U", "postgres", "-d", database, "-v", "ON_ERROR_STOP=1", "-XAtc", query)
            result[database][table] = json.loads(raw)
    return result


def wait(action, predicate, timeout=180):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        try:
            value = action()
            if predicate(value):
                return value
        except (OSError, ValueError, AssertionError):
            pass
        time.sleep(1)
    raise AssertionError("HA observation deadline exceeded")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--scenario", required=True, choices=("broker", "quorum"))
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    output = args.output.resolve()
    output.parent.mkdir(parents=True, exist_ok=True)
    assert not output.exists(), "Refusing to overwrite earlier recovery evidence"
    settings = dict(line.split("=", 1) for line in (ROOT / ".env").read_text().splitlines()
                    if "=" in line and not line.startswith("#"))
    user = os.environ.get("RABBITMQ_USER", settings.get("RABBITMQ_USER", "ride_app"))
    password = os.environ.get("RABBITMQ_PASSWORD", settings.get("RABBITMQ_PASSWORD", "ride_dev_pw"))
    headers = {"Authorization": "Basic " + base64.b64encode((user + ":" + password).encode()).decode()}

    def topology(port):
        with urllib.request.urlopen(urllib.request.Request(
                f"http://127.0.0.1:{port}/api/queues/%2F", headers=headers), timeout=10) as response:
            queues = json.load(response)
        return {"atUtc": stamp(), "queues": [{key: queue.get(key) for key in
                ("name", "type", "leader", "members", "online", "consumers",
                 "messages_ready", "messages_unacknowledged", "message_stats")}
                for queue in queues if queue["type"] == "quorum"]}

    def online(snapshot, count):
        queues = snapshot["queues"]
        return (len(queues) >= 19 and all(len(q["members"] or []) == 3
                and len(q["online"] or []) == count and q["leader"] in q["online"] for q in queues))

    report = {"scenario": args.scenario, "project": PROJECT, "startedAtUtc": stamp(),
              "source": source(), "status": "INCOMPLETE"}
    assert not report["source"]["status"], "A clean source tree is required"
    lost = None
    try:
        report["before"] = wait(lambda: topology(15672), lambda point: online(point, 3))
        report["applicationsBefore"] = applications()
        if args.scenario == "quorum":
            queue = next(q for q in report["before"]["queues"] if q["name"] == "matching.ride-requested.q")
            lost = queue["leader"].split("@", 1)[1]
            assert lost in BROKERS
            report["lostNode"] = lost
            report["faultRequestedAtUtc"] = stamp()
            compose("kill", "-s", "SIGKILL", lost)
            port = 25672 if lost == "rabbitmq" else 15672
            report["duringBeforeWork"] = wait(lambda: topology(port), lambda point: online(point, 2))
            assert all("rabbit@" + lost not in q["online"] for q in report["duringBeforeWork"]["queues"])
        else:
            port = 15672
        core_environment = dict(ENV, W14_BROKER_RECOVERY="1" if args.scenario == "broker" else "0")
        report["workStartedAtUtc"] = stamp()
        with output.with_suffix(".log").open("w", encoding="utf-8") as log:
            result = subprocess.run([sys.executable, "scripts/test/core-runtime-gate.py"],
                                    cwd=ROOT, env=core_environment, stdout=log, stderr=subprocess.STDOUT)
        assert result.returncode == 0, "Core runtime gate failed; inspect retained log"
        report["workFinishedAtUtc"] = stamp()
        core_path = ROOT / "plans/reports/core-runtime-gate.json"
        core = json.loads(core_path.read_text())
        assert core["source"]["commit"] == report["source"]["commit"]
        assert len(core["checks"]) == 8 and all(c["status"] == "PASS" for c in core["checks"])
        if args.scenario == "broker":
            assert len(core["brokerRecovery"]) == 2
        destination = output.with_name(output.stem + "-core.json")
        shutil.copyfile(core_path, destination)
        report["core"] = {"path": destination.name,
                          "sha256": hashlib.sha256(destination.read_bytes()).hexdigest()}
        report["finalState"] = final_state()
        report["duringAfterWork"] = wait(lambda: topology(port),
                                        lambda point: online(point, 2 if lost else 3))
        report["applicationsAfter"] = applications()
        for service in APPLICATIONS:
            # The normal G4 cancellation actor deliberately restarts Payment.
            if not (args.scenario == "quorum" and service == "payment"):
                assert report["applicationsAfter"][service] == report["applicationsBefore"][service], service + " restarted"
        assert source() == report["source"], "Source changed during the rehearsal"
        report["status"] = "PASS"
    except Exception as error:
        report["status"] = "FAIL"
        report["error"] = type(error).__name__ + ": " + str(error)
        raise
    finally:
        try:
            compose("start", *(BROKERS if not lost else (lost,)))
            report["afterRestore"] = wait(lambda: topology(15672), lambda point: online(point, 3))
        except Exception as error:
            report["status"] = "FAIL"
            report["restorationError"] = type(error).__name__
            raise
        finally:
            report["finishedAtUtc"] = stamp()
            output.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8", newline="\n")
    print("PASS: source-bound full runtime and all-owner audit through " + args.scenario + " recovery")


if __name__ == "__main__":
    main()
