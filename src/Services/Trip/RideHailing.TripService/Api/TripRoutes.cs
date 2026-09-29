// Trip HTTP route/method mechanical freeze.
//
// These are DOCUMENTED CONSTANTS ONLY. Program.cs does NOT MapPost/MapGet
// business routes, so no unimplemented endpoint can pretend success.
// Real handlers arrive with later domain implementation.
//
// W2 MECHANICAL FREEZE — reasons:
//   - Resource noun "/trips" (not "/rides"): aligns with the canonical
//     aggregate name Trip (Doc 01 §6) and the stable tripId identity, avoiding
//     a ride/trip synonym split across Trip-owned URLs. Handler names
//     (CreateRide/CancelRide) keep the user-facing verb; URLs keep the
//     authority noun.
//   - POST for CreateRide (not PUT): idempotency here is a domain property
//     backed by durable identity + Idempotency-Key header, not an HTTP-method
//     property (Doc 00 §2.11). POST is the correct transport for "create one
//     logical ride, deduplicated by key" (W4 semantics, not proven here).
//   - GET for GetTrip: safe read of one logical ride by stable identity.
//   - POST /trips/{id}/cancel (not DELETE): cancel is a guarded domain
//     transition with compensation/cleanup side effects, not a resource
//     deletion. Explicit sub-resource action keeps the terminal intent visible.
//   - No /v1 prefix: no versioned HTTP surface exists yet in the repo
//     (only /health/*, /info). Versioning stays future-additive; integration
//     message versioning (V1) is a separate C-owned concern (Doc 02 §6).
//   - Route constraint "{tripId:guid}" documents that tripId is a Guid on the
//     wire (uuid string form), consistent with StableIdentities.

namespace RideHailing.TripService.Api;

/// <summary>
/// Intended Trip HTTP routes and methods (documented freeze, not registered).
/// </summary>
public static class TripRoutes
{
    /// <summary>POST /trips — CreateRide (transport shape only).</summary>
    public const string CreateRide = "/trips";

    /// <summary>GET /trips/{tripId:guid} — GetTrip (transport shape only).</summary>
    public const string GetTrip = "/trips/{tripId:guid}";

    /// <summary>POST /trips/{tripId:guid}/cancel — CancelRide (transport shape only).</summary>
    public const string CancelRide = "/trips/{tripId:guid}/cancel";
}
