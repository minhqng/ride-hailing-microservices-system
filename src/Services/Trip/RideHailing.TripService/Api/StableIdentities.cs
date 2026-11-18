// Stable identity representation, Trip side.
//
// The semantic meanings below are CANONICAL.
// The CLR/wire choices are frozen: smallest representation consistent
// with repo conventions, JSON interop, future PostgreSQL mapping, and
// cross-service integration. No StronglyTypedId framework, no global ID
// package, no generic "Id" that erases distinctions. matchingRound stays
// visibly a generation.
//
//   tripId:
//     Meaning: one logical ride; stable for ride lifetime.
//     CLR Guid / wire JSON string (uuid) in DTOs + route {tripId:guid}.
//     Future PostgreSQL: uuid. Classification: semantic CANONICAL,
//     representation MECHANICAL.
//   matchingRound:
//     Meaning: one matching generation; stale-round fencing; increments only
//     for a new logical round.
//     CLR int / wire JSON number. Visibly a version, never an
//     opaque Guid. Classification: semantic CANONICAL, representation MECHANICAL.
//     No initial numerical value is frozen: initialization/increment behavior
//     belongs to the matching workflow.
//   reservationId:
//     Meaning: one logical Driver reservation effect; same attempt reuses the
//       ID across retry/reconciliation; enables idempotency/fencing/exact
//       cleanup.
//     CLR Guid / wire JSON string (uuid, nullable in reads when unassigned).
//     Future PostgreSQL: uuid + UNIQUE defense. Classification: semantic
//     CANONICAL, representation MECHANICAL.
//   operationKey:
//     Meaning: one logical Payment operation; reused for the same logical
//       Payment effect.
//     CLR string / wire JSON string. NOT a Guid: canonical examples use
//       composite keys like "trip-123:authorize:v1". The exact composite
//       pattern is owned by the Payment authority. This DTO freeze
//       does NOT place operationKey in HTTP bodies.
//   messageId:
//     Meaning: one logical integration message; stable on republish (dedup).
//     CLR Guid / wire string in the future shared envelope.
//     NOT an HTTP DTO field. Only the CLR choice is frozen here, for future
//     Outbox use; the envelope shape itself is owned by the shared messaging
//     layer. Classification: semantic CANONICAL, representation MECHANICAL.
//   correlationId:
//     Meaning: diagnostic business-flow correlation only. NEVER a business
//       idempotency key.
//     CLR string (opaque, preserved, not parsed as Guid so prefixed values
//       like "ride-flow-..." survive) / wire HTTP header X-Correlation-Id,
//       optional, propagated. Classification: semantic CANONICAL,
//       representation+header-name MECHANICAL.
//   traceId:
//     Meaning: observability execution identity only. NEVER a business
//       idempotency key.
//     Transport: W3C traceparent/tracestate headers (OTel), NOT a DTO field,
//       NOT a custom header. CLR: ActivityTraceId/string at the edge.
//       Classification: semantic CANONICAL.
//
// OWNERSHIP: the Trip service freezes only its own DTO/identity
// representation. It does NOT redefine the Driver reserve/reconcile API, the
// Payment command envelope, the RabbitMQ MessageEnvelope, or auth internals.
// The CreateRide idempotency-key transport below is the one additive header
// frozen here; its persistence/fingerprint semantics belong to the create
// flow.

namespace RideHailing.TripService.Api;

/// <summary>
/// Transport header names for stable-identity propagation.
/// </summary>
public static class TripIdentityHeaders
{
    /// <summary>
    /// Optional client-supplied idempotency key for POST /trips.
    /// Transport location/name/representation ONLY; same-key semantics
    /// (same request → same Trip; conflicting request → conflict) belong to the
    /// create flow and are not claimed here. Standard header name keeps
    /// gateway/client
    /// interop future-compatible without inventing a body field.
    /// </summary>
    public const string IdempotencyKey = "Idempotency-Key";

    /// <summary>
    /// Optional diagnostic correlation across Trip → Matching/Driver/Payment.
    /// Never used as an idempotency key. Propagated, not interpreted.
    /// </summary>
    public const string CorrelationId = "X-Correlation-Id";
}
