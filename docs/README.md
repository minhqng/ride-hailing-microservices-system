# Documentation index

The documentation is organized into three groups: the canonical design
contracts, the operating guides, and the final submission record.

## 1. Canonical contracts

| Document | Content |
|---|---|
| [00-project-scope-architecture.md](00-project-scope-architecture.md) | Scope, service boundaries, architecture and design rationale |
| [01-domain-correctness-contract.md](01-domain-correctness-contract.md) | Domain invariants, authority boundaries and transition rules |
| [02-distributed-workflow-engineering.md](02-distributed-workflow-engineering.md) | Messaging, Outbox, Saga, idempotency, retry and recovery rules |
| [03-verification-experiments-demo.md](03-verification-experiments-demo.md) | Verification method, required scenarios and evidence contract |

Reading order: 00 → 01 → 02 → 03. If an implementation detail and a contract
disagree, treat the contract as the requirement and record the discrepancy in
the issue tracker; do not silently reinterpret the invariant.

## 2. Operating guides

| Document | Content |
|---|---|
| [authentication-runtime.md](authentication-runtime.md) | JWT issuance, service grants and local key setup |
| [messaging-runtime.md](messaging-runtime.md) | Publisher/consumer lifecycle, confirms, acknowledgement and retry ownership |
| [release.md](release.md) | Release identity, deployment procedure and known limitations |
| [verification.md](verification.md) | Final verified results, suite counts and release evidence |

## 3. Final submission

| Document | Content |
|---|---|
| [demo.md](demo.md) | The demonstration and defense runbook (Vietnamese): preparation, architecture, live ride, extended scenarios and examiner Q&A |
| [report.md](report.md) | Final technical report (source of `report.pdf`) |
| [report.pdf](report.pdf) | Final project report (academic submission) |
| [slides.pdf](slides.pdf) | Presentation slides |

The release evidence bundle lives under
[`evidence/release/v1.0.1/`](../evidence/release/v1.0.1/README.md). It anchors
the verified results to the source revision recorded in its `manifest.json`.
