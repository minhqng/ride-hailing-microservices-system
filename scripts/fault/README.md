# fault — deterministic faultpoint platform (W11)

Team-shared fault infrastructure lives here once C delivers the W11 platform
(Doc 05C §30: `FaultPointProvider`, `Consumer.AfterCommitBeforeAck`,
`Outbox.AfterPublishBeforeMark`, `Payment.AfterProviderEffectBeforeResponse`,
environment gating, harness signals/barriers).

## Current state (W11)

C's shared platform is not delivered yet. Member A has landed the one
A-owned named faultpoint it needs for its W11 crash/restart proofs, with a
frozen integration contract so C's provider can adopt it without a Trip
rewrite:

- Name: `Saga.AfterPersistBeforeDispatch`
- Location: the owning service's Outbox relay pre-publish seam
  (`OutboxRelayOptions.BeforePublish`) — necessarily after the local commit,
  before dispatch.
- Scope: the saga's outbound payment dispatches only.
- Activation: default-off config (`TripFaultPoints:*`) AND non-Production
  host environment; enabling in Production fails startup; no HTTP surface.
- Signaling: `reached` marker file + `release` file in a test-owned signal
  directory; bounded wait with a hard deadline.

Authoritative documentation: `src/Services/Trip/RideHailing.TripService/
Integration/FaultPoints/TripFaultPointGate.cs` (contract on the class) and
the Trip service README (W11 section). Do not add a second faultpoint
framework next to it — extend the frozen contract or wait for C's provider.
