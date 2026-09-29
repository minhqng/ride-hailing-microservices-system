// Stable identity representation freeze — Trip-side contribution (P2, A-W02-08).
//
// Semantic meanings below are CANONICAL (Docs 00 §2.4/§6.3, 01 §3.3, 02 §5.3-5.4).
// CLR/wire choices are W2 MECHANICAL FREEZE: smallest representation consistent
// with repo conventions, JSON interop, future PostgreSQL mapping, and B/C
// integration. No StronglyTypedId framework, no global ID package, no generic
// "Id" that erases distinctions. matchingRound stays visibly a generation.
//
//   tripId:
//     Meaning: one logical ride; stable for ride lifetime.
//     CLR Guid / wire JSON string (uuid) in DTOs + route {tripId:guid}.
//     Future PostgreSQL: uuid. Classification: semantic CANONICAL,
//     representation MECHANICAL.
//   matchingRound:
//     Meaning: one matching generation; stale-round fencing; increments only
//       for a new logical round (DSI-016).
//     CLR int / wire JSON number. Visibly a version, never an
//     opaque Guid. Classification: semantic CANONICAL, representation MECHANICAL.
//     W2 freezes NO initial numerical value: initialization/increment behavior
//     belongs to later matching-workflow implementation.
//   reservationId:
//     Meaning: one logical Driver reservation effect; same attempt reuses the
//       ID across retry/reconciliation; enables idempotency/fencing/exact
//       cleanup (DSI-004/005/018/031/033).
//     CLR Guid / wire JSON string (uuid, nullable in reads when unassigned).
//     Future PostgreSQL: uuid + UNIQUE defense (W3). Classification: semantic
//     CANONICAL, representation MECHANICAL.
//   operationKey:
//     Meaning: one logical Payment operation; reused for the same logical
//       Payment effect (DSI-024/025/034).
//     CLR string / wire JSON string. NOT a Guid: canonical examples use
//       composite keys like "trip-123:authorize:v1" (Doc 02 §15.1, Doc 05A
//       §18.4). Exact composite pattern is TEAM_DECISION_REQUIRED (C owns
//       Payment authority; A proposes lineage, C confirms). This DTO freeze
//       does NOT place operationKey in HTTP bodies.
//   messageId:
//     Meaning: one logical integration message; stable on republish (dedup).
//     CLR Guid / wire string in the FUTURE C-owned envelope (Doc 02 §5.2).
//     NOT an HTTP DTO field in P2. A freezes only the CLR choice for future
//     Outbox use; envelope shape itself is TEAM_DECISION_REQUIRED (C owns
//     MessageEnvelope per Doc 04). Classification: semantic CANONICAL,
//     representation MECHANICAL.
//   correlationId:
//     Meaning: diagnostic business-flow correlation only. NEVER a business
//       idempotency key.
//     CLR string (opaque, preserved, not parsed as Guid so prefixed values
//       like "ride-flow-..." survive) / wire HTTP header X-Correlation-Id,
//       optional, propagated. Classification: semantic CANONICAL,
//     representation+header-name MECHANICAL.
//   traceId:
//     Meaning: observability execution identity only. NEVER a business
//       idempotency key.
//     Transport: W3C traceparent/tracestate headers (OTel), NOT a DTO field,
//       NOT a custom header. CLR: ActivityTraceId/string at the edge.
//     Classification: semantic CANONICAL (Doc 02 §5.4).
//
// CROSS-TEAM OWNERSHIP (P2): A freezes Trip-side DTO/identity representation
// only. A does NOT redefine Driver reserve/reconcile API (B), Payment command
// envelope (C), RabbitMQ MessageEnvelope (C), or auth internals (shared).
// CreateRide idempotency-key transport below is the one additive header A
// freezes; its persistence/fingerprint semantics remain W4.

namespace RideHailing.TripService.Api;

/// <summary>
/// Transport header names for stable-identity propagation (P2 mechanical freeze).
/// </summary>
public static class TripIdentityHeaders
{
    /// <summary>
    /// Optional client-supplied idempotency key for POST /trips.
    /// Transport location/name/representation ONLY in P2; same-key semantics
    /// (same request → same Trip; conflicting request → conflict) belong to W4
    /// and are not claimed here. Standard header name keeps gateway/client
    /// interop future-compatible without inventing a body field.
    /// </summary>
    public const string IdempotencyKey = "Idempotency-Key";

    /// <summary>
    /// Optional diagnostic correlation across Trip → Matching/Driver/Payment.
    /// Never used as an idempotency key. Propagated, not interpreted.
    /// </summary>
    public const string CorrelationId = "X-Correlation-Id";
}
