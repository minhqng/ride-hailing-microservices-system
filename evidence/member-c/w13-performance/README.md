# C-W13 — performance evidence without semantic drift

`scripts/evidence/performance-snapshot.py` captures one labelled Prometheus
snapshot for each controlled E03/E04 run. It records queue ready/unacked,
redelivery, consumer delivery/dedup, Outbox pending and Payment outcomes.

```bash
python scripts/evidence/performance-snapshot.py \
  --run e03-replica2-run1 \
  --output evidence/member-c/w13-performance/outputs/e03-replica2-run1.json
```

Comparison rule: consumer replica count may vary; prefetch, database pool,
retry/DLQ policy and timeout semantics remain fixed. Expected domain conflicts
must not be counted as infrastructure errors, and a configured timeout is not
reported as an observed SLA.

Raw output is run evidence. Preserve at least 3–5 measured runs before making
a bottleneck claim.
