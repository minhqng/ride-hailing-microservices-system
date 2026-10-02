# Member C — Week 3 Payment authority groundwork

Doc 05C §22 / Doc 04 §14: PaymentOperation, AUTHORIZE/CAPTURE/VOID, legal state transitions, six deterministic modes, PostgreSQL migration, state/effect tests and evidence. Notification remains the W2 worker skeleton.

Only PENDING can begin an attempt. PROCESSING resolves to SUCCEEDED, DECLINED, PENDING (transient failure before effect) or UNKNOWN. UNKNOWN/REQUIRES_REVIEW cannot automatically invoke another effect. Reconciliation is W7; same-key uniqueness/fingerprint/replay is W5. These endpoints model isolated simulator operations and do not establish cross-operation Trip capture legality or a production Payment API.

`POST /simulator/operations` is Development-only. Invalid identity, amount, currency or enum returns 400 before writes. Success/decline returns 200; transient failure returns 503; before/after-effect uncertainty returns 504 with a persisted operation ID. `SlowSuccess` delays two seconds. The provider effect is a simulated durable field on the operation row, not an independently running payment provider.

Execution first persists PROCESSING, then records the outcome using a bounded token independent of the caller disconnect. A process crash may leave PROCESSING; this week does not implement recovery of interrupted attempts. The SQL repository disposes connections and persists terminal completed_at. Repeated same-key POSTs can create separate operations until W5: do not retry this test endpoint to simulate business idempotency.

## Verification

- `dotnet test tests/Unit/RideHailing.PaymentService.UnitTests -c Release`: legal/illegal transitions, all types/modes, effect distinctions and input validation.
- `python scripts/test/payment-smoke.py` after Compose startup: 18 combinations, independent SQL assertions, invalid-request no-write checks, restart and reread. CI executes this with real PostgreSQL.
- Payment semantic review by A and peer API rerun are pending actual team actions. CI evidence is linked in the PR; the presence of a test is not evidence of a passing run.

## October review correction

PaymentOperation has private construction and private init accessors. Public callers must use Create, BeginAttempt and Resolve; record copies cannot override state or monetary fields. The repository uses an internal validated Restore path for persisted state. Tests construct states through real transitions and check that no public constructor/setter bypass exists.

The six simulator modes currently model outcomes and persist effect flags in the operation row. Timeout modes return 504 with diagnostic simulator data; they do not independently commit a provider ledger and then suppress a network response. Do not cite this as REL-08/crash-window evidence. Provider-boundary uncertainty/reconciliation proof remains W7 and deterministic process fault injection W11.
