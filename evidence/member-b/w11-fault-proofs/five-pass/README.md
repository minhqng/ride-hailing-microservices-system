# Five-pass acceptance evidence

Source: `c3907a93404d9016a2afefd5b25434f0bacfb49f`
Tree: `7aad55bb54b574f0edfaf4efaecf3dcaf2caa153`
Run: `five-pass-20261026t174928z`
Command: `PASSES=5 ./scripts/fault/b-w11-full-campaign.sh`
Result: **5/5 consecutive passes for all seven scenarios**.

| Scenario | Passes |
|---|---|
| REL-01 | 5 PASS |
| REL-02 | 5 PASS |
| CON-02 | 5 PASS |
| CON-04 | 5 PASS |
| REL-12 | 5 PASS |
| REL-13 | 5 PASS |
| REL-14 | 5 PASS |

Files:

- `five-pass-summary.json` — machine-readable matrix, source SHA/tree/status,
  image IDs, per-scenario identities and elapsed seconds.
- `scenarios.tsv` — one row per scenario/pass with result, elapsed seconds and
  the `W11 ...` identity/barrier line.
- `campaign.txt` — runner stdout/stderr, including the final PASS line.
- `pass-N/rel01-rel02-run-summary.json` — Phase 1 container run for that pass.
- `pass-N/rel01-rel02-campaign.txt` — Phase 1 campaign log.
- `pass-N/rel01-*-fault.json`, `rel01-*-attempts*.json` — REL-01 barrier,
  UNKNOWN and final durable rows.
- `pass-N/rel02-*-fault.json`, `rel02-*-attempts*.json` — REL-02 barrier,
  pre-crash/kill/final rows.
- `pass-N/rel02-process-*.txt` — real container/PID state before kill, after
  kill (exit 137) and after restart.
- `pass-N/driver-integration.txt`, `pass-N/matching-integration.txt` — full
  CON/REL test logs for that pass.

Every pass used fresh GUID identities and a fresh container namespace. Raw
container logs for the run remain in
`../raw/five-pass-20261026t174928z/` (gitignored).
