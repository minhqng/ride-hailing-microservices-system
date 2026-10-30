#!/usr/bin/env python3
"""W13 performance campaign CLI.

Examples:
  python scripts/perf/w13.py g4
  python scripts/perf/w13.py e01 --mode smoke --n 1,2,10
  python scripts/perf/w13.py e02 --mode smoke
  python scripts/perf/w13.py e03 --mode smoke --replicas 1,2 --rate 5 --duration 5s
  python scripts/perf/w13.py analyze --experiment ALL
  python scripts/perf/w13.py reset --yes
"""

from __future__ import annotations

import argparse
import os
import subprocess
import sys
from pathlib import Path

import w13lib as lib


def _dispatch(module: str, argv: list[str]) -> int:
    script = lib.PERF_DIR / module
    result = subprocess.run(
        [sys.executable, str(script), *argv],
        cwd=str(lib.ROOT),
        env=os.environ.copy(),
    )
    return result.returncode


def command_g4(args: argparse.Namespace) -> int:
    readiness = lib.g4_readiness(Path(args.evidence) if args.evidence else None)
    if args.write and readiness.get("evidencePath"):
        # Only write when the path is in the repository; never invent evidence.
        path = Path(readiness["evidencePath"])
        if path.exists():
            lib.print_json({"status": "observational", "note": "existing evidence was not overwritten",
                            "readiness": readiness})
        else:
            lib.write_json(path, readiness)
    lib.print_json(readiness)
    return 0 if readiness.get("green") else 3


def command_env(args: argparse.Namespace) -> int:
    environment = lib.capture_environment(include_observed=not args.configured_only)
    if args.out:
        lib.write_json(Path(args.out), environment)
    else:
        lib.print_json(environment)
    return 0


def command_up(args: argparse.Namespace) -> int:
    try:
        lib.compose_up(args.services, build=not args.no_build, scale=args.scale,
                       wait_timeout=args.wait_timeout)
    except lib.HarnessError as exc:
        print(str(exc), file=sys.stderr)
        return 2
    lib.print_json({"status": "UP", "project": lib.PROJECT, "services": args.services,
                    "scale": args.scale})
    return 0


def command_down(args: argparse.Namespace) -> int:
    try:
        lib.compose_down(volumes=not args.keep_volumes)
    except lib.HarnessError as exc:
        print(str(exc), file=sys.stderr)
        return 2
    lib.print_json({"status": "DOWN", "project": lib.PROJECT, "volumes": not args.keep_volumes})
    return 0


def command_reset(args: argparse.Namespace) -> int:
    if lib.PROJECT != "ride-hailing-w13":
        print("refusing reset: compose project is not the isolated W13 project", file=sys.stderr)
        return 4
    if not (args.yes or os.environ.get("W13_ALLOW_RESET") == "1"):
        print("refusing reset without --yes or W13_ALLOW_RESET=1 (this destroys W13 volumes only)",
              file=sys.stderr)
        return 4
    try:
        lib.compose_down(volumes=True)
    except lib.HarnessError as exc:
        print(str(exc), file=sys.stderr)
        return 2
    lib.print_json({"status": "RESET", "project": lib.PROJECT})
    return 0


def command_audit(args: argparse.Namespace) -> int:
    script = lib.ROOT / "scripts" / "audit" / "b-w12-g4.sh"
    if not script.exists():
        print(f"authoritative auditor script is missing: {script}", file=sys.stderr)
        return 2
    env = os.environ.copy()
    if args.out:
        out = Path(args.out)
        out.mkdir(parents=True, exist_ok=True)
        env["B12_AUDIT_OUTPUT_DIR"] = str(out)
    env.setdefault("TESTCONTAINERS_RYUK_DISABLED", "true")
    result = subprocess.run(["bash", str(script), *args.passthrough], cwd=str(lib.ROOT), env=env)
    return result.returncode


def command_validate(args: argparse.Namespace) -> int:
    checks = []
    compose_config = subprocess.run(
        [*lib.compose_args("config", "--quiet")], cwd=str(lib.ROOT), text=True,
        capture_output=True, env=os.environ.copy(),
    )
    checks.append({"check": "compose config", "exit": compose_config.returncode})
    python_tests = subprocess.run(
        [sys.executable, "-m", "unittest", "discover", "-s", "scripts/perf/tests", "-v"],
        cwd=str(lib.ROOT), env=os.environ.copy(),
    )
    checks.append({"check": "synthetic analyzer tests", "exit": python_tests.returncode})
    failed = [check for check in checks if check["exit"] != 0]
    lib.print_json({"status": "PASS" if not failed else "FAIL", "checks": checks})
    return 0 if not failed else 2


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description="W13 performance campaign harness",
        epilog="Experiment passthrough: e01|e02|e03 [experiment flags]; analyze [analysis flags].",
    )
    sub = parser.add_subparsers(dest="command", required=True)

    g4 = sub.add_parser("g4", help="print/validate curated G4 readiness")
    g4.add_argument("--evidence", default=None)
    g4.add_argument("--write", action="store_true", help="write a new readiness file only if none exists")
    g4.set_defaults(func=command_g4)

    env_parser = sub.add_parser("env", help="capture configured and observed environment")
    env_parser.add_argument("--out", default=None)
    env_parser.add_argument("--configured-only", action="store_true")
    env_parser.set_defaults(func=command_env)

    up = sub.add_parser("up", help="start the isolated W13 compose environment")
    up.add_argument("services", nargs="*", default=["postgres", "rabbitmq", "driver"])
    up.add_argument("--scale", type=int, default=None)
    up.add_argument("--no-build", action="store_true")
    up.add_argument("--wait-timeout", type=int, default=240)
    up.set_defaults(func=command_up)

    down = sub.add_parser("down", help="stop the W13 compose environment")
    down.add_argument("--keep-volumes", action="store_true")
    down.set_defaults(func=command_down)

    reset = sub.add_parser("reset", help="destroy the disposable W13 volumes")
    reset.add_argument("--yes", action="store_true")
    reset.set_defaults(func=command_reset)

    audit = sub.add_parser("audit", help="run the authoritative B-owned invariant auditor")
    audit.add_argument("--out", default=None)
    audit.add_argument("passthrough", nargs=argparse.REMAINDER)
    audit.set_defaults(func=command_audit)

    validate = sub.add_parser("validate", help="compose config plus synthetic parser/analyzer tests")
    validate.set_defaults(func=command_validate)

    return parser


def main(argv: list[str] | None = None) -> int:
    argv = list(sys.argv[1:] if argv is None else argv)
    if argv and argv[0] in ("e01", "e02", "e03"):
        return _dispatch(f"w13_{argv[0]}.py", argv[1:])
    if argv and argv[0] == "analyze":
        return _dispatch("w13_analyze.py", argv[1:])
    parser = build_parser()
    args = parser.parse_args(argv)
    return int(args.func(args))


if __name__ == "__main__":
    raise SystemExit(main())
