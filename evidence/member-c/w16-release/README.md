# W16 release rehearsal

Run the deterministic gate from the repository root:

```powershell
./scripts/evidence/release-check.ps1
```

If Docker is available in the same shell, export all eight passwords required
by `deploy/compose/compose.release.yaml` and include `-WithDocker`. That mode
validates both a positive release render and a negative secretless render.
Otherwise, render both release and HA Compose configurations explicitly through
the host that owns the Docker engine. Generate dashboard traffic with:

```powershell
./scripts/demo/member-c-mock-traffic.ps1 -Count 24
```

The generator sends stable envelopes and exact duplicate deliveries. Its PASS
means RabbitMQ accepted and routed the commands; Payment state, dedup counters,
and Notification projections must still be checked in the dashboards/database.
