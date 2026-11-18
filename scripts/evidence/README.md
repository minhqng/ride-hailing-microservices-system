# Evidence helper scripts

These helpers support the release gates. They are not a substitute for the
executable gates in `scripts/test/`.

- `runtime-observations.py` records the bounded Prometheus observations that CI
  publishes alongside the platform-smoke artifacts.
- `release-check.ps1` runs the deterministic release/secret gate. Add
  `-WithDocker` for the Compose render and HA-overlay checks; without it the
  script reports a unit/build gate, not release acceptance.

Live outputs belong under `evidence/`. Never replace missing live output with a
hand-written PASS file.
