# A-W13 — performance correctness support

Member A does not tune away correctness for throughput. This slice makes Trip,
Saga and Outbox state visible during E02/E04 runs and preserves a bounded raw
metric snapshot for each run.

Delivered:

- `RideHailing.Trip` is registered in the shared OTel meter pipeline used by
  the live collector.
- Grafana dashboard `ride-trip-saga` shows active Saga steps, compensation /
  review entries, Trip decisions and Outbox backlog/age.
- `performance-snapshot.py` captures queue, consumer, Outbox, Saga, Trip and
  Payment metrics without changing retry/prefetch policy.
- `TripInvariantAuditor` from W12 remains the final-state oracle after a
  selected performance run.

Example:

```bash
python scripts/evidence/performance-snapshot.py \
  --run e04-trip-backlog-run1 \
  --output evidence/member-a/w13-performance/outputs/e04-trip-backlog-run1.json
```

The output directory is runtime evidence and is not a substitute for the
auditor. Expected domain conflicts must remain separate from system errors.
