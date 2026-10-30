# B-REC-01 / DSI-021 evidence manifest

| Field | Value |
|---|---|
| Branch | `feature/B-W12-matching-recovery` |
| Repair commit | `b4cf93c2e318b32fd191bd8ef9dae8030ec3335d` |
| Repair tree | `109f8c7aee39259d68f309236706436c2e420e05` |
| Base | `4aea3f51b2730f12b986e4f46815981460b6ebb7` (B-W12 head, tree `5902b6f3`) |
| W11 PR | #44 at `3ba3724e` (untouched) |
| W12 PR | #45 at `4aea3f5` (base) |
| Five-pass campaign | `five-pass-20261029t125402z`, source status empty |
| Five-pass driver image | `sha256:dd63e3eff8f8c802d154fceb66f15e969e229087e7a9f22a5284e90641b712a2` |
| Five-pass matching image | `sha256:0debf7a575a0686eaf6fa68cc373c2186c0d56f03a3d6cf0489bd6d8a8d824b9` |

## Artifacts

- `README.md` — architecture, proof matrix, measured results and residuals.
- `auditor-final.md` — B12 auditor regression on the repair commit.
- `w11-five-pass/five-pass-summary.json` — machine-readable 5/5 matrix.
- `w11-five-pass/scenarios.tsv` — per-pass scenario/test/result/IDs.
- `hashes.txt` — SHA-256 over the curated artifacts above.

Raw service logs, container logs and per-pass directories remain under the
gitignored `evidence/**/raw/` tree.

## Reproduce

```bash
DOTNET=/path/to/dotnet bash scripts/fault/b-w11-full-campaign.sh
dotnet build RideHailing.Distributed.slnx -c Release
dotnet test tests/Unit/RideHailing.MatchingService.UnitTests -c Release
dotnet test tests/Integration/RideHailing.MatchingService.IntegrationTests -c Release
dotnet test tests/Integration/RideHailing.DriverService.IntegrationTests -c Release
dotnet test tests/Audit/RideHailing.B12Audit.Tests -c Release
docker compose config --quiet
```
