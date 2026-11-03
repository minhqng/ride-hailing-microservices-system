#!/usr/bin/env python3
"""Fail closed when `dotnet test` exits successfully without executing the expected suites.

The caller supplies a disposable TRX directory, a global pass floor, and the
suite identities that must actually execute. Counts are taken from the TRX
result rows themselves: VSTest can report `notExecuted=0` in <Counters> while
a <UnitTestResult outcome="NotExecuted"> row still exists, so the summary
attributes alone cannot prove that a skip happened.
"""
import argparse
from collections import Counter, defaultdict
from pathlib import Path
import sys
import xml.etree.ElementTree as ET

SKIP_OUTCOME = "notexecuted"
FAILING_OUTCOMES = (
    "failed", "error", "timeout", "aborted", "inconclusive",
    "notrunnable", "disconnected", "warning", "passedbutrunaborted",
)


def _local_name(tag):
    return tag.rsplit("}", 1)[-1]


def _suite_identity(root):
    for node in root.iter():
        if _local_name(node.tag) == "UnitTest":
            storage = (node.get("storage") or "").strip()
            if storage:
                return storage
    return ""


def _results(root):
    for node in root.iter():
        if _local_name(node.tag) == "UnitTestResult":
            yield (node.get("testName") or "<unnamed>",
                   (node.get("outcome") or "").strip().lower())


def _read(path):
    document = ET.parse(path)
    root = document.getroot()
    if _local_name(root.tag) != "TestRun":
        raise ValueError("not a TestRun document")
    counters = next((node for node in root.iter()
                     if _local_name(node.tag) == "Counters"), None)
    if counters is None:
        raise ValueError("missing TRX Counters")
    results = list(_results(root))
    total = int(counters.get("total", "0"))
    if total != len(results):
        raise ValueError(
            f"Counters total={total} disagrees with {len(results)} result rows")
    return root, results


def summarize(results_dir):
    files = sorted(Path(results_dir).rglob("*.trx"))
    counts = Counter()
    skipped = []
    suites = defaultdict(Counter)
    nonempty = 0
    for path in files:
        try:
            root, results = _read(path)
        except (ET.ParseError, OSError, ValueError) as exc:
            raise ValueError(f"Invalid TRX {path}: {exc}") from exc
        if not results:
            continue
        identity = _suite_identity(root)
        if not identity:
            raise ValueError(
                f"Invalid TRX {path}: results without a suite assembly identity")
        nonempty += 1
        key = identity.lower()
        for name, outcome in results:
            counts[outcome] += 1
            suites[key][outcome] += 1
            if outcome == SKIP_OUTCOME:
                skipped.append(name)
    return files, nonempty, counts, skipped, suites


def parse_suite(spec):
    name, separator, minimum = spec.rpartition(":")
    if separator and name and minimum.isdigit():
        return name, int(minimum)
    return spec, 1


def read_suite_file(path):
    specs = []
    for line in Path(path).read_text(encoding="utf-8").splitlines():
        line = line.split("#", 1)[0].strip()
        if line:
            specs.append(parse_suite(line))
    return specs


def verify(results_dir, min_passed, min_files, max_skipped,
           allowed_skip=(), required_suites=()):
    files, nonempty, counts, skipped, suites = summarize(results_dir)
    problems = []
    if nonempty < min_files:
        problems.append(
            f"expected at least {min_files} TRX files with executed tests, "
            f"found {nonempty}")
    if counts["passed"] < min_passed:
        problems.append(
            f"expected at least {min_passed} passed tests, found {counts['passed']}")
    for outcome in FAILING_OUTCOMES:
        if counts[outcome]:
            problems.append(f"unexpected {outcome}={counts[outcome]}")
    if counts[SKIP_OUTCOME] > max_skipped:
        problems.append(
            f"notExecuted={counts[SKIP_OUTCOME]} exceeds {max_skipped}")
    allowed = [pattern.lower() for pattern in allowed_skip]
    unexpected = [name for name in skipped
                  if not any(pattern in name.lower() for pattern in allowed)]
    if unexpected:
        problems.append("unexpected skipped tests: " + ", ".join(sorted(unexpected)))
    for name, minimum in required_suites:
        key = name.lower()
        matches = [suite for identity, suite in suites.items() if key in identity]
        if not matches:
            problems.append(
                f"required suite {name} produced no executed results")
            continue
        passed = sum(suite["passed"] for suite in matches)
        if passed < minimum:
            problems.append(
                f"required suite {name} executed {passed} passing tests, "
                f"expected at least {minimum}")
    label = "PASS" if not problems else "FAIL"
    print(f"{label}: TRX files={len(files)} (executed suites={nonempty}), " +
          ", ".join(f"{key}={value}" for key, value in sorted(counts.items())) +
          f", suite files={len(suites)}")
    if problems:
        raise ValueError("; ".join(problems))
    return counts


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("--results-dir", required=True, type=Path)
    p.add_argument("--min-passed", type=int, required=True)
    p.add_argument("--min-files", type=int, default=1)
    p.add_argument("--max-skipped", type=int, default=0)
    p.add_argument("--allowed-skip", action="append", default=[],
                   help="test-name substring that may be skipped (repeatable)")
    p.add_argument("--require-suite", action="append", default=[],
                   help="assembly substring[:min_passed] that must execute (repeatable)")
    p.add_argument("--required-suites-file", action="append", default=[],
                   help="file of suite specs, one per line (repeatable)")
    args = p.parse_args()
    if args.min_passed < 1 or args.min_files < 1 or args.max_skipped < 0:
        p.error("positive min-passed/min-files and nonnegative max-skipped required")
    required = [parse_suite(spec) for spec in args.require_suite]
    for path in args.required_suites_file:
        required.extend(read_suite_file(path))
    try:
        verify(args.results_dir, args.min_passed, args.min_files,
               args.max_skipped, args.allowed_skip, required)
    except ValueError as exc:
        print(f"TRX execution gate rejected run: {exc}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
