"""Real-provider Driver/Matching demonstrations in one disposable project.

Uses existing authenticated contention, PostgreSQL auditors and bounded HTTP
faultpoint tests. It never writes business database rows or invents outcomes.
"""
from __future__ import annotations

import argparse
from datetime import datetime, timezone
import json
import os
from pathlib import Path
import subprocess
import sys
import time
import uuid
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "scripts/perf"))
import w13lib as lib
import w13_auth as auth
import w13_e01 as contention

PROJECT = "team-w12-b-release"
SDK = "mcr.microsoft.com/dotnet/sdk:10.0@sha256:de4fe90a98845d75910b9769662465c84de1a934665c431279d1382b2bddc02b"
TEST_RUNNER = "rh-b-release-tests"
RUNNER_OWNER_LABEL = "ride-hailing.demo-owner"
MATCHING_TESTS = "tests/Integration/RideHailing.MatchingService.IntegrationTests/RideHailing.MatchingService.IntegrationTests.csproj"
NS = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}


def write(path, data):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes((json.dumps(data, indent=2) + "\n").encode())


def run(command, output, *, timeout=900):
    started = time.monotonic()
    result = subprocess.run(command, cwd=ROOT, capture_output=True, text=True,
                            encoding="utf-8", errors="replace", timeout=timeout)
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(result.stdout + result.stderr, encoding="utf-8")
    if result.returncode:
        raise RuntimeError(f"{command[0]} failed ({result.returncode}); inspect {output}")
    return result.stdout, time.monotonic() - started


def compose(*args):
    return ["docker", "compose", "-f", "compose.yaml", "--project-name", PROJECT, "--profile", "obs", *args]


def wait(label, action, predicate, seconds=60):
    end = time.monotonic() + seconds
    while time.monotonic() < end:
        value = action()
        if predicate(value):
            return value
        time.sleep(.25)
    raise AssertionError("Timed out: " + label)


def sdk(command, output, cache):
    return run(["docker", "run", "--rm", "--name", TEST_RUNNER,
                "--label", f"{RUNNER_OWNER_LABEL}={cache}",
                "-v", str(ROOT).replace("\\", "/") + ":/work",
                "-v", cache + ":/root/.nuget/packages",
                "-v", "/var/run/docker.sock:/var/run/docker.sock",
                "-e", "TESTCONTAINERS_HOST_OVERRIDE=host.docker.internal",
                "-e", "B12_AUDIT_OUTPUT_DIR=/work/TestResults/b12-audit",
                "-w", "/work", SDK, "bash", "-lc", command], output)


def cleanup_test_runner(cache):
    # A timed-out Docker client can leave its container running. The unique
    # cache name doubles as this invocation's ownership token.
    owned = subprocess.run(
        ["docker", "ps", "-aq", "--filter", f"name=^/{TEST_RUNNER}$",
         "--filter", f"label={RUNNER_OWNER_LABEL}={cache}"],
        capture_output=True, text=True)
    if owned.returncode:
        return owned.returncode
    ids = owned.stdout.split()
    if not ids:
        return 0
    stopped = subprocess.run(["docker", "stop", "--time", "10", *ids], capture_output=True)
    remaining = subprocess.run(
        ["docker", "ps", "-aq", "--filter", f"name=^/{TEST_RUNNER}$",
         "--filter", f"label={RUNNER_OWNER_LABEL}={cache}"],
        capture_output=True, text=True)
    if remaining.returncode or not remaining.stdout.strip():
        return remaining.returncode
    remove = ["docker", "rm", *(["-f"] if stopped.returncode else []), *remaining.stdout.split()]
    return subprocess.run(remove, capture_output=True).returncode


def test_results(directory, minimum, expected=None):
    results = []
    for path in directory.glob("*.trx"):
        xml = ET.parse(path)
        counter = xml.find(".//t:Counters", NS).attrib
        method = xml.find(".//t:TestMethod", NS)
        project = Path(method.attrib["codeBase"]).stem if method is not None else "empty"
        results.append({"project": project, "passed": int(counter["passed"]),
                        "failed": int(counter["failed"]), "total": int(counter["total"])})
    assert sum(r["passed"] for r in results) >= minimum and not any(r["failed"] for r in results)
    if expected:
        assert {r["project"]: r["passed"] for r in results} == expected
    return results


def provider_regression(output, cache):
    projects = {
        "RideHailing.DriverService.UnitTests": ("Unit", 131),
        "RideHailing.DriverService.IntegrationTests": ("Integration", 118),
        "RideHailing.MatchingService.UnitTests": ("Unit", 115),
        "RideHailing.MatchingService.IntegrationTests": ("Integration", 200),
    }
    relative = output.relative_to(ROOT).as_posix() + "/regression"
    commands = ["dotnet restore RideHailing.Distributed.slnx",
                "dotnet build RideHailing.Distributed.slnx -c Release --no-restore",
                "dotnet build scripts/test/w10-g3-harness/RideHailing.W10Harness.csproj -c Release"]
    commands += [f"dotnet test tests/{kind}/{name}/{name}.csproj -c Release --no-build --logger trx --results-directory /work/{relative}"
                 for name, (kind, _) in projects.items()]
    _, elapsed = sdk(" && ".join(commands), output / "provider-regression.log", cache)
    summary = {"elapsedSeconds": elapsed, "projects": test_results(output / "regression", 564,
               {name: count for name, (_, count) in projects.items()})}
    write(output / "provider-regression.json", summary)
    return summary


def redis(*args):
    result = subprocess.run(compose("exec", "-T", "redis", "redis-cli", "--json", *map(str, args)),
                            cwd=ROOT, capture_output=True, text=True, check=True)
    return json.loads(result.stdout)


def live_contention(output, repeat):
    started = time.monotonic()
    driver_id = str(uuid.uuid4())
    base = "http://127.0.0.1:8102"
    driver = contention.create_and_online(base, driver_id, timeout=20)
    version = int(driver["version"])
    session = uuid.uuid4().hex
    captured = datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%S.0000000Z")
    old_location = {"deviceSessionId": session, "sequenceNo": 20, "capturedAt": captured,
                    "latitude": 10.7769, "longitude": 106.7009}
    for sequence in (20, 21):
        status, _, _ = contention.http_json(base, "POST", f"/drivers/{driver_id}/location",
                                          body={**old_location, "sequenceNo": sequence}, timeout=20)
        assert status == 202
    member = driver_id.replace("-", "")
    key = "driver:projection:" + member
    wait("accepted location projection", lambda: redis("HGET", key, "sequenceNo"), lambda v: v == "21")
    samples = contention.contend(base, driver_id, version, 10, int(uuid.uuid4().int % 1000000), repeat, timeout=20)
    winners = [s for s in samples if s["outcome"] == "success"]
    assert len(winners) == 1 and sum(s["outcome"] == "domainConflict" for s in samples) == 9
    assert all(s["outcome"] in ("success", "domainConflict") for s in samples)
    winner = winners[0]
    final = contention.final_state(driver_id, winner)
    audit = contention.aud01(final, winner, version)
    assert audit["status"] == "PASS"
    reserved_version = final["driver"]["version"]
    wait("reserved projection", lambda: redis("HGET", key, "driverAggregateVersion"),
         lambda v: v == str(reserved_version))
    before = contention.final_state(driver_id, winner)["driver"]
    stale_state = redis("FCALL", "driver_projection_update", 3, key, "drivers:geo",
                        "driver:projection:receipt:unused", member, "AVAILABLE", version,
                        "", "", "", "", "", "")
    stale_location = redis("FCALL", "driver_projection_update", 3, key, "drivers:geo",
                           "driver:projection:receipt:unused", member, "", "", 106.7009, 10.7769,
                           session, 20, captured, "")
    assert stale_state[0] == "stale" and stale_location[1] == "stale"
    assert redis("HGET", key, "derivedStatus") == "RESERVED"
    assert redis("HGET", key, "driverAggregateVersion") == str(reserved_version)
    assert redis("HGET", key, "sequenceNo") == "21"
    after = contention.final_state(driver_id, winner)["driver"]
    assert before == after
    status, released, _ = contention.http_json(base, "POST", f"/drivers/{driver_id}/release",
        body={"tripId": winner["tripId"], "reservationId": winner["reservationId"]}, timeout=20)
    assert status == 200
    status, offline, _ = contention.http_json(base, "POST", f"/drivers/{driver_id}/offline",
                                             body={"expectedVersion": released["version"]}, timeout=20)
    assert status == 200 and offline["status"] == "OFFLINE"
    cleaned = contention.final_state(driver_id, winner)
    assert not cleaned["activeReservationRows"]
    result = {"scenario": "CON-01/REL-12/REL-13", "elapsedSeconds": time.monotonic() - started,
              "n": 10, "winners": 1, "expectedConflicts": 9, "unexpectedErrors": 0,
              "samples": samples, "finalAuthority": final, "aud01": audit,
              "redis": {"staleStateResult": stale_state, "staleLocationResult": stale_location,
                        "driverBefore": before, "driverAfter": after,
                        "proofBoundary": "Real accepted state/location replay into production Redis Function; provider handler/receipt path separately regression-tested"},
              "afterExactReleaseAndOffline": cleaned}
    write(output / "contention-redis.json", result)
    return result


def response_loss(output, cache):
    relative = output.relative_to(ROOT).as_posix() + "/response-loss"
    command = f"dotnet test {MATCHING_TESTS} -c Release --no-build --filter FullyQualifiedName~LostReserveResponseHttpTests --logger trx --results-directory /work/{relative}"
    _, elapsed = sdk(command, output / "response-loss.log", cache)
    cases = test_results(output / "response-loss", 1)
    assert sum(c["passed"] for c in cases) == 1
    records = []
    for path in (output / "response-loss").glob("*.trx"):
        for stdout in ET.parse(path).findall(".//t:StdOut", NS):
            for line in (stdout.text or "").splitlines():
                if line.startswith("AUTHORITY_PROOF "):
                    records.append(json.loads(line.removeprefix("AUTHORITY_PROOF ")))
    assert len(records) == 1
    proof = records[0]
    assert proof["uncertainState"] == "UNKNOWN" and proof["finalRoundStatus"] == "RESERVED"
    assert len({proof[k] for k in ("reservationId", "postCommitReservationId", "uncertainReservationId",
                                  "finalRoundReservationId", "finalDriverReservationId")}) == 1
    assert proof["otherDriverStatus"] == "Available" and proof["reserveOutboxCount"] == 1
    assert proof["attemptCount"] == proof["authoritativeReservationCount"] == 1
    assert proof["finalAttemptState"] == "RESERVED" and proof["finalDriverStatus"] == "Reserved"
    result = {"elapsedSeconds": elapsed, "cases": cases, "authorityProof": proof,
              "proofBoundary": "Production Driver Kestrel endpoint/repository/faultpoint and Matching typed HTTP client/orchestrator; full production worker/process recovery in regression"}
    write(output / "response-loss.json", result)
    return result


def rehearse(output, repeat, cache, source):
    trial = output / f"rehearsal-{repeat}"
    run(compose("up", "-d", "--wait", "postgres", "redis", "rabbitmq"), trial / "infra.log")
    wait("PostgreSQL final TCP startup", lambda: subprocess.run(compose("exec", "-T", "postgres", "pg_isready", "-h", "127.0.0.1"), capture_output=True).returncode, lambda c: c == 0)
    run(compose("up", "-d", "--no-build", "--wait"), trial / "startup.log")
    run([sys.executable, "scripts/test/platform-smoke.py"], trial / "platform.log")
    auth.reset()
    started = time.monotonic()
    contention_result = live_contention(trial, repeat)
    fault_result = response_loss(trial, cache)
    _, shared_seconds = run([sys.executable, "scripts/test/core-runtime-gate.py"], trial / "core.log")
    core = json.loads((ROOT / "plans/reports/core-runtime-gate.json").read_text())
    assert core["source"]["commit"] == source["commit"] and len(core["scenarios"]) == 3
    assert all(check["status"] == "PASS" for check in core["checks"])
    write(trial / "core-authorities.json", core)
    elapsed = time.monotonic() - started
    summary = {"repeat": repeat, "demoSeconds": elapsed,
        "bSegmentSeconds": contention_result["elapsedSeconds"] + fault_result["elapsedSeconds"],
        "sharedJourneySeconds": shared_seconds, "status": "PASS", "auditor": core["checks"],
        "humanRoleHandoffs": "NOT PERFORMED", "artifacts": [f"rehearsal-{repeat}/contention-redis.json", f"rehearsal-{repeat}/response-loss.json", f"rehearsal-{repeat}/core-authorities.json"]}
    run(compose("down", "-v"), trial / "teardown.log")
    print(f"PASS: technical rehearsal {repeat}, demo {elapsed:.2f}s", flush=True)
    return summary


def main():
    if not __debug__:
        raise SystemExit("Run without Python optimization: assertions are authority checks")
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repeats", type=int, choices=(1, 3), default=3)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    assert not subprocess.check_output(["git", "status", "--porcelain", "--", ".", ":(exclude)evidence"], cwd=ROOT, text=True).strip(), "Commit source changes before rehearsal"
    source = {key: subprocess.check_output(["git", "rev-parse", value], cwd=ROOT, text=True).strip()
              for key, value in (("commit", "HEAD"), ("tree", "HEAD^{tree}"))}
    output = (args.output or ROOT / "evidence/member-b/final/release/outputs" /
              (datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ") + "-" + uuid.uuid4().hex[:8])).resolve()
    allowed_outputs = (ROOT / "evidence/member-b/final/release/outputs", ROOT / "plans/reports")
    assert any(output.is_relative_to(path) for path in allowed_outputs) and not output.exists(), "Use a new ignored release output or plans/reports directory"
    existing = subprocess.check_output(["docker", "ps", "-aq", "--filter", "label=com.docker.compose.project=" + PROJECT], text=True).strip()
    assert not existing, "Inspect existing project owner before starting"
    assert not subprocess.check_output(["docker", "ps", "-aq", "--filter", f"name=^/{TEST_RUNNER}$"], text=True).strip(), "Test runner already exists"
    os.environ.update(COMPOSE_PROJECT_NAME=PROJECT, COMPOSE_FILE="compose.yaml",
                      TRIP_MESSAGING_ENABLED="true", W13_IDENTITY_PORT="8101")
    lib.PROJECT, lib.COMPOSE_FILES = PROJECT, ["compose.yaml"]
    cache = "rh-b-release-nuget-" + uuid.uuid4().hex[:8]
    result = {"source": source, "project": PROJECT, "humanParticipation": "NOT PERFORMED",
              "status": "FAIL", "rehearsals": []}
    try:
        run(["docker", "volume", "create", cache], output / "cache.log")
        result["providerRegression"] = provider_regression(output, cache)
        run([sys.executable, "scripts/security/init-local-auth.py"], output / "auth.log")
        run(compose("config", "--quiet"), output / "compose.log")
        run(compose("build"), output / "build.log")
        for repeat in range(1, args.repeats + 1):
            result["rehearsals"].append(rehearse(output, repeat, cache, source))
        run([sys.executable, "scripts/evidence/validate-driver-matching-claims.py", "--output", str(output / "claims.json")], output / "claims.log")
        result["status"] = "PASS"
    finally:
        result["testRunnerTeardownExit"] = cleanup_test_runner(cache)
        cleanup = subprocess.run(compose("down", "-v"), cwd=ROOT, capture_output=True, text=True)
        result["teardownExit"] = cleanup.returncode
        result["cacheTeardownExit"] = subprocess.run(["docker", "volume", "rm", cache], capture_output=True).returncode
        write(output / "summary.json", result)
    assert (result["status"] == "PASS" and result["teardownExit"] == 0
            and result["testRunnerTeardownExit"] == 0 and result["cacheTeardownExit"] == 0)
    print("PASS: real technical rehearsals; human team/oral rehearsals remain PENDING", flush=True)


if __name__ == "__main__":
    main()
