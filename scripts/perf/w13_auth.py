"""Real Identity grants for disposable performance workloads; never evidence."""
from __future__ import annotations

import os
import time
import uuid

import w13lib as lib

_services: dict[str, tuple[str, float]] = {}


def _grant(path: str, body: dict) -> dict:
    base = f"http://127.0.0.1:{os.environ.get('W13_IDENTITY_PORT', '18101')}"
    status, _, raw, _ = lib.http_request("POST", base + path, body=body)
    if status not in (200, 201):
        raise lib.HarnessError(f"Identity {path} rejected performance bootstrap with HTTP {status}")
    result = lib.parse_json(raw)
    if not isinstance(result, dict):
        raise lib.HarnessError("Identity returned an invalid grant")
    return result


def account(role: str) -> tuple[str, str]:
    credentials = {"username": "perf-" + uuid.uuid4().hex, "password": uuid.uuid4().hex + "!"}
    registered = _grant("/auth/register", {**credentials, "role": role})
    grant = _grant("/auth/login", credentials)
    if grant.get("userId") != registered.get("userId") or not grant.get("access_token"):
        raise lib.HarnessError("Identity grant does not match the registered performance account")
    return registered["userId"], grant["access_token"]


def service(client: str) -> str:
    cached = _services.get(client)
    if cached and cached[1] > time.monotonic():
        return cached[0]
    settings = dict(line.split("=", 1) for line in (lib.ROOT / ".env").read_text().splitlines()
                    if "=" in line and not line.startswith("#"))
    secret = settings.get(f"AUTH_{client.upper()}_SECRET")
    if not secret:
        raise lib.HarnessError("Initialize local authentication before running performance workloads")
    grant = _grant("/connect/token", {"clientId": client, "clientSecret": secret})
    token = grant.get("access_token")
    if not token:
        raise lib.HarnessError("Identity returned no service token")
    _services[client] = (token, time.monotonic() + int(grant["expires_in"]) - 15)
    return token


def reset() -> None:
    _services.clear()
