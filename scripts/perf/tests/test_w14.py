from __future__ import annotations

import copy
import sys
import tempfile
import unittest
from datetime import datetime, timedelta, timezone
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import w13lib as lib
import w14_analyze as analyze
import w14_ha as ha


def fixture(root: Path) -> dict:
    """Deliberately synthetic evidence: never eligible as an experiment result."""
    source = lib.source_binding()
    source.update(dirty=False, statusDigest=lib.sha256_text(""))
    t0 = datetime(2026, 10, 30, tzinfo=timezone.utc)
    stamp = lambda n: (t0 + timedelta(seconds=n)).isoformat()
    receipts, rounds, events = [], [], []
    for index, sent in enumerate((0.5, 2.1, 3.1, 5.1)):
        msg = lib.seeded_uuid(14, f"message-{index}")
        trip = lib.seeded_uuid(14, f"trip-{index}")
        event = lib.seeded_uuid(14, f"result-{index}")
        receipts.append({"messageId": msg, "tripId": trip, "confirmed": True,
            "error": None, "latencyMs": 10, "elapsed": sent + .01,
            "sentElapsed": sent, "sentAtUtc": stamp(sent), "confirmedAtUtc": stamp(sent + .01)})
        rounds.append({"trip_id": trip, "matching_round": 1, "input_message_id": msg,
            "status": "NO_DRIVER", "auto_execute": True, "selected_driver_id": None,
            "selected_reservation_id": None, "result_message_id": event})
        events.append({"message_id": event, "message_type": "MatchingFailedV1",
            "status": "PUBLISHED", "published_at": stamp(7), "payload_json": {"tripId": trip}})
    samples = []
    for elapsed, phase, ready, terminal in ((1, "baseline", 0, 1), (2.5, "outage", 1, 1),
            (3.5, "outage", 2, 1), (4.5, "recovery", 1, 2),
            (5.5, "recovery", 0, 3), (6.5, "recovery", 0, 4), (7.5, "quiescence", 0, 4)):
        samples.append({"elapsed": elapsed, "atUtc": stamp(elapsed), "phase": phase,
            "ready": ready, "unacked": 0, "consumers": 0 if phase == "outage" else 1,
            "redeliver": 0, "ack": terminal, "intake": terminal, "terminal": terminal,
            "pending": 0, "type": "quorum"})
    manifest = {"schemaVersion": 1, "experiment": "E04", "runId": "W14-E04-20261030T000000Z-aabbccdd",
        "group": "explicitly-synthetic", "repeat": 1, "plannedRepeats": 1, "mode": "smoke",
        "synthetic": True, "source": source, "sourceAfter": source, "status": "PASS",
        "configuration": {"rate": 1, "outageSeconds": 2, "baselineSeconds": 1, "recoveryTimeout": 10},
        "environment": {"configured": {}}, "g4": {"green": False},
        "fingerprint": lib.environment_fingerprint({"configured": {}}),
        "interruption": {"start": 2, "end": 4, "startUtc": stamp(2), "endUtc": stamp(4)},
        "steadyElapsed": 6.5, "driverBefore": [], "duplicateReceipt": receipts[0],
        "auditor": {"sourceCommit": source["commit"], "sourceTree": source["tree"],
            "checks": [{"checkId": key, "status": "PASS"} for key in
                       ("AUD-01", "AUD-02", "AUD-06", "AUD-08")]}, "artifacts": {}}
    final = {"rounds": rounds, "attempts": [], "matchingOutbox": events, "drivers": [], "driverOutbox": []}
    for name, data in (("samples", samples), ("receipts", receipts), ("final-state", final)):
        artifact_path = root / f"{name}.json"
        lib.write_json(artifact_path, data)
        manifest["artifacts"][name] = {"path": artifact_path.name, "sha256": lib.sha256_file(artifact_path)}
    lib.write_json(root / "manifest.json", manifest)
    return manifest


class RecoveryAnalysisTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.root = Path(self.temporary.name)
        self.manifest = fixture(self.root)

    def tearDown(self):
        self.temporary.cleanup()

    def result(self):
        lib.write_json(self.root / "manifest.json", self.manifest)
        return analyze.analyze_group([self.root])

    def test_synthetic_never_accepted_and_rates_use_observations(self):
        result = self.result()
        self.assertEqual("SYNTHETIC_ONLY", result["status"])
        run = result["runs"][0]
        self.assertEqual(1, run["lambdaOutage"])
        self.assertEqual(.5, run["lambdaRecovery"])
        self.assertEqual(1, run["mu"])
        self.assertEqual(2, run["predictedBacklog"])
        self.assertEqual(2.5, run["observedSteadySeconds"])

    def test_missing_manifest_and_empty_group_rejected(self):
        self.assertEqual("INVALID", analyze.analyze_group([])["status"])
        (self.root / "manifest.json").unlink()
        self.assertEqual("INVALID", analyze.analyze_group([self.root])["status"])

    def test_metadata_negative_controls(self):
        controls = (("dirty", lambda m: m["source"].update(dirty=True)),
            ("provenance", lambda m: m["source"].update(tree="0" * 40)),
            ("repetition", lambda m: m.update(plannedRepeats=3)),
            ("timestamp", lambda m: m["interruption"].update(endUtc="not-a-date")),
            ("negative rate", lambda m: m["configuration"].update(rate=-1)),
            ("nonfinite rate", lambda m: m["configuration"].update(rate=float("nan"))),
            ("audit", lambda m: m["auditor"]["checks"][0].update(status="NOT_RUN")),
            ("missing finals", lambda m: m["artifacts"].pop("final-state")),
            ("artifact escape", lambda m: m["artifacts"]["samples"].update(path="../outside.json")),
            ("source changed", lambda m: m.update(sourceAfter={"dirty": True})))
        for label, mutate in controls:
            with self.subTest(label=label):
                self.manifest = fixture(self.root)
                mutate(self.manifest)
                self.assertEqual("INVALID", self.result()["status"])

    def test_raw_negative_controls_survive_updated_digests(self):
        controls = (("receipts", lambda data: data[0].update(confirmed=False)),
            ("samples", lambda data: data[2].update(elapsed=-1)),
            ("samples", lambda data: data.clear()),
            ("final-state", lambda data: data["rounds"].pop()),
            ("final-state", lambda data: data["rounds"][0].update(status="PROCESSING")),
            ("final-state", lambda data: data["matchingOutbox"][0].update(status="PENDING")),
            ("final-state", lambda data: data["attempts"].append({"state": "UNKNOWN"})))
        for key, mutate in controls:
            with self.subTest(artifact=key, mutate=mutate):
                self.manifest = fixture(self.root)
                path = self.root / f"{key}.json"
                data = lib.read_json(path)
                mutate(data)
                lib.write_json(path, data)
                self.manifest["artifacts"][key]["sha256"] = lib.sha256_file(path)
                self.assertEqual("INVALID", self.result()["status"])

    def test_tampering_and_real_label_without_provider_proof_rejected(self):
        (self.root / "samples.json").write_text("[]")
        self.assertEqual("INVALID", self.result()["status"])
        self.manifest = fixture(self.root)
        self.manifest["synthetic"] = False
        self.assertEqual("INVALID", self.result()["status"])

    def test_report_gate_and_repetitions_cannot_be_lowered(self):
        self.manifest["mode"] = "report"
        result = self.result()
        self.assertEqual("INVALID", result["status"])
        self.assertTrue(any("three" in error for error in result["errors"]))
        self.assertTrue(any("G4" in error for error in result["errors"]))

    def test_non_drainable_workload_has_no_finite_prediction(self):
        samples_path = self.root / "samples.json"
        samples = lib.read_json(samples_path)
        # Same observed completions compressed into a high-arrival, low-service window.
        # Lower terminal throughput via the interval length, keeping all final proofs intact.
        for row in samples:
            if row["phase"] == "recovery":
                row["terminal"] = 4
                row["intake"] = 4
        lib.write_json(samples_path, samples)
        self.manifest["artifacts"]["samples"]["sha256"] = lib.sha256_file(samples_path)
        result = self.result()
        self.assertEqual("SYNTHETIC_ONLY", result["status"])
        self.assertFalse(result["runs"][0]["drainableUnderMeasuredWorkload"])
        self.assertIsNone(result["runs"][0]["predictedDrainSeconds"])

    def test_ha_rejects_single_node_even_if_queue_type_is_quorum(self):
        point = {"capturedAtUtc": "2026-10-30T00:00:00Z", "source": self.manifest["source"],
            "cluster": "test", "queue": {"type": "quorum", "members": ["rabbit@one"],
            "online": ["rabbit@one"], "leader": "rabbit@one", "consumers": 1}}
        during, after = copy.deepcopy(point), copy.deepcopy(point)
        during["capturedAtUtc"] = "2026-10-30T00:00:03Z"
        after["capturedAtUtc"] = "2026-10-30T00:00:08Z"
        result = ha.verify(point, during, after, "rabbit@one", [])
        self.assertEqual("FAIL", result["status"])
        self.assertTrue(any("three" in error for error in result["errors"]))


if __name__ == "__main__":
    unittest.main()
