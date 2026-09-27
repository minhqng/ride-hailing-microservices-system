# Evidence

Commit manifests/summaries and auditor outputs. Do not commit large raw dumps or secrets.

- Each scenario run: `evidence/<SCENARIO>/run-<UTC>-<runId>/` with manifest, config, assertions, raw logs.
- Preferred: final DB assertions + invariant auditor output, not screenshots.
