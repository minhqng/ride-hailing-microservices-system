# Driver/Matching oral-defense workbook

All 45 questions follow Doc 05B §26 in order. These are preparation materials,
not evidence that a human spoke or completed an oral rehearsal. Practice the
short answer first, then open the exact implementation/proof when challenged.
The [technical report](driver-matching-report.md) supplies measured tables,
editable diagrams, limitations and source identities.

Proof references resolve to actual implementation or executable tests:

- [D]: [Driver authority repository](../src/Services/Driver/RideHailing.DriverService/Infrastructure/Persistence/DriverRepository.cs).
- [FSM]: [Driver aggregate](../src/Services/Driver/RideHailing.DriverService/Domain/Drivers/Driver.cs).
- [CON]: [real PostgreSQL contender proof](../tests/Integration/RideHailing.DriverService.IntegrationTests/DriverReserveConcurrencyTests.cs).
- [FENCE]: [Driver race/fencing tests](../tests/Integration/RideHailing.DriverService.IntegrationTests/DriverRaceFencingTests.cs).
- [WORK]: [durable Matching dispatcher](../src/Services/Matching/RideHailing.MatchingService/Application/Matching/MatchingWorkDispatcher.cs).
- [ATTEMPT]: [Matching orchestrator](../src/Services/Matching/RideHailing.MatchingService/Application/Reserve/MatchingReserveOrchestrator.cs).
- [SAFE]: [reconciliation safety matrix](../tests/Integration/RideHailing.MatchingService.IntegrationTests/ReservationReconciliationSafetyTests.cs).
- [REL1]: [real HTTP post-commit response-loss proof](../tests/Integration/RideHailing.MatchingService.IntegrationTests/LostReserveResponseHttpTests.cs).
- [REL2]: [production lifecycle process recovery](../tests/Integration/RideHailing.MatchingService.IntegrationTests/ProductionMatchingLifecycleRecoveryTests.cs).
- [RANK]: [candidate ranker](../src/Services/Matching/RideHailing.MatchingService/Domain/Matching/CandidateRanker.cs).
- [ELIG]: [candidate eligibility](../src/Services/Matching/RideHailing.MatchingService/Domain/Matching/CandidateEligibility.cs).
- [REDIS]: [atomic projection function](../src/Services/Matching/RideHailing.MatchingService/Infrastructure/RedisProjection/update_projection.lua).
- [STALE]: [real Redis stale-event proofs](../tests/Integration/RideHailing.MatchingService.IntegrationTests/DriverProjectionStalenessHandlerTests.cs).
- [PROD]: [production worker/broker workflows](../tests/Integration/RideHailing.MatchingService.IntegrationTests/ProductionMatchingWorkflowTests.cs).
- [E01]: [accepted authenticated contention samples/protocol](../evidence/team/w13-integration/README.md#e01-authenticated-driver-contention).
- [E02]: [accepted public offered-load evidence](../evidence/team/w13-integration/README.md#e02-focused-authenticated-public-intake).
- [E03]: [accepted intake scaling evidence](../evidence/team/w13-integration/README.md).
- [E04]: [corrected backlog measurements](../evidence/team/w14-integration/e04/analysis.json).
- [HA]: [actual primary-leader-loss proof](../evidence/team/w14-integration/ha/final-primary-loss-runtime.json).
- [G4]: [authenticated authority/auditor harness](../scripts/test/core-runtime-gate.py).

| # / canonical question | Short spoken answer | Deeper explanation | Exact implementation/proof | Likely examiner follow-up | Misconception to avoid |
| --- | --- | --- | --- | --- | --- |
| 1. Who owns Driver reservation? | Driver Service and its PostgreSQL database. | Matching requests a decision; only Driver commits legal state and the reservation event intent. | D, FSM | Where does Trip own assignment? | Calling Matching or Redis the authority. |
| 2. Why not Redis? | Redis is a derived discovery index and may be stale. | A stale nomination can conflict at Driver without violating exclusivity. | REDIS, D | Can Redis be available while Driver is down? | Equating cache availability with legal reservability. |
| 3. What is the Driver linearization point? | Successful local reservation transaction commit. | The row lock orders contenders; commit establishes the Driver state and durable Outbox intent together. | D, CON | What if the transaction rolls back after locking? | Calling lock acquisition or HTTP send the committed effect. |
| 4. What is optimistic concurrency? | Validate the version the caller observed before accepting its write. | This implementation combines expected-version checks with PostgreSQL row locking; it is not an EF token demonstration. | D, FSM | Why use a row lock as well? | Claiming all writes use a lock-free CAS or EF concurrency token. |
| 5. Why can two callers read AVAILABLE but only one commit? | Both reads can be old; the guarded mutation is serialized. | After the winner commits, the next lock holder sees RESERVED/new version and conflicts. | CON, D | What would unconditional UPDATE do? | Assuming the earlier SELECT reserves the row. |
| 6. What does a zero-row conditional update mean? | Its predicate no longer matches; inspect the owning contract. | It is not permission to overwrite or conclude every remote outcome failed. Driver reserve here locks then validates, while other conditional workflow writes use affected-row guards. | D, ATTEMPT | Could a concurrent worker have already concluded the attempt? | Saying zero rows always means NO_DRIVER or a system error. |
| 7. Why not blindly retry a concurrency conflict? | Retrying with a fresh version can overwrite newer business work. | Classify the conflict and retain the original attempt identity; do not turn rejection into a new uncontrolled write. | FSM, SAFE | When is same-R retry legal? | Refresh-and-retry-until-success on every conflict. |
| 8. What is a persistence constraint doing here? | It backs up invariants against invalid writes. | Reservation uniqueness and state/reference constraints complement domain checks; they must match the actual invariant. Only the recognized reservation constraint maps to domain conflict. | D, CON | Why not catch every unique violation? | Hiding unexpected database faults as normal conflicts. |
| 9. Why use reservationId? | It names one logical reservation effect. | It fences release/start/complete and lets reconciliation ask about the exact earlier effect rather than current Driver status alone. | D, SAFE | Is it the same as traceId? | Treating diagnostic correlation as business identity. |
| 10. Why is reservationId reused for retry? | A retry must refer to the same work. | A new identity creates a new logical attempt; current matching replay preserves original effect and round provenance. | FSM, ATTEMPT | Does replay replace matchingRound? | Generating a new ID after every timeout or changing recorded provenance. |
| 11. What is fencing? | Reject a command that targets an obsolete state or identity. | Version fencing protects observed state; exact reservation fencing protects cleanup/lifecycle ownership. | FENCE, FSM | Are version and reservation identity interchangeable? | Calling any retry token a complete fence. |
| 12. How can stale Release damage a new reservation? | Driver-only cleanup could clear R-new after R-old ended. | Delay permits the Driver to acquire later work before old compensation arrives. | FENCE | Show the harmful interleaving. | Assuming old messages arrive before new reservations. |
| 13. How is stale Release prevented? | Match the exact Driver, Trip and reservation before mutation. | The owning transaction returns conflict/no-op according to current legal state and never clears unrelated references. | FENCE, D | What about an AVAILABLE replay? | Updating by Driver ID alone. |
| 14. What happens in GoOffline vs Reserve? | One legal outcome wins at Driver. | OFFLINE prevents reservation; a committed reservation prevents offline transition. State/version guards stop both effects from illegitimately winning. | FSM, FENCE | Which final states are forbidden? | Reporting arbitrary conflict responses without reading final state. |
| 15. What is MatchingRound? | One durable matching generation for a Trip. | It stores input identity, current stage, frozen candidates, cancellation and selected outcome; Trip guards which generation is current. | WORK, PROD | How do two workers coordinate? | Keeping round progress only in memory. |
| 16. Why persist MatchingAttempt before remote reserve? | A crash must not erase knowledge of a possible effect. | IN_FLIGHT and the dispatch fence commit before HTTP; recovery knows which R may have reached Driver. | ATTEMPT, SAFE | What if crash occurs just before sending? | Persisting the attempt only after a success response. |
| 17. What is UNKNOWN? | The caller lacks authoritative knowledge of the remote outcome. | It is a durable state that blocks later candidates and stage progress while reconciliation uses the same identity. | ATTEMPT, REL1 | Can UNKNOWN remain after a bounded pass? | Treating it as an exception label with no workflow state. |
| 18. Why timeout ≠ failure? | The server may have committed before the response disappeared. | The client waiting deadline cannot undo a remote transaction or distinguish request loss from response loss. | REL1 | Which faultpoint proves post-commit loss? | Assuming cancellation of HTTP rolls back Driver. |
| 19. Why cannot Matching try D2 while D1 is UNKNOWN? | D1 may already be reserved, so D2 could create another effect. | Safety requires resolving the first identity before dispatching another candidate or advancing stage. | REL1, SAFE | What if Driver is AVAILABLE now? | Ignoring lifecycle history or the dispatch-version fence. |
| 20. How does reconcile use same reservation identity? | Query Driver about original R and preserve it through conclusion. | Exact STARTED/COMPLETED history can establish ownership after current fields change; bare404 remains ambiguity. | SAFE, REL2 | Which negative observations are conclusive? | Treating proxy404 or STALE_RESERVATION as proven rejection. |
| 21. Why is deterministic ranking valuable? | The same fixed input/configuration produces the same ordering. | Frozen per-stage snapshots preserve decisions across restart and support reproducible tests. | RANK, PROD | What is the stable tie-breaker? | Claiming deterministic ranking makes stale discovery authoritative. |
| 22. Why eligibility precedes ranking? | Ineligible Drivers must never win by score. | Filter availability/freshness/capability first, then rank the remaining fixed candidates deterministically. | ELIG, RANK | Can proximity override stale location? | Scoring invalid candidates and filtering only after selection. |
| 23. Why is Redis state allowed to be stale? | Final reservation safety is checked at Driver. | Projection lag affects candidate efficiency and liveness; it cannot legitimately authorize an assignment. | REDIS, D | How does stale state increase work? | Calling eventual consistency harmless to all user-visible behavior. |
| 24. What does Redis Function atomicity protect? | One compare/update of projection metadata, GEO membership and receipt. | Other Redis callers cannot interleave inside that invocation; version/session/sequence gates avoid regression. | REDIS, STALE | What happens on wrong key type? | Separating metadata and GEO changes into an alleged atomic pair of client calls. |
| 25. What does it not protect? | Driver/Trip transactions or cross-service agreement. | Redis cannot make the HTTP reserve and independent databases atomic, nor rebuild lost acknowledged events automatically. | REDIS, G4 | What restores total projection loss? | Claiming reconnect implies full reconstruction. |
| 26. Why not use Redlock for Driver assignment? | The PostgreSQL owner already guards the reservation decision. | Adding a cache lock would not replace authoritative state/version checks or solve lost-response reconciliation. | D, REL1 | What if a lock lease expires during a slow request? | Making distributed lock availability a new assignment authority. |
| 27. What happens if Redis is down? | Discovery degrades and work stays recoverable. | It is not conclusive NO_DRIVER. Established connections may reconnect; startup registration failure can require restart; data loss needs explicit replay. | PROD, REDIS | Is recovery automatic in every case? | Promising an automatic complete rebuild. |
| 28. What happens if Driver Service is down? | A possible dispatched effect stays unresolved with the same R. | Matching owns bounded reconciliation/backoff. It cannot safely advance on transport failure. | ATTEMPT, SAFE | What happens after reconciliation budget exhaustion? | Converting exhaustion into invented rejection. |
| 29. What does CON-01 prove? | Synchronized callers produce one valid current reservation. | Read final authority, identity, version and Outbox; also separate conflicts and unexpected failures. | CON, E01 | Does it prove production capacity? | Using only one HTTP200 as the oracle. |
| 30. Why are99 conflicts at N=100 not99 system errors? | The authority legally rejected competing claims. | Expected domain409s are an intended safety outcome; timeouts and5xx belong to separate populations. | CON, E01 | Can aggregate latency fall as conflicts grow? | Combining all non200 responses into infrastructure failure. |
| 31. What does REL-01 prove? | A lost post-commit reserve response converges using original R. | The bounded faultpoint observes committed Driver+Outbox, Matching UNKNOWN, no D2 and exact final agreement. | REL1 | How do you know commit preceded the fault? | Replacing the commit-window barrier with a random delay. |
| 32. What does REL-02 prove? | Durable Matching work resumes after process failure. | Recovery reads persisted round/attempt/fence/history and does not depend on the failed worker's memory. | REL2, PROD | What if Trip started before restart? | Equating a same-process retry with process restart proof. |
| 33. What is final-state oracle? | Independent assertions against owning durable state. | Compare Driver/Trip/Matching/Saga/Payment authorities after quiescence; logs and traces supply timing/context. | G4, CON | Why inspect more than one database? | Treating a screenshot or message count as authoritative business state. |
| 34. Why use barriers instead of sleeps? | A barrier establishes the intended causal race window. | Time delays alone do not prove overlap or post-commit placement on a slow machine. | CON, REL1 | Can polling be valid? | Calling any fixed wait a deterministic race proof. |
| 35. Why use real PostgreSQL? | Locks, transaction rollback and constraints are provider behavior. | An in-memory store cannot establish the owning database's concurrency and persistence contract. | CON, FENCE | Which cases still belong in unit tests? | Replacing database proof with mocked success responses. |
| 36. Why use open arrival-rate model? | Offer arrivals independently of current response time. | It answers a fixed offered-load question and exposes achieved rate and dropped work explicitly. | E02 | What remains controlled across rates? | Confusing configured arrivals with achieved business throughput. |
| 37. What is coordinated omission? | A waiting generator can stop offering work while the system slows. | A closed loop may omit the arrivals/latencies the intended independent workload would have created. | E02 | Does an open model remove every load-generator limit? | Saying open scheduling guarantees all offered iterations execute. |
| 38. What are dropped iterations? | Scheduled iterations the generator could not start. | They can reflect insufficient virtual users or response-induced occupancy; report them alongside offered/achieved rates. | E02 | How many occurred in accepted runs? | Counting them as successful requests or silently excluding them. |
| 39. Why report P50/P95? | Show typical and tail behavior with sample size. | State quantile definition, semantic outcome population, per-run n and run dispersion; N=1 cannot establish a tail SLA. | E01, E02 | Are these pooled quantiles? | Reporting P99 from tiny samples or only means. |
| 40. Why rerun auditor after load? | Fast responses do not establish invariant safety. | Load can expose duplicate effects, assignments or unresolved cleanup; owning rows must still satisfy business rules. | G4, E01, E02 | When is quiescence reached? | Calling a low-latency corrupt system a success. |
| 41. What does E04 formula approximate? | Accumulated work and drain time under compatible rates/populations. | B≈lambda*T; when mu>lambda, drain≈B/(mu−lambda). Ready queue depth differs from outstanding business work; startup/polling and varying rates affect observations. | E04 | Which B and lambda were measured? | Mixing ready backlog with business completions under an unlabeled B. |
| 42. What does RabbitMQ quorum test prove? | Recorded queues survive a real leader loss with a live majority. | The actual three-node profile retained2/3 members across journeys and restored3/3; owning final-state checks still passed. | HA | Was the killed node actually a leader? | Calling a single-node restart a quorum proof. |
| 43. What does it not prove? | Whole-host, database or arbitrary-partition availability. | All nodes share one physical host; PostgreSQL/Redis HA, unlimited faults and zero downtime were not tested. | HA, E04 | What if two quorum members disappear? | Generalizing one-node loss to every failure model. |
| 44. What are the system's scalability limitations? | One local host, database contention and bounded workloads. | E03 is intake with workers disabled; replicas increase total app resources. E02 is no-fleet intake; E04 is bounded recovery, not completed-ride capacity. | E01–E04, report | Which bottleneck did the measurement isolate? | Extrapolating production capacity from local smoke or conflict-heavy latency. |
| 45. What would you change first for real production scale? | Measure a representative workload and bottleneck before changing architecture. | Establish production identity/secrets/operations and capacity targets, then test a bounded change while preserving authority/idempotency/recovery. This is future advice, not implemented scope. | Report limitations, G4 | Would you shard the Driver authority immediately? | Promising a new broker/framework or unmeasured optimization solves scale. |

## Timed mock drill

Use a ten-minute practice slot: 90 seconds explaining the two report diagrams,
then six randomly selected questions at 45 seconds each, two adversarial
follow-ups at 60 seconds each, and two minutes locating exact proof artifacts.
Suggested adversarial sequence: Q5 → Q19 → Q20 → Q25 → Q39 → Q41. The model
answer should name the authority, identity, uncertainty and observed proof
limit before discussing implementation detail.

Ask the examiner to interrupt with: “Trip completed before reconciliation,”
“the404 came from a proxy,” “Redis restarted with empty data,” and “the4-replica
run added more CPU.” Correct answers point to historical ownership, provenance,
explicit replay, and controlled-resource limitations respectively.

Score human practice only after an actual interaction: correctness, timing,
ability to locate evidence and refusal to overclaim. Record date, participants,
actual questions and observed timing separately from this workbook. No human
speaking score or completed human rehearsal is asserted here.
