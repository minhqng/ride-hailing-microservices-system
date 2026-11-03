"""Negative controls for AUD-01: CI must fail closed on zero/missing TRX."""
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

SCRIPT = Path(__file__).with_name("assert-trx.py")
SUITES = [
    "RideHailing.B12Audit.Tests",
    "RideHailing.DriverService.UnitTests",
    "RideHailing.DriverService.IntegrationTests",
    "RideHailing.IdentityService.IntegrationTests",
    "RideHailing.MatchingService.UnitTests",
    "RideHailing.MatchingService.IntegrationTests",
    "RideHailing.NotificationWorker.UnitTests",
    "RideHailing.PaymentService.UnitTests",
    "RideHailing.PaymentService.IntegrationTests",
    "RideHailing.Platform.IntegrationTests",
    "RideHailing.TripService.UnitTests",
    "RideHailing.TripService.IntegrationTests",
]
KNOWN_SKIP = ("RideHailing.IdentityService.IntegrationTests.AuthNegativeTests."
              "RejectedRequestsDoNotMutateAuthoritativeState")


def trx(path, assembly, passed=0, failed=0, skipped=0, counter_skipped=None,
        counter_total=None, report_identity=True, skip_name=None):
    """Write a TRX; counter_skipped mimics VSTest dropping notExecuted counts."""
    rows = ([("Passed", f"{assembly}.pass.{i}") for i in range(passed)] +
            [("Failed", f"{assembly}.fail.{i}") for i in range(failed)] +
            [("NotExecuted", skip_name or f"{assembly}.skip.{i}")
             for i in range(skipped)])
    if counter_skipped is None:
        counter_skipped = skipped
    total = len(rows) if counter_total is None else counter_total
    definitions = ([f'<UnitTest name="x" storage="{assembly}.dll" />']
                   if rows and report_identity else [])
    results = "".join(
        f'<UnitTestResult testName="{name}" outcome="{outcome}" />'
        for outcome, name in rows)
    path.write_text(
        '<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">'
        f'<TestDefinitions>{"".join(definitions)}</TestDefinitions>'
        f'<Results>{results}</Results>'
        '<ResultSummary outcome="Completed">'
        f'<Counters total="{total}" executed="0" passed="{passed}" '
        f'failed="{failed}" notExecuted="{counter_skipped}" />'
        '</ResultSummary></TestRun>',
        encoding="utf-8",
    )


def check(directory, minimum="1", files="1", skipped="0", extra=()):
    return subprocess.run(
        [sys.executable, str(SCRIPT), "--results-dir", str(directory),
         "--min-passed", minimum, "--min-files", files,
         "--max-skipped", skipped, *extra],
        capture_output=True, text=True,
    )


def write_suites(directory, passed=100, skip_identity=False):
    for index, suite in enumerate(SUITES):
        skipped = 1 if skip_identity and "Identity" in suite else 0
        trx(Path(directory) / f"suite{index}.trx", suite,
            passed - skipped, skipped=skipped, counter_skipped=0,
            skip_name=KNOWN_SKIP if skipped else None)


class TrxExecutionGateTests(unittest.TestCase):
    def test_missing_results_rejected(self):
        with tempfile.TemporaryDirectory() as d:
            self.assertNotEqual(0, check(d).returncode)

    def test_zero_executed_rejected(self):
        with tempfile.TemporaryDirectory() as d:
            trx(Path(d) / "empty.trx", "RideHailing.SystemTests")
            self.assertNotEqual(0, check(d).returncode)

    def test_malformed_results_rejected(self):
        with tempfile.TemporaryDirectory() as d:
            (Path(d) / "bad.trx").write_text("<not-xml", encoding="utf-8")
            self.assertNotEqual(0, check(d).returncode)

    def test_counter_total_mismatch_rejected(self):
        with tempfile.TemporaryDirectory() as d:
            trx(Path(d) / "bad.trx", "RideHailing.TripService.UnitTests",
                passed=2, counter_total=3)
            self.assertNotEqual(0, check(d).returncode)

    def test_failure_rejected_even_with_passes(self):
        with tempfile.TemporaryDirectory() as d:
            trx(Path(d) / "failed.trx", "RideHailing.TripService.UnitTests",
                passed=7, failed=1)
            self.assertNotEqual(0, check(d, minimum="7").returncode)

    def test_skip_hidden_by_counters_rejected(self):
        with tempfile.TemporaryDirectory() as d:
            trx(Path(d) / "skipped.trx", "RideHailing.TripService.UnitTests",
                passed=7, skipped=1, counter_skipped=0)
            self.assertNotEqual(0, check(d, minimum="7").returncode)

    def test_known_opt_in_skip_allowed(self):
        with tempfile.TemporaryDirectory() as d:
            trx(Path(d) / "identity.trx",
                "RideHailing.IdentityService.IntegrationTests",
                passed=0, skipped=1, counter_skipped=0, skip_name=KNOWN_SKIP)
            result = check(d, minimum="1", skipped="1",
                           extra=["--allowed-skip", KNOWN_SKIP,
                                  "--require-suite",
                                  "ridehailing.identityservice.integrationtests:0"])
            # One known skip is allowed, but the global pass floor still applies.
            self.assertNotEqual(0, result.returncode)
            trx(Path(d) / "unit.trx", "RideHailing.TripService.UnitTests", passed=1)
            result = check(d, minimum="1", skipped="1",
                           extra=["--allowed-skip", KNOWN_SKIP,
                                  "--require-suite",
                                  "ridehailing.identityservice.integrationtests:0"])
            self.assertEqual(0, result.returncode, result.stderr)

    def test_unexpected_skip_name_rejected(self):
        with tempfile.TemporaryDirectory() as d:
            trx(Path(d) / "unit.trx", "RideHailing.TripService.UnitTests",
                passed=7, skipped=1)
            result = check(d, minimum="7", skipped="1",
                           extra=["--allowed-skip", KNOWN_SKIP])
            self.assertNotEqual(0, result.returncode)

    def test_empty_placeholder_does_not_satisfy_file_floor(self):
        with tempfile.TemporaryDirectory() as d:
            trx(Path(d) / "system.trx", "RideHailing.SystemTests")
            trx(Path(d) / "trip.trx", "RideHailing.TripService.UnitTests", passed=1200)
            self.assertNotEqual(0, check(d, minimum="1200", files="2").returncode)

    def test_missing_required_suite_rejected(self):
        with tempfile.TemporaryDirectory() as d:
            write_suites(d)
            (Path(d) / "suite0.trx").unlink()
            trx(Path(d) / "system.trx", "RideHailing.SystemTests")
            result = check(d, minimum="1000", files="11",
                           extra=["--require-suite",
                                  "ridehailing.b12audit.tests:1"])
            self.assertNotEqual(0, result.returncode)
            self.assertIn("required suite", result.stderr)

    def test_required_suite_minimum_enforced(self):
        with tempfile.TemporaryDirectory() as d:
            write_suites(d)
            result = check(d, minimum="1000", files="12",
                           extra=["--require-suite",
                                  "ridehailing.identityservice.integrationtests:101"])
            self.assertNotEqual(0, result.returncode)

    def test_valid_real_counts_accepted(self):
        with tempfile.TemporaryDirectory() as d:
            write_suites(d, skip_identity=True)
            trx(Path(d) / "system.trx", "RideHailing.SystemTests")
            result = check(d, minimum="1199", files="12", skipped="1",
                           extra=["--allowed-skip", KNOWN_SKIP,
                                  "--required-suites-file",
                                  str(SCRIPT.with_name("required-suites.txt"))])
            self.assertEqual(0, result.returncode, result.stderr)


if __name__ == "__main__":
    unittest.main()
