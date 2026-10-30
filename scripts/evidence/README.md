# Member C evidence helpers

- `performance-snapshot.py` records the fixed W13 query set from Prometheus;
  run it 3–5 times per experiment condition and preserve every raw JSON file.
- `release-check.ps1` runs the deterministic release/secret gate. Add
  `-WithDocker` to validate release and HA Compose manifests when Docker is
  available.

Live REL-05/06/08/11/15/16 outputs belong under `evidence/member-c/`; never
replace missing live output with a hand-written PASS file.

Core must be healthy first. No fake config committed. Owner: C (OTel/platform), B (perf methodology). See Docs 02-03.
