# Member C — Week 1 engineering notes

This note records the W1 artifacts from `docs/05C-member-c-payment-messaging-platform.md`. It is a working contract summary, not a replacement for canonical Docs 00–03.

## Identity map

| Identity | Scope | Stable across | Must not be used as |
|---|---|---|---|
| `messageId` | One logical integration message | Outbox republish and broker redelivery | Payment idempotency key |
| `operationKey` | One logical Payment operation/Saga step | HTTP retry, timeout, redelivery, restart | Trace/correlation identifier |
| `reservationId` | One Driver reservation attempt | Reserve reconciliation | Message identity |
| `correlationId` | One end-to-end business workflow | Calls and events in that workflow | Deduplication key |
| `traceId` | One diagnostic trace | Trace propagation where available | Durable workflow state or idempotency key |

## Four crash windows

| Window | Durable fact before failure | Expected repeat/uncertainty | Recovery identity/mechanism |
|---|---|---|---|
| DB commit → publish | Business state and Outbox intent committed together | Relay can publish later; publish can repeat | Stable Outbox `messageId`, consumer dedup |
| Publish → confirm/mark | Broker may have accepted message; Outbox remains pending absent confirm | Relay republishes, so duplicate delivery is possible | Stable `messageId`, confirm before mark-published |
| Consumer commit → ACK | Local effect and dedup marker committed | Broker redelivers after missing ACK | Atomic effect + dedup keyed by `messageId` |
| Payment effect → response | Provider-simulator effect may exist while caller sees timeout | Caller cannot infer failure from timeout | Stable `operationKey`, persisted outcome/query/reconciliation |

## Payment operation state model (W3 target)

The provider simulator records whether a provider-side effect happened separately from the caller-visible operation resolution. `TIMEOUT_AFTER_EFFECT` is deliberately ambiguous to the caller; it must not erase the effect or imply that another operation key is safe.

```text
REQUESTED → SUCCEEDED | DECLINED | FAILED | UNKNOWN
```

Exact retry and transition semantics remain governed by Docs 01–02 and the implemented contract. W1 does not claim that Payment idempotency is implemented; W5 owns that issue pack.

## RabbitMQ ownership transfer

```text
publisher -- publish --> broker -- delivery --> consumer
publisher <-- confirm -- broker
publisher confirm: broker accepted responsibility for publication
consumer -- ACK --> broker, only after local durable processing
```

Publisher confirm and consumer ACK are independent. Neither proves the other side's application-level work completed.

## Retry classification baseline

| Failure class | Handling direction | Reason |
|---|---|---|
| Invalid schema / unsupported version | Reject to bounded terminal path / DLQ | Repeating unchanged input will not fix it |
| Domain conflict / stale transition | Record definitive outcome; no infrastructure retry | This is a business result, not a transient dependency failure |
| Transient dependency/broker/DB failure before commit | Bounded retry with backoff | Retry may succeed, but must have a finite limit |
| Timeout after possible side effect | Preserve `UNKNOWN`; reconcile with same stable identity | Timeout is not proof of failure |
| Retry exhausted / poison message | DLQ with safe diagnostic metadata and operator visibility | Avoid infinite hot requeue loops |

## Defense note

RabbitMQ is an at-least-once transport. A publisher confirm reports broker-side acceptance; a consumer ACK returns responsibility to the broker only after durable local processing. The database effect, dedup marker, and any resulting Outbox intent belong in one local transaction. If a process dies after commit but before ACK, redelivery is expected and a stable message identity makes it harmless. Payment uses a separate stable `operationKey` because transport identity alone does not define one logical charge. Traces explain the path but never decide recovery.
