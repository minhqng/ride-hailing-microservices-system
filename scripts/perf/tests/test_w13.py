from __future__ import annotations

import json
import shutil
import sys
import tempfile
import unittest
from unittest.mock import patch
from pathlib import Path

PERF_DIR = Path(__file__).resolve().parents[1]
if str(PERF_DIR) not in sys.path:
    sys.path.insert(0, str(PERF_DIR))

import w13_analyze as analyze  # noqa: E402
import w13lib as lib  # noqa: E402

FIXTURES = PERF_DIR / "fixtures" / "synthetic"


def materialize(name: str, target: Path, **manifest_overrides) -> tuple[dict, Path]:
    source = FIXTURES / name
    manifest = json.loads((source / "manifest.json").read_text(encoding="utf-8"))
    run_dir = target / manifest["runId"]
    shutil.copytree(source, run_dir)
    manifest_path = run_dir / "manifest.json"
    manifest["artifacts"]["runDirectory"] = str(run_dir.resolve())
    manifest.update(manifest_overrides)
    manifest_path.write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    return manifest, run_dir


class PercentileTests(unittest.TestCase):
    def test_uri_userinfo_is_redacted_before_source_evidence(self) -> None:
        self.assertEqual("https://<redacted>@github.com/team/repo.git",
            lib._redact_string("https://account:private-token@github.com/team/repo.git"))
        self.assertEqual("amqp://<redacted>@localhost/vhost",
            lib._redact_string("amqp://username:password@localhost/vhost"))

    def test_g4_green_requires_every_check_and_verified_source(self) -> None:
        # A valid certificate must be able to turn green; any failed check still blocks.
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            path = root / "g4.json"
            evidence = {"schemaVersion": 1, "sourceCommit": "a" * 40,
                        "verdict": "GREEN", "blockers": [], "checks": {
                key: "PASS" for key in ("bOwnedCorrectness", "securityBaseline",
                    "aCorrectnessAcceptance", "cCorrectnessAcceptance", "authoritativeAuditor",
                    "productionApiSurface", "brokerSurface", "stableEnvironment")}}
            lib.write_json(path, evidence)
            import subprocess
            with patch.object(lib, "EVIDENCE_ROOT", root), patch.object(lib, "run",
                    return_value=subprocess.CompletedProcess([], 0, "a" * 40, "")):
                self.assertTrue(lib.g4_readiness(path)["green"])
                evidence["checks"]["securityBaseline"] = "FAIL"
                lib.write_json(path, evidence)
                self.assertFalse(lib.g4_readiness(path)["green"])

    def test_nearest_rank(self) -> None:
        values = [float(value) for value in range(1, 11)]
        self.assertEqual(lib.percentile_nearest_rank(values, 50), 5.0)
        self.assertEqual(lib.percentile_nearest_rank(values, 95), 10.0)
        self.assertEqual(lib.percentile_nearest_rank(values, 100), 10.0)
        self.assertIsNone(lib.percentile_nearest_rank([], 95))

    def test_single_sample(self) -> None:
        self.assertEqual(lib.percentile_nearest_rank([3.5], 50), 3.5)
        self.assertEqual(lib.percentile_nearest_rank([3.5], 95), 3.5)


class ManifestValidationTests(unittest.TestCase):
    def test_valid_synthetic_fixture(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            _, run_dir = materialize("e01-smoke", Path(tmp))
            manifest, errors = lib.load_run(run_dir)
            self.assertEqual(errors, [])
            self.assertIsNotNone(manifest)
            self.assertTrue(manifest["synthetic"])

    def test_invalid_fixture_reports_missing_source_and_bad_outcome_sum(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            _, run_dir = materialize("invalid-shape", Path(tmp))
            manifest, errors = lib.load_run(run_dir)
            self.assertIsNotNone(manifest)
            joined = "\n".join(errors)
            self.assertIn("source.tree", joined)
            self.assertIn("outcome counts do not sum", joined)

    def test_report_grade_requires_green_g4(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            _, run_dir = materialize("report-gated", Path(tmp))
            manifest, errors = lib.load_run(run_dir)
            self.assertIsNotNone(manifest)
            self.assertIn("reportGrade=true requires a green G4 gate", "\n".join(errors))

    def test_pass_requires_samples(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            _, run_dir = materialize("e01-smoke", Path(tmp))
            manifest = json.loads((run_dir / "manifest.json").read_text(encoding="utf-8"))
            manifest["result"]["sampleCount"] = 0
            manifest["result"]["outcomes"] = {
                "success": 0, "domainConflict": 0, "systemError": 0,
                "timeout": 0, "unexpected": 0,
            }
            errors = lib.validate_manifest(manifest, run_dir)
            self.assertIn("status=PASS requires at least one sample", "\n".join(errors))

    def test_run_directory_mismatch_is_rejected(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            _, run_dir = materialize("e01-smoke", Path(tmp))
            manifest = json.loads((run_dir / "manifest.json").read_text(encoding="utf-8"))
            manifest["artifacts"]["runDirectory"] = str(run_dir / "other")
            errors = lib.validate_manifest(manifest, run_dir)
            self.assertIn("artifacts.runDirectory does not match", "\n".join(errors))


class RedactionAndGateTests(unittest.TestCase):
    def test_secrets_are_redacted(self) -> None:
        value = {
            "connection": "Host=postgres;Username=driver_app;Password=driver_dev_pw;",
            "RABBITMQ_DEFAULT_PASS": "ride_dev_pw",
            "normal": "visible",
        }
        redacted = lib._redact(value)
        self.assertNotIn("driver_dev_pw", json.dumps(redacted))
        self.assertEqual(redacted["RABBITMQ_DEFAULT_PASS"], "<redacted>")
        self.assertEqual(redacted["normal"], "visible")

    def test_report_grade_rejects_dirty_source(self) -> None:
        g4 = {"green": True, "blockers": [], "verdict": "GREEN"}
        with self.assertRaises(lib.GateError):
            lib.ensure_report_grade(g4, {"dirty": True, "statusDigest": "x"})


class AnalyzerTests(unittest.TestCase):
    def test_smoke_analysis_can_never_accept(self) -> None:
        for fixture, analyzer, expected_experiment in (
            ("e01-smoke", analyze.analyze_e01, "E01"),
            ("e02-smoke", analyze.analyze_e02, "E02"),
            ("e03-smoke", analyze.analyze_e03, "E03"),
        ):
            with tempfile.TemporaryDirectory() as tmp:
                runs_root = Path(tmp) / "runs" / expected_experiment
                runs_root.mkdir(parents=True)
                _, run_dir = materialize(fixture, runs_root)
                manifests, errors = analyze._load(expected_experiment, Path(tmp) / "runs")
                self.assertEqual(errors, [])
                if analyzer is analyze.analyze_e01:
                    result = analyzer(manifests, errors, 3)
                elif analyzer is analyze.analyze_e02:
                    result = analyzer(manifests, errors, 3, (10,))
                else:
                    result = analyzer(manifests, errors, 3)
                self.assertFalse(result["acceptance"])
                self.assertEqual(result["verdict"], "SYNTHETIC_ONLY")

    def test_report_grade_single_run_is_incomplete(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            runs_root = Path(tmp) / "runs" / "E01"
            runs_root.mkdir(parents=True)
            manifest, run_dir = materialize("report-gated", runs_root)
            # A synthetic green G4 is used only to exercise the repeat/coverage
            # gate; the synthetic marker still prevents acceptance.
            manifest["g4"] = {"green": True, "verdict": "GREEN", "evidencePath": "synthetic", "blockers": []}
            (run_dir / "manifest.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
            manifests, errors = analyze._load("E01", Path(tmp) / "runs")
            result = analyze.analyze_e01(manifests, errors, 3)
            self.assertFalse(result["acceptance"])
            self.assertEqual(result["verdict"], "INCOMPLETE")
            self.assertTrue(any("report-grade requires" in reason for reason in result.get("reasons", [])))

    def test_invalid_manifest_is_not_analyzed_as_green(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            runs_root = Path(tmp) / "runs" / "E01"
            runs_root.mkdir(parents=True)
            materialize("invalid-shape", runs_root)
            manifests, errors = analyze._load("E01", Path(tmp) / "runs")
            result = analyze.analyze_e01(manifests, errors, 3)
            self.assertEqual(result["runCount"], 0)
            self.assertEqual(result["verdict"], "INVALID")
            self.assertFalse(result["acceptance"])
            self.assertTrue(result["manifestErrors"])

    def test_missing_runs_are_not_run(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            manifests, errors = analyze._load("E02", Path(tmp) / "runs")
            result = analyze.analyze_e02(manifests, errors, 3, (10, 25, 50, 100))
            self.assertEqual(result["verdict"], "NOT_RUN")
            self.assertFalse(result["acceptance"])


if __name__ == "__main__":
    unittest.main()
