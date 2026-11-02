"""Create disposable local/CI credentials in ignored .env; never prints secrets."""
import base64
import os
from pathlib import Path
import secrets
import shutil
import subprocess
import tempfile

root = Path(__file__).resolve().parents[2]
target = root / ".env"
text = target.read_text(encoding="utf-8") if target.exists() else (root / ".env.example").read_text(encoding="utf-8")
existing = dict(line.split("=", 1) for line in text.splitlines() if "=" in line and not line.startswith("#"))
names = ["AUTH_PUBLIC_KEY_BASE64", "AUTH_PRIVATE_KEY_BASE64", "AUTH_MATCHING_SECRET", "AUTH_TRIP_SECRET"]
if all(existing.get(name) for name in names):
    print("Local authentication credentials already initialized.")
    raise SystemExit(0)
if any(existing.get(name) for name in names):
    raise SystemExit("Incomplete authentication configuration; restore a complete key pair and client credentials.")
openssl = shutil.which("openssl")
if not openssl and os.name == "nt":
    candidate = Path("C:/Program Files/Git/usr/bin/openssl.exe")
    if candidate.exists():
        openssl = str(candidate)
if not openssl:
    raise SystemExit("OpenSSL is required for local credential initialization.")
with tempfile.TemporaryDirectory(prefix="ride-auth-") as directory:
    private = Path(directory) / "private.pem"
    public = Path(directory) / "public.pem"
    subprocess.run([openssl, "genpkey", "-algorithm", "RSA", "-pkeyopt", "rsa_keygen_bits:3072", "-out", str(private)], check=True, capture_output=True)
    subprocess.run([openssl, "pkey", "-in", str(private), "-pubout", "-out", str(public)], check=True, capture_output=True)
    values = dict(zip(names, [base64.b64encode(public.read_bytes()).decode(), base64.b64encode(private.read_bytes()).decode(), secrets.token_urlsafe(48), secrets.token_urlsafe(48)]))
lines = [line for line in text.splitlines() if line.split("=", 1)[0] not in names]
lines.extend(name + "=" + values[name] for name in names)
target.write_text("\n".join(lines) + "\n", encoding="utf-8")
if os.name != "nt":
    target.chmod(0o600)
print("Initialized disposable local authentication credentials in ignored .env.")
