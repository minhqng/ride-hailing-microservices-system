"""Negative controls for AUD-01: CI must fail closed on zero/missing TRX."""
import importlib.util
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

SCRIPT = Path(__file__).with_name("assert-trx.py")


def trx(path, passed, failed=0, skipped=0):
    path.write_text(
        '<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">'
        '<ResultSummary outcome="Completed"><Counters total="{}" executed="{}" '
        'passed="{}" failed="{}" notExecuted="{}" /></ResultSummary></TestRun>'
        .format(passed + failed + skipped, passed + failed, passed, failed, skipped),
        encoding="utf-8",
    )


def check(directory, minimum="1", files="1", skipped="0"):
    return subprocess.run(
        [sys.executable, str(SCRIPT), "--results-dir", str(directory),
         "--min-passed", minimum, "--min-files", files, "--max-skipped", skipped],
        capture_output=True, text=True,
    )


class TrxExecutionGateTests(unittest.TestCase):
    def test_missing_results_rejected(self):
        with tempfile.TemporaryDirectory() as d:
            self.assertNotEqual(0, check(d).returncode)

    def test_zero_executed_rejected(self):
        with tempfile.TemporaryDirectory() as d:
            trx(Path(d) / "empty.trx", 0)
            self.assertNotEqual(0, check(d).returncode)

    def test_malformed_results_rejected(self):
        with tempfile.TemporaryDirectory() as d:
            (Path(d) / "bad.trx").write_text("<not-xml", encoding="utf-8")
            self.assertNotEqual(0, check(d).returncode)

    def test_failure_rejected_even_with_passes(self):
        with tempfile.TemporaryDirectory() as d:
            trx(Path(d) / "failed.trx", 7, failed=1)
            self.assertNotEqual(0, check(d, minimum="7").returncode)

    def test_unexpected_skip_rejected(self):
        with tempfile.TemporaryDirectory() as d:
            trx(Path(d) / "skipped.trx", 7, skipped=1)
            self.assertNotEqual(0, check(d, minimum="7").returncode)

    def test_missing_suites_rejected(self):
        with tempfile.TemporaryDirectory() as d:
            trx(Path(d) / "a.trx", 1200)
            self.assertNotEqual(0, check(d, minimum="1200", files="12").returncode)

    def test_valid_real_counts_accepted(self):
        with tempfile.TemporaryDirectory() as d:
            for n in range(12):
                trx(Path(d) / f"suite{n}.trx", 102 if n < 11 else 101,
                    skipped=1 if n == 11 else 0)
            self.assertEqual(0, check(d, minimum="1200", files="12", skipped="1").returncode)


if __name__ == "__main__":
    unittest.main()
