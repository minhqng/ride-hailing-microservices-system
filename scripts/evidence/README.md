# Member C evidence helpers

- `performance-snapshot.py` records the fixed W13 Prometheus query set; run it
  3–5 times per experiment condition and preserve every raw JSON file.
- `release-check.ps1` runs the deterministic W16 release/secret gate. Add
  `-WithDocker` when Docker is available directly in the current shell.
- `../demo/member-c-mock-traffic.ps1` publishes unique Payment commands plus
  exact transport replays so dashboards expose throughput and dedup behavior.

Live REL-05/06/08/11/15/16 outputs belong under `evidence/member-c/`. Never
replace missing live output with a hand-written PASS file.
