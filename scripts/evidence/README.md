# Member C evidence helpers

- `performance-snapshot.py` records the fixed W13 query set from Prometheus;
  run it 3–5 times per experiment condition and preserve every raw JSON file.
  It fails (and still writes a diagnostic JSON) when a required queue, Outbox
  or Saga series is missing or non-finite instead of printing PASS.
- `member-a-recovery-check.ps1 -MetricsOnly` requires non-empty Outbox series
  and enforces `-MaxPending` / `-MaxOldestSeconds` / `-MaxFailedVisible`
  thresholds; `saga_active` may be empty only when no non-terminal Saga work
  remains.
- `release-check.ps1` runs the deterministic release/secret gate. Add
  `-WithDocker` to validate release and HA Compose manifests when Docker is
  available; release secrets must be supplied in the process environment and
  the gate also proves the release overlay fails closed without them.

Live REL-05/06/08/11/15/16 outputs belong under `evidence/member-c/`; never
replace missing live output with a hand-written PASS file.

Core must be healthy first. No fake config committed. Owner: C (OTel/platform), B (perf methodology). See Docs 02-03.
