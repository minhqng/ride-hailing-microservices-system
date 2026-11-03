#!/usr/bin/env python3
"""Fail closed when a dotnet test command returns success without executing tests.

Use freshly isolated TRX output directories; the caller supplies its expected
minimum executed/pass count. SystemTests currently has zero tests and is not
counted as verification.
"""
import argparse
from pathlib import Path
import sys
import xml.etree.ElementTree as ET


def _local_name(tag):
    return tag.rsplit("}", 1)[-1]


def summarize(results_dir):
    files = sorted(Path(results_dir).rglob("*.trx"))
    counts = {"passed": 0, "failed": 0, "notExecuted": 0, "error": 0,
              "timeout": 0, "aborted": 0, "inconclusive": 0}
    for path in files:
        try:
            document = ET.parse(path)
            if _local_name(document.getroot().tag) != "TestRun":
                raise ValueError("not a TestRun document")
            counters = next((node for node in document.iter()
                             if _local_name(node.tag) == "Counters"), None)
            if counters is None:
                raise ValueError("missing TRX Counters")
            for key in counts:
                counts[key] += int(counters.get(key, "0"))
        except (ET.ParseError, OSError, ValueError) as exc:
            raise ValueError(f"Invalid TRX {path}: {exc}") from exc
    return files, counts


def verify(results_dir, min_passed, min_files, max_skipped):
    files, counts = summarize(results_dir)
    problems = []
    if len(files) < min_files:
        problems.append(f"expected at least {min_files} TRX files, found {len(files)}")
    if counts["passed"] < min_passed:
        problems.append(f"expected at least {min_passed} passed tests, found {counts['passed']}")
    for field in ("failed", "error", "timeout", "aborted", "inconclusive"):
        if counts[field]:
            problems.append(f"unexpected {field}={counts[field]}")
    if counts["notExecuted"] > max_skipped:
        problems.append(f"notExecuted={counts['notExecuted']} exceeds {max_skipped}")
    label = "PASS" if not problems else "FAIL"
    print(f"{label}: TRX files={len(files)}, " +
          ", ".join(f"{key}={value}" for key, value in counts.items()))
    if problems:
        raise ValueError("; ".join(problems))
    return counts


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("--results-dir", required=True, type=Path)
    p.add_argument("--min-passed", type=int, required=True)
    p.add_argument("--min-files", type=int, default=1)
    p.add_argument("--max-skipped", type=int, default=0)
    args = p.parse_args()
    if args.min_passed < 1 or args.min_files < 1 or args.max_skipped < 0:
        p.error("positive min-passed/min-files and nonnegative max-skipped required")
    try:
        verify(args.results_dir, args.min_passed, args.min_files, args.max_skipped)
    except ValueError as exc:
        print(f"TRX execution gate rejected run: {exc}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
