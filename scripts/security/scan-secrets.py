"""Fail on high-confidence secrets in Git-tracked files and unsafe release wiring."""
from __future__ import annotations

import pathlib
import re
import subprocess
import sys

ROOT = pathlib.Path(__file__).resolve().parents[2]

def git_paths(*args: str) -> list[str]:
    return subprocess.check_output(
        ["git", "ls-files", "-z", *args], cwd=ROOT
    ).decode("utf-8").split("\0")


# Include untracked candidates so the pre-commit/release check covers the
# current patch too. Git reports nested untracked repositories as a directory;
# those are skipped below instead of being traversed.
tracked = sorted(set(filter(None, git_paths() + git_paths("--others", "--exclude-standard"))))

patterns = {
    "private key": re.compile(r"-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----"),
    "GitHub token": re.compile(r"\b(?:ghp|gho|ghu|ghs|ghr)_[A-Za-z0-9]{30,}\b"),
    "Slack token": re.compile(r"\bxox[baprs]-[A-Za-z0-9-]{20,}\b"),
    "AWS access key": re.compile(r"\bAKIA[0-9A-Z]{16}\b"),
    "JWT": re.compile(r"\beyJ[A-Za-z0-9_-]{20,}\.[A-Za-z0-9_-]{20,}\.[A-Za-z0-9_-]{10,}\b"),
}

findings: list[str] = []
for relative in filter(None, tracked):
    path = ROOT / relative
    if path.is_dir():
        continue
    if path.name == ".env":
        findings.append(f"{relative}: tracked .env file")
        continue
    try:
        raw = path.read_bytes()
        if b"\0" in raw:
            continue
        text = raw.decode("utf-8")
    except (OSError, UnicodeDecodeError):
        continue
    for label, pattern in patterns.items():
        for match in pattern.finditer(text):
            line = text.count("\n", 0, match.start()) + 1
            findings.append(f"{relative}:{line}: possible {label}")

payment_program = (ROOT / "src/Services/Payment/RideHailing.PaymentService/Program.cs").read_text("utf-8")
if "app.Environment.IsDevelopment()" not in payment_program:
    findings.append("Payment Program.cs: simulator endpoints are not Development-gated")

release_override = (ROOT / "deploy/compose/compose.release.yaml").read_text("utf-8")
if "ASPNETCORE_ENVIRONMENT: Production" not in release_override:
    findings.append("compose.release.yaml: Payment is not forced to Production")

if findings:
    print("SECRET/RELEASE SCAN FAILED")
    print("\n".join(findings))
    sys.exit(1)

print(f"PASS: scanned {len(tracked)} tracked paths; no high-confidence secret or release-gate violation")
