# Member C W12–W16 completion matrix

Date: 2026-10-28

| Week | Implementation status | Live evidence status |
|---|---|---|
| W12 | OTel meters, propagation path, Rabbit selective Prometheus scrape, three provisioned dashboards, DB isolation/secret/release gates implemented. Notification placeholder replaced by a durable non-authoritative consumer. | Docker dashboard population and external auth fixture rerun pending. |
| W13 | Prefetch remains bounded/fixed, consumer/redelivery/dedup/Payment metrics exist, and a fixed Prometheus snapshot tool records comparable runs. | 3–5 measured E03 runs pending Docker/performance host. |
| W14 | Three-node RabbitMQ 4.3.6 HA overlay, endpoint-list reconnect, quorum membership reconciliation/growth, REL-15 (fresh Payment connections plus queue drain) and REL-16 (2/3 online during node loss, 3/3 after rejoin) guarded harnesses implemented. | One-node-loss, restore/rejoin and backlog/drain outputs pending Docker. |
| W15 | Reliability report, five diagrams, claim boundaries, source/config freeze and final manifest implemented. | Peer rerun/review pending. |
| W16 | Fail-closed release Compose override (all eight secrets required), secret scanner and deterministic release gate implemented; CI also proves a secretless render fails. | Three live rehearsals, final tag and raw backup pending Docker/peer. |

The matrix separates repository completion from environment-dependent proof;
it does not convert blocked runs into evidence.
