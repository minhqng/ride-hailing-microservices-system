# G4 executable coverage

Authority: Doc 04 section 23 and Doc 03 G4. Evidence source/tree and the exact Linux run are bound in manifest.json. All named test families executed in that run; the runtime and Identity results come from its platform artifact.

| Required gate | Executed proof |
|---|---|
| Mandatory functional path | core-runtime-gate.py: authenticated intake, autonomous Matching reserve, real Driver/Payment commands, completion and exact cancellation/void; independent authority snapshots |
| CON-01 | DriverReserveConcurrencyTests: distinct/same/mixed reservation contenders; authoritative one-winner and one-effect assertions |
| CON-02 | DriverCon02DeterministicTests and DriverRaceFencingTests: both fenced offline/reserve schedules |
| CON-03 | PaymentConcurrencyTests: one operation row/provider effect per logical key across contenders |
| CON-04 | DriverCompensationTests and exact-token release/fencing tests; real runtime cancellation release |
| REL-01 | LostReserveResponseHttpTests; ProductionMatchingWorkflowTests response loss across real HTTP/providers |
| REL-02 | ProductionMatchingWorkflowTests.Restart_Recovers_Acked_Work_Without_Any_Manual_Trigger; original reservation identity survives process restart |
| REL-04 | TripW11FaultProcessTests.ProducerProcess_CrashedAtSagaDispatchFaultPoint_RestartPublishesSameIdentity; durable same-message publication |
| REL-05 | W9OutboxRelayTests.PublishAcceptedThenMarkLostRepublishesSameIdentityAndDedupKeepsOneEffect, including shared deterministic gate |
| REL-06 | W9DedupTests.CommitBeforeAckRedeliveryFindsCommittedMarkerAndAppliesNoSecondEffect, including shared gate |
| REL-07 | TripW11FaultProcessTests.SagaRestart_WhilePaymentOutcomeBacklogs_SameSagaContinuesToCompletion |
| REL-09 | TripW10SagaTests.DeclinedAuthorization_CompensatesWithExactRelease_UncertainNeverCompensates; real Payment decline/provider proofs. The current runtime script adds live terminal-authorization-decline through the simulator control API, actual broker outcome and exact release; final candidate CI must execute that extension. |
| REL-11 | RabbitBackboneTests.PoisonOrExhaustedTransientMovesToConfirmedErrorQueue and failure-policy/schema rejection tests |
| SEC-01..04 | 55 real Identity/Gateway/Driver/Trip/Matching checks, seven opt-in Identity tests, Trip authorization negatives, Driver guard no-mutation tests; exact method/case-sensitive routing variants included |
| SEC-05 | Driver location validation/ingress and Trip coordinate/identity validation tests; real authenticated invalid-coordinate rejection |
| SEC-06 | Real authenticated same-key/different-payload 409; Payment idempotency/fingerprint integration tests |
| SEC-07 | Real oversized Gateway request 413; existing bounded Driver ingress tests; Identity body/rate bounds |
| SEC-08 | Six owned DB positive controls and all 30 prohibited cross-role DB connections rejected over authenticated TCP |
| SEC-09 | Hygiene/ignored secret paths, no staged or tracked signing keys/tokens, generated credentials excluded from Docker context; current NuGet vulnerability audit reports none from configured sources |
| AUD-01..08 | final-state/core-gate.json: owning Driver/Trip/Matching/Payment/provider records, active assignment snapshots, unique effects, terminal compensation, drained Outboxes and closed cleanup |
| Zero unresolved S0/S1 | Source auth exposure, unclosed Matching cleanup, worker trace discontinuity and fixture races corrected; full CI/runtime/auditor results show no unresolved critical finding in this scope |

W12 additional source regressions cover CON-05/06, REL-03/12/13/14, projection ordering, production stage progression and no unsafe advance from READY/IN_FLIGHT/UNKNOWN. Redis recovery means implemented reconnect/projection recovery; no automatic complete rebuild from an arbitrary empty Redis instance is claimed.

This is G4 acceptance, not W13 performance evidence or W14 live HA/G5 acceptance.
