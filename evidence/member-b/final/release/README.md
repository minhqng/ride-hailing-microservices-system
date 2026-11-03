# Driver/Matching release evidence

This is B's incremental release/defense contribution over
[the freeze evidence](../freeze/README.md), not a team-wide release certificate.
The [manifest](manifest.json) maps all nine B tasks and separates technical
delivery from pending human/peer/lead actions.

- [Operator runbook and A/C handoff](../../../../docs/driver-matching-demo.md).
- [All45 defense answers and timed practice](../../../../docs/driver-matching-defense.md).
- [Measured report and editable proof diagrams](../../../../docs/driver-matching-report.md).
- [Candidate/tag/CI verification](release-verification.json).
- [Curated execution totals and timing](test-output.txt).

Reproduce from a fresh full checkout of the manifest's source commit:

```powershell
python scripts/demo/member-b-live.py --repeats 3
```

The command requires Docker Linux containers, Git, Python3 and free fixed
Compose ports. A host .NET SDK is not required. It rejects dirty source,
pre-existing owned-name resources, unsafe output destinations and Python
optimization; it overrides inherited Compose overlays. Source, real durations,
provider regressions and per-trial authoritative results are written beneath
the ignored `outputs/` directory. Failed runs retain failure context and never
become curated passing evidence automatically.

Each of three independent trials runs authenticated N=10 contention, real
Redis stale-version/sequence replay with immutable PostgreSQL authority,
bounded real HTTP post-commit loss/reconciliation, and the three integrated
production-handler journeys. Actual application rows, exact reservation IDs,
Outbox effects and AUD-01..08 are retained. Raw build/test streams remain
ignored; only intentional final-state proof, measured samples/timings and
digests are curated here.

The isolated response-loss demonstration uses real Driver Kestrel endpoints
and Matching's production typed client/orchestrator. The full B regression
separately exercises autonomous production workers, broker intake, process
restart and STARTED/COMPLETED history. Redis replay is explicitly at the
production Function boundary; handler/receipt tests remain separate evidence.
The shared lifecycle actor invokes production handlers; no public lifecycle
HTTP endpoint is claimed.

Three automated technical rehearsals do not establish human speaking ability,
three human role handoffs or actual peer sign-off. Those remain PENDING.
Remote archive/evidence tags do not constitute a final release tag. Only the
lead can approve and create the shared final tag after exact integrated-source
verification; this task creates neither a final tag nor a shared release.
